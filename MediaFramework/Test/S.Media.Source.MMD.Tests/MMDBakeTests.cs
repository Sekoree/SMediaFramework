using System.Numerics;
using S.Media.Source.MMD;
using Xunit;

namespace S.Media.Source.MMD.Tests;

/// <summary>Pre-baked physics: the offline forward simulation must be deterministic, survive the disk
/// round-trip, and make the played-back pose a pure function of time (seek-independent).</summary>
public sealed class MMDBakeTests
{
    private static PMXBone Bone(string name, Vector3 position, int parent) =>
        new(name, name, position, parent,
            AppendRotation: false, AppendTranslation: false, AppendParentIndex: -1, AppendRatio: 0f,
            IsIk: false, IkTargetIndex: -1, IkLoopCount: 0, IkLimitRadians: 0f, IkLinks: []);

    /// <summary>Kinematic anchor with a dynamic pendulum arm, driven by a 2-second root motion.</summary>
    private static (PMXDocument Model, VMDDocument Motion) PendulumScene()
    {
        var model = new PMXDocument
        {
            Version = 2.0f,
            ModelName = "bake",
            ModelNameEnglish = "bake",
            Vertices = [new PMXVertex(new Vector3(1, 10, 0), Vector3.UnitY, Vector2.Zero, 1, -1, -1, -1, 1f, 0f, 0f, 0f)],
            Indices = [],
            Textures = [],
            Materials = [],
            Bones =
            [
                Bone("root", new Vector3(0, 10, 0), parent: -1),
                Bone("tip", new Vector3(1, 10, 0), parent: 0),
            ],
            Morphs = [],
            RigidBodies =
            [
                new PMXRigidBody("anchor", 0, Group: 0, CollisionMask: 0, PMXRigidShape.Sphere,
                    new Vector3(0.1f, 0, 0), new Vector3(0, 10, 0), Vector3.Zero,
                    1f, 0.5f, 0.5f, 0f, 0.5f, PMXPhysicsMode.FollowBone),
                new PMXRigidBody("swing", 1, Group: 1, CollisionMask: 0, PMXRigidShape.Sphere,
                    new Vector3(0.1f, 0, 0), new Vector3(1, 10, 0), Vector3.Zero,
                    1f, 0.5f, 0.5f, 0f, 0.5f, PMXPhysicsMode.Physics),
            ],
            Joints =
            [
                new PMXJoint("free", Type: 0, RigidBodyA: 0, RigidBodyB: 1,
                    new Vector3(0, 10, 0), Vector3.Zero,
                    Vector3.Zero, Vector3.Zero,
                    new Vector3(-MathF.PI), new Vector3(MathF.PI),
                    Vector3.Zero, Vector3.Zero),
            ],
        };

        var motion = new VMDDocument
        {
            ModelName = "bake",
            BoneTracks = new Dictionary<string, IReadOnlyList<VMDBoneFrame>>(StringComparer.Ordinal)
            {
                ["root"] =
                [
                    new VMDBoneFrame(0, Vector3.Zero, Quaternion.Identity,
                        20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107),
                    new VMDBoneFrame(60, new Vector3(3, 0, 0), Quaternion.Identity,
                        20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107),
                ],
            },
            MorphTracks = new Dictionary<string, IReadOnlyList<VMDMorphFrame>>(StringComparer.Ordinal),
            CameraTrack = [],
            LastFrame = 60,
        };
        return (model, motion);
    }

    [Fact]
    public void Bake_IsDeterministic_AndRoundTripsThroughSaveLoad()
    {
        var (model, motion) = PendulumScene();
        var first = MMDBakedPhysics.Bake(model, motion)!;
        var second = MMDBakedPhysics.Bake(model, motion)!;
        Assert.NotNull(first);
        Assert.True(first.DrivesBone(1));
        Assert.False(first.DrivesBone(0));

        using var stream = new MemoryStream();
        first.Save(stream);
        stream.Position = 0;
        var loaded = MMDBakedPhysics.TryLoad(stream, model);
        Assert.NotNull(loaded);

        for (var frame = 0f; frame <= 60f; frame += 7.3f)
        {
            first.Sample(1, frame, out var r1, out var t1);
            second.Sample(1, frame, out var r2, out var t2);
            loaded.Sample(1, frame, out var r3, out var t3);
            Assert.True(MathF.Abs(Quaternion.Dot(r1, r2)) > 0.999999f && Vector3.Distance(t1, t2) < 1e-6f,
                $"bake not deterministic at frame {frame}");
            Assert.True(MathF.Abs(Quaternion.Dot(r1, r3)) > 0.999999f && Vector3.Distance(t1, t3) < 1e-6f,
                $"disk round-trip changed the bake at frame {frame}");
        }
    }

    [Fact]
    public void BakedPlayback_IsSeekIndependent_AndTracksTheMotion()
    {
        var (model, motion) = PendulumScene();
        var baked = MMDBakedPhysics.Bake(model, motion)!;
        var animator = new MMDAnimator(model, motion);
        var positions = new Vector3[model.Vertices.Count];

        // Reference pose at t=1.5s evaluated cold.
        animator.Evaluate(TimeSpan.FromSeconds(1.5), positions, null, baked);
        var reference = animator.TryGetBoneWorldPosition("tip")!.Value;
        Assert.True(float.IsFinite(reference.X));
        // The arm hangs below a root that moved with the motion: x tracks root (0→3 over 2 s).
        var root = animator.TryGetBoneWorldPosition("root")!.Value;
        Assert.True(Vector3.Distance(reference, root) < 1.5f, $"arm detached: tip={reference} root={root}");

        // Evaluate a scattered seek pattern, then the same time again — identical pose, no state.
        foreach (var seconds in new[] { 0.2, 1.9, 0.7, 1.2, 0.0 })
            animator.Evaluate(TimeSpan.FromSeconds(seconds), positions, null, baked);
        animator.Evaluate(TimeSpan.FromSeconds(1.5), positions, null, baked);
        var again = animator.TryGetBoneWorldPosition("tip")!.Value;
        Assert.True(Vector3.Distance(reference, again) < 1e-5f,
            $"baked playback is not seek-independent: {reference} vs {again}");
    }

    [Fact]
    public async Task BakeCache_EvictsCompletedBakes_AndSurvivesCallerCancellation()
    {
        // MMD-01: a caller cancelling its wait must not poison the shared bake, and completed bakes must
        // not linger forever in the in-flight table (so their results stay collectable).
        var (model, motion) = PendulumScene();
        var directory = Path.Combine(Path.GetTempPath(), "mmd-bake-test-" + Guid.NewGuid().ToString("N"));
        var previous = MMDPhysicsBakeCache.CacheDirectory;
        MMDPhysicsBakeCache.CacheDirectory = directory;
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var cancelled = MMDPhysicsBakeCache.BakeAsync("m.pmx", "v.vmd", model, motion, cancellation: cts.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

            // Retry with no cancellation still yields a valid bake (the shared work was never poisoned).
            var baked = await MMDPhysicsBakeCache.BakeAsync("m.pmx", "v.vmd", model, motion);
            Assert.NotNull(baked);
            Assert.True(baked!.DrivesBone(1));

            // The completed bake is evicted from the in-flight table (result stays collectable).
            SpinWaitUntil(() => MMDPhysicsBakeCache.PendingBakeCount == 0, TimeSpan.FromSeconds(10));
            Assert.Equal(0, MMDPhysicsBakeCache.PendingBakeCount);
        }
        finally
        {
            MMDPhysicsBakeCache.CacheDirectory = previous;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task BakeCache_DeletesCorruptCache_AndRebakes()
    {
        // MMD-02: an unreadable/incompatible cache file is deleted and rebaked, not returned or duplicated.
        var (model, motion) = PendulumScene();
        var directory = Path.Combine(Path.GetTempPath(), "mmd-bake-test-" + Guid.NewGuid().ToString("N"));
        var previous = MMDPhysicsBakeCache.CacheDirectory;
        MMDPhysicsBakeCache.CacheDirectory = directory;
        try
        {
            var baked = await MMDPhysicsBakeCache.BakeAsync("m.pmx", "v.vmd", model, motion);
            Assert.NotNull(baked);
            var file = Assert.Single(Directory.GetFiles(directory, "*.mmdbake"));
            File.WriteAllText(file, "not a valid bake");

            var (ready, pending) = MMDPhysicsBakeCache.LoadOrStart("m.pmx", "v.vmd", model, motion);
            Assert.Null(ready); // corrupt cache is not offered as ready
            var rebaked = await pending;
            Assert.NotNull(rebaked);
            Assert.True(rebaked!.DrivesBone(1));
            Assert.Single(Directory.GetFiles(directory, "*.mmdbake")); // replaced atomically, not duplicated
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));  // no temp files left behind
        }
        finally
        {
            MMDPhysicsBakeCache.CacheDirectory = previous;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static void SpinWaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!condition() && Environment.TickCount64 < deadline)
            Thread.Sleep(20);
    }

    [Fact]
    public async Task BakeCache_WritesOnce_ThenLoadsInstantly()
    {
        var (model, motion) = PendulumScene();
        var directory = Path.Combine(Path.GetTempPath(), "mmd-bake-test-" + Guid.NewGuid().ToString("N"));
        var previous = MMDPhysicsBakeCache.CacheDirectory;
        MMDPhysicsBakeCache.CacheDirectory = directory;
        try
        {
            // Paths only key the cache file name — they need not exist.
            var (ready, pending) = MMDPhysicsBakeCache.LoadOrStart("model.pmx", "motion.vmd", model, motion);
            Assert.Null(ready);
            var baked = await pending;
            Assert.NotNull(baked);
            Assert.Single(Directory.GetFiles(directory, "*.mmdbake"));

            var (second, _) = MMDPhysicsBakeCache.LoadOrStart("model.pmx", "motion.vmd", model, motion);
            Assert.NotNull(second);
            Assert.True(second.DrivesBone(1));
        }
        finally
        {
            MMDPhysicsBakeCache.CacheDirectory = previous;
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
