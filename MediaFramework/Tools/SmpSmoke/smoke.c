/*
 * smoke.c — the Phase-7 outbound C-ABI gate. A pure C client of s_media_player.h: it drives the AOT-published
 * s_media_player shared library through the whole lifecycle, proving a host in *any* language can run a show with
 * no managed code of its own.
 *
 *   ./smoke                 → an empty ShowDocument (lifecycle only: open/load/go/query/close).
 *   ./smoke <media-path>    → a one-cue show whose clip plays <media-path> (e.g. an ffmpeg-generated tone) — also
 *                             exercises the cue-list query + a real GO that opens the clip.
 *
 * Beyond the happy path it treats the header as a spec (ABI-03): the documented error codes (MFP_ERR_LOAD_FAILED,
 * MFP_ERR_NOT_FOUND), thread-local last-error semantics, per-session concurrent call leasing (ABI-02), and
 * repeated init/shutdown are all asserted here from C.
 *
 * Build (Linux): gcc smoke.c -I<S.Media.Interop/include> <publish>/s_media_player.so -o smoke -lpthread -Wl,-rpath,<publish>
 */
#include "s_media_player.h"
#include <stdio.h>
#include <string.h>
#include <stdint.h>
#include <pthread.h>
#include <stdatomic.h>

static const char* EMPTY_SHOW =
    "{\"Version\":1,\"Cues\":[],\"Clips\":[],\"Compositions\":[],\"Outputs\":[],\"Routes\":[],\"Devices\":[]}";

#define CHECK(cond, what) \
    do { if (!(cond)) { fprintf(stderr, "FAIL: %s (last_error=\"%s\")\n", (what), mfp_last_error()); return 1; } } while (0)

/* ABI-01/ABI-03: the documented error + last-error contract, on a throwaway session so the main run is undisturbed. */
static int run_error_contract_checks(void) {
    mfp_session s = mfp_session_create();
    CHECK(s != NULL, "error-contract: create");

    /* Malformed JSON is a clean MFP_ERR_LOAD_FAILED with a diagnostic — not a crash, not a generic error. The
     * live document is untouched (validate-then-stage), so the session stays usable afterwards. */
    CHECK(mfp_session_load_show(s, "{ this is not valid json ]") == MFP_ERR_LOAD_FAILED, "malformed json -> LOAD_FAILED");
    CHECK(strlen(mfp_last_error()) > 0, "a failed load sets a thread-local last-error message");

    CHECK(mfp_session_load_show(s, EMPTY_SHOW) == MFP_OK, "empty show still loads after a failed load");
    CHECK(strlen(mfp_last_error()) == 0, "a successful call clears the last-error");

    /* Unknown cue id / transport group are MFP_ERR_NOT_FOUND (the header's dedicated code), not a generic error. */
    CHECK(mfp_session_fire_cue(s, "no-such-cue") == MFP_ERR_NOT_FOUND, "unknown cue id -> NOT_FOUND");
    CHECK(mfp_session_state(s, "no-such-group") == MFP_ERR_NOT_FOUND, "unknown transport group -> NOT_FOUND");

    mfp_session_destroy(s);
    return 0;
}

/* ABI-02/ABI-03: many concurrent in-flight calls on one session. The per-session call lease must let readers run
 * at once (ActiveCalls > 1) without corrupting the handle table; destroy then drains them cleanly. All workers
 * join before destroy, so the check is deterministic (no timing-dependent race that could flake CI). */
#define CC_THREADS 4
#define CC_ITERS 4000
typedef struct { mfp_session s; int rc; } cc_arg;

static void* cc_worker(void* p) {
    cc_arg* a = (cc_arg*)p;
    a->rc = 0;
    for (int i = 0; i < CC_ITERS; i++) {
        if (mfp_session_state(a->s, NULL) < 0 || mfp_session_position_ticks(a->s, NULL) < 0) { a->rc = 1; return NULL; }
    }
    return NULL;
}

static int run_concurrency_check(void) {
    mfp_session s = mfp_session_create();
    CHECK(s != NULL, "concurrency: create");
    CHECK(mfp_session_load_show(s, EMPTY_SHOW) == MFP_OK, "concurrency: load");

    pthread_t th[CC_THREADS];
    cc_arg args[CC_THREADS];
    for (int i = 0; i < CC_THREADS; i++) {
        args[i].s = s;
        args[i].rc = -1;
        CHECK(pthread_create(&th[i], NULL, cc_worker, &args[i]) == 0, "spawn concurrent reader");
    }
    for (int i = 0; i < CC_THREADS; i++) {
        pthread_join(th[i], NULL);
        CHECK(args[i].rc == 0, "concurrent reader saw only valid state under the call lease");
    }

    mfp_session_destroy(s); /* leases already drained (all readers joined) */
    return 0;
}

/* ABI-02: destroy must actually race calls that are still issuing leases. Workers accept the point at which
 * destroy removes the handle (INVALID_HANDLE), but no other failure and no crash/use-after-free is legal. */
typedef struct { mfp_session s; atomic_int* entered; atomic_int* stop; int rc; } destroy_arg;

static void* destroy_worker(void* p) {
    destroy_arg* a = (destroy_arg*)p;
    a->rc = 0;
    atomic_fetch_add(a->entered, 1);
    while (!atomic_load(a->stop)) {
        int state = mfp_session_state(a->s, NULL);
        if (state < 0 && state != MFP_ERR_INVALID_HANDLE) { a->rc = 1; return NULL; }
        if (state == MFP_ERR_INVALID_HANDLE) return NULL;
    }
    return NULL;
}

static int run_destroy_race_check(void) {
    mfp_session s = mfp_session_create();
    CHECK(s != NULL, "destroy-race: create");
    CHECK(mfp_session_load_show(s, EMPTY_SHOW) == MFP_OK, "destroy-race: load");

    atomic_int entered = 0;
    atomic_int stop = 0;
    pthread_t th[CC_THREADS];
    destroy_arg args[CC_THREADS];
    for (int i = 0; i < CC_THREADS; i++) {
        args[i] = (destroy_arg){ s, &entered, &stop, -1 };
        CHECK(pthread_create(&th[i], NULL, destroy_worker, &args[i]) == 0, "destroy-race: spawn");
    }
    while (atomic_load(&entered) != CC_THREADS) { }
    mfp_session_destroy(s); /* races active/next query leases and must drain safely */
    atomic_store(&stop, 1);
    for (int i = 0; i < CC_THREADS; i++) {
        pthread_join(th[i], NULL);
        CHECK(args[i].rc == 0, "destroy-race: worker saw an unexpected result");
    }
    return 0;
}

/* ABI-02: shutdown closes the runtime generation atomically with its handle snapshot. Calls already leased
 * drain; subsequent calls report NOT_INITIALIZED, and no creator/query can publish work behind the snapshot. */
static void* shutdown_worker(void* p) {
    destroy_arg* a = (destroy_arg*)p;
    a->rc = 0;
    atomic_fetch_add(a->entered, 1);
    while (!atomic_load(a->stop)) {
        int state = mfp_session_state(a->s, NULL);
        if (state < 0 && state != MFP_ERR_NOT_INITIALIZED) { a->rc = 1; return NULL; }
        if (state == MFP_ERR_NOT_INITIALIZED) return NULL;
    }
    return NULL;
}

static int run_shutdown_race_check(void) {
    mfp_session s = mfp_session_create();
    CHECK(s != NULL, "shutdown-race: create");
    CHECK(mfp_session_load_show(s, EMPTY_SHOW) == MFP_OK, "shutdown-race: load");

    atomic_int entered = 0;
    atomic_int stop = 0;
    pthread_t th[CC_THREADS];
    destroy_arg args[CC_THREADS];
    for (int i = 0; i < CC_THREADS; i++) {
        args[i] = (destroy_arg){ s, &entered, &stop, -1 };
        CHECK(pthread_create(&th[i], NULL, shutdown_worker, &args[i]) == 0, "shutdown-race: spawn");
    }
    while (atomic_load(&entered) != CC_THREADS) { }
    mfp_shutdown();
    atomic_store(&stop, 1);
    for (int i = 0; i < CC_THREADS; i++) {
        pthread_join(th[i], NULL);
        CHECK(args[i].rc == 0, "shutdown-race: worker saw an unexpected result");
    }
    return 0;
}

int main(int argc, char** argv) {
    const char* media = argc > 1 ? argv[1] : NULL;
    char show[2048];
    int expect_cues;

    if (media) {
        snprintf(show, sizeof(show),
            "{\"Version\":1,"
            "\"Cues\":[{\"Id\":\"cue1\",\"Number\":1,\"Label\":\"Tone\"}],"
            "\"Clips\":[{\"CueId\":\"cue1\",\"MediaPath\":\"%s\"}],"
            "\"Compositions\":[],\"Outputs\":[],\"Routes\":[],\"Devices\":[]}", media);
        expect_cues = 1;
    } else {
        snprintf(show, sizeof(show), "%s", EMPTY_SHOW);
        expect_cues = 0;
    }

    CHECK(mfp_initialize() == MFP_OK, "mfp_initialize");
    printf("abi version = %u, mode = %s\n", mfp_abi_version(), media ? "media" : "empty");

    mfp_session s = mfp_session_create();
    CHECK(s != NULL, "mfp_session_create");

    CHECK(mfp_session_load_show(s, show) == MFP_OK, "mfp_session_load_show");

    int cue_count = mfp_session_cue_count(s);
    printf("cue count = %d (expected %d)\n", cue_count, expect_cues);
    CHECK(cue_count == expect_cues, "mfp_session_cue_count");

    if (cue_count > 0) {
        char id[64];
        CHECK(mfp_session_cue_id(s, 0, id, sizeof(id)) == MFP_OK, "mfp_session_cue_id");
        printf("cue[0] id = %s\n", id);
        CHECK(strcmp(id, "cue1") == 0, "cue id matches");
    }

    CHECK(mfp_session_go(s, NULL) == MFP_OK, "mfp_session_go");
    int64_t pos = mfp_session_position_ticks(s, NULL);
    int64_t dur = mfp_session_duration_ticks(s, NULL);
    int state = mfp_session_state(s, NULL);
    printf("after go: position = %lld ticks, duration = %lld ticks, state = %d\n",
           (long long)pos, (long long)dur, state);
    CHECK(pos >= 0, "mfp_session_position_ticks");
    CHECK(dur >= 0, "mfp_session_duration_ticks");
    CHECK(state >= 0, "mfp_session_state");
    if (media)
        CHECK(dur > 0, "media clip reports a duration");

    CHECK(mfp_session_stop(s, NULL) == MFP_OK, "mfp_session_stop");

    /* --- NXT-08 negative-handle gate: bad/stale/double-freed handles must be REJECTED, never crash the
     * process. With the old raw-GCHandle scheme a garbage token was dereferenced and could fault across the
     * unmanaged boundary; the handle table now resolves by lookup and rejects safely. --- */
    {
        mfp_session garbage = (mfp_session)(intptr_t)0xDEADBEEFu;
        CHECK(mfp_session_state(garbage, NULL) < 0, "garbage handle rejected by state (no crash)");
        CHECK(mfp_session_load_show(garbage, EMPTY_SHOW) == MFP_ERR_INVALID_HANDLE, "garbage handle rejected by load");
        CHECK(mfp_session_state(NULL, NULL) < 0, "null handle rejected by state");
        CHECK(mfp_session_go(NULL, NULL) == MFP_ERR_INVALID_HANDLE, "null handle rejected by go");
    }

    mfp_session_destroy(s);
    /* Use-after-destroy and double-destroy are safe errors / no-ops, not crashes or double-frees. */
    CHECK(mfp_session_state(s, NULL) < 0, "use-after-destroy rejected");
    mfp_session_destroy(s); /* double destroy */

    /* ABI-03 conformance extensions — run while the runtime is still initialized. */
    if (run_error_contract_checks() != 0) return 1;
    if (run_concurrency_check() != 0) return 1;
    if (run_destroy_race_check() != 0) return 1;

    /* Invalid-argument paths must release their call lease too. This used to make destroy wait 30 seconds. */
    {
        mfp_session bad_arg = mfp_session_create();
        CHECK(bad_arg != NULL, "bad-arg lease: create");
        CHECK(mfp_session_cue_id(bad_arg, 0, NULL, 0) == MFP_ERR_INVALID_ARG,
              "bad-arg lease: null output buffer rejected");
        mfp_session_destroy(bad_arg);
    }

    if (run_shutdown_race_check() != 0) return 1;
    /* After shutdown the runtime refuses handles but does not crash, and destroy stays valid (no-op). */
    CHECK(mfp_session_state(s, NULL) == MFP_ERR_NOT_INITIALIZED,
          "post-shutdown call reports NOT_INITIALIZED");
    CHECK(mfp_session_create() == NULL, "post-shutdown create rejected");
    mfp_session_destroy(s);

    /* ABI-03: the runtime survives repeated init/shutdown cycles (no one-shot global state left behind). */
    CHECK(mfp_initialize() == MFP_OK, "re-initialize after shutdown");
    {
        mfp_session s2 = mfp_session_create();
        CHECK(s2 != NULL, "create session after re-init");
        CHECK(mfp_session_load_show(s2, EMPTY_SHOW) == MFP_OK, "load show after re-init");
        mfp_session_destroy(s2);
    }
    mfp_shutdown();

    printf("SmpSmoke OK — a C host ran a %s show (load/cue-list/go/query/stop/close) through s_media_player.h; "
           "the error/NOT_FOUND + last-error contract, concurrent per-session call leases, repeated init/shutdown, "
           "and bad/stale/double-freed/post-shutdown handles all behaved without crashing.\n",
           media ? "media" : "empty");
    return 0;
}
