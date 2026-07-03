using System.Security.Cryptography;
using System.Text;

namespace S.Media.Source.MMD;

/// <summary>
/// Pre-rendered ("baked") physics: the simulation run ONCE, offline, deterministically forward on the
/// VMD 30 fps timeline — exactly how MMD produces its reference renders — and stored as
/// PARENT-RELATIVE transforms of every physics-driven bone per frame. Playback then just SAMPLES the
/// bake (slerp between frames, chained onto the live FK parents): perfectly repeatable, seek-exact and
/// immune to render cadence, stalls and resets — the live solver's entire failure surface. This is
/// what makes the skirt read "heavy" and the hair "wavy" the way the MMD reference videos do: those
/// are offline forward simulations too, never a real-time solve.
/// </summary>
public sealed class MmdBakedPhysics
{
    /// <summary>Bump when the solver or the bake format changes — invalidates disk caches.</summary>
    public const int Version = 1;

    private const string Magic = "MMDBAKE1";
    private const float FramesPerSecond = (float)VmdDocument.FramesPerSecond;

    private readonly int[] _slotOfBone;       // bone index → slot (−1 when the bone is not driven)
    private readonly int[] _drivenBones;      // slot → bone index
    private readonly Quaternion[] _rotations; // [frame * slots + slot], relative to the bone's parent
    private readonly Vector3[] _translations;
    private readonly int _frames;

    private MmdBakedPhysics(int boneCount, int[] drivenBones, int frames, Quaternion[] rotations, Vector3[] translations)
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
    public static MmdBakedPhysics? Bake(
        PmxDocument model, VmdDocument motion, CancellationToken cancellation = default, Action<float>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(motion);
        var physics = MmdPhysics.TryCreate(model);
        if (physics is null)
            return null;

        var animator = new MmdAnimator(model, motion);
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
        // find their hang — MMD's own play-from-start behaves the same way.
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

        progress?.Invoke(1f);
        return new MmdBakedPhysics(model.Bones.Count, [.. driven], frames, rotations, translations);
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
    public static MmdBakedPhysics? TryLoad(Stream stream, PmxDocument model)
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

            return new MmdBakedPhysics(boneCount, driven, frames, rotations, translations);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or OverflowException)
        {
            return null;
        }
    }
}

/// <summary>
/// Disk cache + coalesced background baking. First open of a (model, motion) pair starts one
/// background bake and plays with live physics; every later open — the show's actual GO — loads the
/// cached bake instantly (the YouTube reliable-mode pattern: prepare once, never compute on GO).
/// </summary>
public static class MmdPhysicsBakeCache
{
    private static readonly Dictionary<string, Task<MmdBakedPhysics?>> Pending = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    public static string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mfplayer", "mmd-bake");

    /// <summary>Returns the cached bake when it exists; otherwise starts (or joins) ONE background
    /// bake for this pair and returns its task so callers can hot-swap when it lands.</summary>
    public static (MmdBakedPhysics? Ready, Task<MmdBakedPhysics?> Pending) LoadOrStart(
        string modelPath, string motionPath, PmxDocument model, VmdDocument motion)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(motion);
        var file = CacheFileFor(modelPath, motionPath);
        if (File.Exists(file))
        {
            try
            {
                using var stream = File.OpenRead(file);
                if (MmdBakedPhysics.TryLoad(stream, model) is { } cached)
                    return (cached, Task.FromResult<MmdBakedPhysics?>(cached));
            }
            catch (IOException)
            {
                // unreadable cache — fall through to re-bake
            }
        }

        lock (Gate)
        {
            if (Pending.TryGetValue(file, out var running) && !running.IsFaulted)
                return (null, running);

            var task = StartBake(file, model, motion, progress: null, CancellationToken.None);
            Pending[file] = task;
            return (null, task);
        }
    }

    /// <summary>True when a finished bake for this (model, motion) pair is already on disk.</summary>
    public static bool IsCached(string modelPath, string motionPath) =>
        File.Exists(CacheFileFor(modelPath, motionPath));

    /// <summary>
    /// Explicit user-triggered bake (the dialog's "bake now" button). Joins the running background
    /// bake when one exists (its progress isn't observable — callers show indeterminate); otherwise
    /// starts a bake that reports 0..1 progress. Returns the cached bake immediately when present.
    /// </summary>
    public static Task<MmdBakedPhysics?> BakeAsync(
        string modelPath, string motionPath, PmxDocument model, VmdDocument motion,
        Action<float>? progress = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(motion);
        var file = CacheFileFor(modelPath, motionPath);
        if (File.Exists(file))
        {
            try
            {
                using var stream = File.OpenRead(file);
                if (MmdBakedPhysics.TryLoad(stream, model) is { } cached)
                {
                    progress?.Invoke(1f);
                    return Task.FromResult<MmdBakedPhysics?>(cached);
                }
            }
            catch (IOException)
            {
                // unreadable cache — fall through to re-bake
            }
        }

        lock (Gate)
        {
            if (Pending.TryGetValue(file, out var running) && !running.IsFaulted)
                return running;

            var task = StartBake(file, model, motion, progress, cancellation);
            Pending[file] = task;
            return task;
        }
    }

    private static Task<MmdBakedPhysics?> StartBake(
        string file, PmxDocument model, VmdDocument motion, Action<float>? progress, CancellationToken cancellation) =>
        Task.Run(() =>
        {
            var baked = MmdBakedPhysics.Bake(model, motion, cancellation, progress);
            if (baked is not null)
            {
                Directory.CreateDirectory(CacheDirectory);
                var partial = file + ".partial";
                using (var stream = File.Create(partial))
                    baked.Save(stream);
                File.Move(partial, file, overwrite: true);
            }

            return baked;
        }, CancellationToken.None);

    private static string CacheFileFor(string modelPath, string motionPath)
    {
        static string Stamp(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : path;
        }

        var key = $"{Stamp(modelPath)}||{Stamp(motionPath)}||v{MmdBakedPhysics.Version}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return Path.Combine(CacheDirectory, hash + ".mmdbake");
    }
}
