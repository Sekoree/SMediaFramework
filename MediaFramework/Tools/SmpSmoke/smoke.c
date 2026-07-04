/*
 * smoke.c — the Phase-7 outbound C-ABI gate. A pure C client of s_media_player.h: it drives the AOT-published
 * s_media_player shared library through the whole lifecycle, proving a host in *any* language can run a show with
 * no managed code of its own.
 *
 *   ./smoke                 → an empty ShowDocument (lifecycle only: open/load/go/query/close).
 *   ./smoke <media-path>    → a one-cue show whose clip plays <media-path> (e.g. an ffmpeg-generated tone) — also
 *                             exercises the cue-list query + a real GO that opens the clip.
 *
 * Build (Linux): gcc smoke.c -I<S.Media.Interop/include> <publish>/s_media_player.so -o smoke -Wl,-rpath,<publish>
 */
#include "s_media_player.h"
#include <stdio.h>
#include <string.h>
#include <stdint.h>

static const char* EMPTY_SHOW =
    "{\"Version\":1,\"Cues\":[],\"Clips\":[],\"Compositions\":[],\"Outputs\":[],\"Routes\":[],\"Devices\":[]}";

#define CHECK(cond, what) \
    do { if (!(cond)) { fprintf(stderr, "FAIL: %s (last_error=\"%s\")\n", (what), mfp_last_error()); return 1; } } while (0)

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

    mfp_shutdown();
    /* After shutdown the runtime refuses handles but does not crash, and destroy stays valid (no-op). */
    CHECK(mfp_session_state(s, NULL) < 0, "post-shutdown call rejected");
    mfp_session_destroy(s);

    printf("SmpSmoke OK — a C host ran a %s show (load/cue-list/go/query/stop/close) through s_media_player.h, "
           "and bad/stale/double-freed/post-shutdown handles were rejected without crashing.\n",
           media ? "media" : "empty");
    return 0;
}
