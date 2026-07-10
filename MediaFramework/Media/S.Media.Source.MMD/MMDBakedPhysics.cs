using System.Security.Cryptography;
using System.Text;

namespace S.Media.Source.MMD;

/// <summary>
/// Pre-rendered ("baked") physics: the simulation run ONCE, offline, deterministically forward on the
/// VMD 30 fps timeline - exactly how MMD produces its reference renders - and stored as
/// PARENT-RELATIVE transforms of every physics-driven bone per frame. Playback then just SAMPLES the
/// bake (slerp between frames, chained onto the live FK parents): perfectly repeatable, seek-exact and
/// immune to render cadence, stalls and resets - the live solver's entire failure surface. This is
/// what makes the skirt read "heavy" and the hair "wavy" the way the MMD reference videos do: those
/// are offline forward simulations too, never a real-time solve.
/// </summary>
public sealed class MMDBakedPhysics
{
    /// <summary>Bump when the solver or the bake format changes - invalidates disk caches.</summary>
    public const int Version = 5; // v5: real Bullet 3.25 solver (replaces the custom sequential-impulse one)

    private const string Magic = "MMDBAKE1";
    private const float FramesPerSecond = (float)VMDDocument.FramesPerSecond;

    private readonly int[] _slotOfBone;       // bone index → slot (−1 when the bone is not driven)
    private readonly int[] _drivenBones;      // slot → bone index
    private readonly Quaternion[] _rotations; // [frame * slots + slot], relative to the bone's parent
    private readonly Vector3[] _translations;
    private readonly int _frames;

    private MMDBakedPhysics(int boneCount, int[] drivenBones, int frames, Quaternion[] rotations, Vector3[] translations)
    {
        _drivenBones = drivenBones;
        _frames = frames;
        _rotations = rotations;
        _translations = translations;
        _slotOfBone = new int[boneCount];
        Array.Fill(_slotOfBone, -1);
        for (var slot = 0; slot < drivenBones.Length; slot++)
            if ((uint)drivenBones[slot] < (uint)boneCount)
                _slotOfBone[drivenBones[slot]] = slot;
    }

    public int FrameCount => _frames;

    /// <summary>True when this bone's parent-relative transform comes from the bake.</summary>
    public bool DrivesBone(int bone) => (uint)bone < (uint)_slotOfBone.Length && _slotOfBone[bone] >= 0;

    /// <summary>Interpolated parent-relative transform of a driven bone at a 30 fps frame position.</summary>
    public void Sample(int bone, float frame, out Quaternion rotation, out Vector3 translation)
    {
        var slot = _slotOfBone[bone];
        var slots = _drivenBones.Length;
        var clamped = Math.Clamp(frame, 0f, _frames - 1);
        var f0 = (int)clamped;
        var f1 = Math.Min(f0 + 1, _frames - 1);
        var t = clamped - f0;
        var a = f0 * slots + slot;
        var b = f1 * slots + slot;
        if (t <= 0f || a == b)
        {
            rotation = _rotations[a];
            translation = _translations[a];
            return;
        }

        rotation = Quaternion.Normalize(Quaternion.Slerp(_rotations[a], _rotations[b], t));
        translation = Vector3.Lerp(_translations[a], _translations[b], t);
    }

    /// <summary>Frame position for a media time (the caller clamps/loops time itself).</summary>
    public static float FrameOf(TimeSpan time) => (float)(time.TotalSeconds * FramesPerSecond);

    /// <summary>Runs the full offline bake: settle at the first pose (the post-reset fade plus a second
    /// of hang), then roll the motion forward frame by frame recording every physics-driven bone
    /// relative to its parent. Returns null when the model has no dynamic bodies.</summary>
    public static MMDBakedPhysics? Bake(
        PMXDocument model, VMDDocument motion, CancellationToken cancellation = default, Action<float>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(motion);
        using var physics = MMDPhysics.TryCreate(model);
        if (physics is null)
            return null;

        var animator = new MMDAnimator(model, motion);
        var frames = checked((int)motion.LastFrame + 1);
        var driven = new List<int>();
        for (var i = 0; i < model.Bones.Count; i++)
            if (physics.DrivesBone(i))
                driven.Add(i);
        if (driven.Count == 0)
            return null;

        var slots = driven.Count;
        var rotations = new Quaternion[(long)frames * slots is <= int.MaxValue and var total ? (int)total : throw new InvalidOperationException("bake too large")];
        var translations = new Vector3[rotations.Length];

        // Settle: hold the first pose long enough for the post-reset fade to run out and the chains to
        // find their hang - MMD's own play-from-start behaves the same way.
        const float settleSeconds = 1.5f;
        var step = 1f / FramesPerSecond;
        animator.EvaluatePoseForBake(TimeSpan.Zero, physics, -1f);
        for (var s = 0f; s < settleSeconds; s += step)
        {
            cancellation.ThrowIfCancellationRequested();
            animator.EvaluatePoseForBake(TimeSpan.Zero, physics, step);
        }

        for (var f = 0; f < frames; f++)
        {
            cancellation.ThrowIfCancellationRequested();
            animator.EvaluatePoseForBake(TimeSpan.FromSeconds(f / (double)FramesPerSecond), physics, f == 0 ? 0f : step);
            for (var slot = 0; slot < slots; slot++)
            {
                var bone = driven[slot];
                var world = animator.BoneWorldForBake(bone);
                var parent = model.Bones[bone].ParentIndex;
                Matrix4x4 local;
                if (parent >= 0 && parent < model.Bones.Count)
                {
                    Matrix4x4.Invert(animator.BoneWorldForBake(parent), out var parentInverse);
                    local = world * parentInverse; // row-vector: local · parent = world
                }
                else
                {
                    local = world;
                }

                var index = f * slots + slot;
                rotations[index] = Quaternion.Normalize(
                    Quaternion.CreateFromRotationMatrix(local with { Translation = Vector3.Zero }));
                translations[index] = local.Translation;
            }

            if ((f & 63) == 0)
                progress?.Invoke(f / (float)frames);
        }

        // Optional temporal low-pass on the baked transforms. This was a band-aid for the OLD custom
        // solver, which rigidly transmitted the fast dance's head-shake down the spring-locked chains.
        // Real Bullet (with the additional-damping the file authors rely on) settles that inertia itself,
        // so smoothing is OFF by default (SmoothingPasses = 0) - the bake is the raw Bullet result, exactly
        // like MMD's own render. The kernel is retained (probe/opt-in only) behind the field.
        SmoothTemporally(rotations, translations, slots, frames, SmoothingPasses);

        progress?.Invoke(1f);
        return new MMDBakedPhysics(model.Bones.Count, [.. driven], frames, rotations, translations);
    }

    /// <summary>Passes of the [0.25, 0.5, 0.25] temporal kernel applied to the baked transforms (0 = off).
    /// Internal-settable so the jitter probe can sweep it; 3 was chosen by the probe - it cuts the worst
    /// hair-tip frame-to-frame angular jerk ~5× (0.50→0.10 rad) with ZERO phase lag (symmetric kernel over
    /// an offline bake), so fast motion survives while the transmitted head-shake twitch is smoothed out.</summary>
    internal static int SmoothingPasses = 0;

    internal static void SmoothTemporally(Quaternion[] rotations, Vector3[] translations, int slots, int frames, int passes)
    {
        if (passes <= 0 || frames < 3)
            return;

        var smoothedR = new Quaternion[rotations.Length];
        var smoothedT = new Vector3[translations.Length];
        for (var pass = 0; pass < passes; pass++)
        {
            for (var slot = 0; slot < slots; slot++)
            {
                for (var f = 0; f < frames; f++)
                {
                    var index = f * slots + slot;
                    if (f == 0 || f == frames - 1)
                    {
                        smoothedR[index] = rotations[index]; // endpoints unchanged (no future/past to blend)
                        smoothedT[index] = translations[index];
                        continue;
                    }

                    var prev = (f - 1) * slots + slot;
                    var next = (f + 1) * slots + slot;
                    // Quaternion 3-tap via double nlerp (hemisphere-aligned): centre weight 0.5, neighbours 0.25.
                    var half = NlerpAligned(rotations[prev], rotations[next], 0.5f);          // 0.25 : 0.25
                    smoothedR[index] = NlerpAligned(rotations[index], half, 0.5f);             // → 0.5 : 0.5
                    smoothedT[index] = translations[index] * 0.5f
                                       + (translations[prev] + translations[next]) * 0.25f;
                }
            }

            Array.Copy(smoothedR, rotations, rotations.Length);
            Array.Copy(smoothedT, translations, translations.Length);
        }
    }

    private static Quaternion NlerpAligned(Quaternion a, Quaternion b, float t)
    {
        if (Quaternion.Dot(a, b) < 0f)
            b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W); // shortest-arc hemisphere
        return Quaternion.Normalize(Quaternion.Lerp(a, b, t));
    }

    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes(Magic));
        w.Write(Version);
        w.Write(_slotOfBone.Length);
        w.Write(_drivenBones.Length);
        w.Write(_frames);
        foreach (var bone in _drivenBones)
            w.Write(bone);
        for (var i = 0; i < _rotations.Length; i++)
        {
            w.Write(_rotations[i].X); w.Write(_rotations[i].Y); w.Write(_rotations[i].Z); w.Write(_rotations[i].W);
            w.Write(_translations[i].X); w.Write(_translations[i].Y); w.Write(_translations[i].Z);
        }
    }

    /// <summary>Loads a bake, or null when the stream is not a compatible bake for this model.</summary>
    public static MMDBakedPhysics? TryLoad(Stream stream, PMXDocument model)
    {
        ArgumentNullException.ThrowIfNull(model);
        try
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (!r.ReadBytes(8).AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
                return null;
            if (r.ReadInt32() != Version)
                return null;
            var boneCount = r.ReadInt32();
            if (boneCount != model.Bones.Count)
                return null;
            var slots = r.ReadInt32();
            var frames = r.ReadInt32();
            if (slots is <= 0 or > 4096 || frames is <= 0 or > 2_000_000)
                return null;

            var driven = new int[slots];
            for (var i = 0; i < slots; i++)
            {
                driven[i] = r.ReadInt32();
                if ((uint)driven[i] >= (uint)boneCount)
                    return null;
            }

            var count = checked(frames * slots);
            var rotations = new Quaternion[count];
            var translations = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                rotations[i] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                translations[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }

            return new MMDBakedPhysics(boneCount, driven, frames, rotations, translations);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or OverflowException)
        {
            return null;
        }
    }
}

/// <summary>
/// Disk cache + coalesced background baking. First open of a (model, motion) pair starts one
/// background bake and plays with live physics; every later open - the show's actual GO - loads the
/// cached bake instantly (the YouTube reliable-mode pattern: prepare once, never compute on GO).
/// </summary>
/// <remarks>
/// <para><strong>Eviction (MMD-01).</strong> <see cref="Pending"/> holds only <em>in-flight</em> bakes.
/// A completing bake removes its own entry in a continuation, but only when the entry still refers to
/// that same task, so a retry started after it finished is never clobbered. Completed results are not
/// pinned in the static dictionary; the returned <see cref="MMDBakedPhysics"/> (and its arrays) become
/// collectable once callers drop it.</para>
/// <para><strong>Shared lifetime vs. per-caller waiting (MMD-01).</strong> The background bake runs on
/// <see cref="CancellationToken.None"/> - one caller cancelling must not abort the bake other callers
/// joined. <see cref="BakeAsync"/> honours its caller token by awaiting the shared task through
/// <c>WaitAsync(callerToken)</c>: cancelling stops that caller's wait, not the shared work. A faulted or
/// cancelled shared task is treated as retryable (never re-joined).</para>
/// <para><strong>Persistence (MMD-02).</strong> Writes go to a per-bake unique temp file, are flushed to
/// disk, then atomically moved into place. A corrupt/incompatible cache found on read is deleted and
/// rebaked; a storage failure while writing is reported (logged) and best-effort cleaned up, but the valid
/// in-memory bake is still returned so playback is never blocked by a caching problem.</para>
/// </remarks>
public static class MMDPhysicsBakeCache
{
    private static readonly Dictionary<string, Task<MMDBakedPhysics?>> Pending = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    public static string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mfplayer", "mmd-bake");

    /// <summary>Test/diagnostic hook: number of bakes currently in flight (evicted on completion).</summary>
    internal static int PendingBakeCount
    {
        get { lock (Gate) return Pending.Count; }
    }

    /// <summary>Returns the cached bake when it exists; otherwise starts (or joins) ONE background
    /// bake for this pair and returns its task so callers can hot-swap when it lands.</summary>
    public static (MMDBakedPhysics? Ready, Task<MMDBakedPhysics?> Pending) LoadOrStart(
        string modelPath, string motionPath, PMXDocument model, VMDDocument motion)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(motion);
        var file = CacheFileFor(modelPath, motionPath);
        if (TryLoadFromDisk(file, model) is { } cached)
            return (cached, Task.FromResult<MMDBakedPhysics?>(cached));

        lock (Gate)
        {
            if (Pending.TryGetValue(file, out var running) && !running.IsCompleted)
                return (null, running);

            return (null, StartBakeLocked(file, model, motion, progress: null));
        }
    }

    /// <summary>True when a finished bake for this (model, motion) pair is already on disk.</summary>
    public static bool IsCached(string modelPath, string motionPath) =>
        File.Exists(CacheFileFor(modelPath, motionPath));

    /// <summary>
    /// Explicit user-triggered bake (the dialog's "bake now" button). Joins the running background
    /// bake when one exists (its progress isn't observable - callers show indeterminate); otherwise
    /// starts a bake that reports 0..1 progress. Returns the cached bake immediately when present.
    /// The <paramref name="cancellation"/> token cancels this caller's <em>wait</em>, not the shared bake.
    /// </summary>
    public static Task<MMDBakedPhysics?> BakeAsync(
        string modelPath, string motionPath, PMXDocument model, VMDDocument motion,
        Action<float>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(motion);
        var file = CacheFileFor(modelPath, motionPath);
        if (TryLoadFromDisk(file, model) is { } cached)
        {
            progress?.Invoke(1f);
            return Task.FromResult<MMDBakedPhysics?>(cached);
        }

        Task<MMDBakedPhysics?> shared;
        lock (Gate)
        {
            shared = Pending.TryGetValue(file, out var running) && !running.IsCompleted
                ? running
                : StartBakeLocked(file, model, motion, progress);
        }

        // Per-caller waiting: cancelling stops THIS await, the shared bake keeps running for other joiners.
        return cancellation.CanBeCanceled ? shared.WaitAsync(cancellation) : shared;
    }

    /// <summary>Reads a bake from disk. Returns null when absent or unreadable; deletes an existing-but-
    /// incompatible/corrupt cache so the next open rebakes cleanly (MMD-02: bad cache → delete + rebake).</summary>
    private static MMDBakedPhysics? TryLoadFromDisk(string file, PMXDocument model)
    {
        if (!File.Exists(file))
            return null;
        try
        {
            using (var stream = File.OpenRead(file))
            {
                if (MMDBakedPhysics.TryLoad(stream, model) is { } cached)
                    return cached;
            }
        }
        catch (IOException)
        {
            return null; // storage/read unavailable - fall through to (re)bake, don't delete
        }

        // File exists but is not a valid bake for this model (bad magic / version / bone-count mismatch).
        TryDeleteQuietly(file);
        return null;
    }

    /// <summary>Starts a background bake, registers it in <see cref="Pending"/>, and arranges self-eviction
    /// on completion. Caller holds <see cref="Gate"/>.</summary>
    private static Task<MMDBakedPhysics?> StartBakeLocked(
        string file, PMXDocument model, VMDDocument motion, Action<float>? progress)
    {
        // Shared bake lifetime is independent of any caller token (MMD-01).
        var task = Task.Run(() => RunBake(file, model, motion, progress), CancellationToken.None);
        Pending[file] = task;
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var key = (string)state!;
                lock (Gate)
                {
                    // Evict ONLY if the entry still refers to the task that just completed - a retry may have
                    // already replaced it (MMD-01: cancelled/faulted work is retryable).
                    if (Pending.TryGetValue(key, out var current) && ReferenceEquals(current, completed))
                        Pending.Remove(key);
                }
            },
            file,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private static MMDBakedPhysics? RunBake(string file, PMXDocument model, VMDDocument motion, Action<float>? progress)
    {
        var baked = MMDBakedPhysics.Bake(model, motion, CancellationToken.None, progress);
        if (baked is not null)
            TryPersist(file, baked);
        return baked;
    }

    /// <summary>Persists a completed bake: unique temp file → flush → atomic move. A storage failure is
    /// logged and the temp cleaned up, but never propagated - the in-memory bake stays usable (MMD-02).</summary>
    private static void TryPersist(string file, MMDBakedPhysics baked)
    {
        string? temp = null;
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            temp = file + "." + Guid.NewGuid().ToString("N") + ".partial";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                baked.Save(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, file, overwrite: true);
            temp = null; // moved - nothing to clean up
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Storage unavailable: report it, but the caller still gets the valid in-memory bake.
            S.Media.Core.Diagnostics.MediaDiagnostics.LogWarning(
                $"MMDPhysicsBakeCache: could not persist bake to '{file}' ({ex.GetType().Name}: {ex.Message}); " +
                "playback continues from the in-memory bake, cache will be recomputed next time.");
        }
        finally
        {
            if (temp is not null)
                TryDeleteQuietly(temp);
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort cleanup
        }
    }

    private static string CacheFileFor(string modelPath, string motionPath)
    {
        static string Stamp(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : path;
        }

        var key = $"{Stamp(modelPath)}||{Stamp(motionPath)}||v{MMDBakedPhysics.Version}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return Path.Combine(CacheDirectory, hash + ".mmdbake");
    }
}
