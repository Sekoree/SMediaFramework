using S.Media.Source.MMD;
using Xunit;

namespace S.Media.Source.MMD.Tests;

/// <summary>MMD-03: pins the native <c>mmd_bullet</c> shim's ABI contract — the version handshake, the
/// create → add → step → read → destroy lifecycle, and safe rejection of invalid handles/args across the
/// boundary. Requires the native shim staged next to the test binary (build.sh).</summary>
public sealed unsafe class MMDShimContractTests
{
    [Fact]
    public void AbiVersion_NativeMatchesBinding_AndReportsAvailable()
    {
        Assert.True(MMDBulletNative.IsAvailable);
        Assert.Equal(MMDBulletNative.AbiVersion, MMDBulletNative.NativeAbiVersion());
    }

    [Fact]
    public void Lifecycle_CreateAddStepReadDestroy_DrivesABodyUnderGravity()
    {
        var world = MMDBulletNative.WorldCreate(0f, -98f, 0f); // MMD gravity
        Assert.NotEqual(nint.Zero, world);
        try
        {
            var transform = stackalloc float[16];
            transform[0] = transform[5] = transform[10] = transform[15] = 1f; // identity (column-major)

            var body = MMDBulletNative.WorldAddBody(
                world, MMDBulletNative.ShapeSphere, 1f, 1f, 1f, transform,
                MMDBulletNative.MotionDynamic,
                mass: 1f, linearDamping: 0f, angularDamping: 0f, friction: 0.5f, restitution: 0f,
                collisionGroup: 1, collisionMask: 0xFFFF,
                additionalDamping: 0, noContactResponse: 0, disableDeactivation: 1);
            Assert.True(body >= 0);

            for (var i = 0; i < 30; i++)
                MMDBulletNative.WorldStep(world, 1f / 60f, 5, 1f / 60f);

            var read = stackalloc float[16];
            MMDBulletNative.BodyGetTransform(world, body, read);
            for (var i = 0; i < 16; i++)
                Assert.True(float.IsFinite(read[i]), $"transform[{i}] is not finite");
            Assert.True(read[13] < 0f, $"a dynamic body should fall under gravity; Ty={read[13]}"); // column-major Ty
        }
        finally
        {
            MMDBulletNative.WorldDestroy(world);
        }
    }

    [Fact]
    public void InvalidHandlesAndArgs_AreRejectedSafely_WithoutCrashing()
    {
        // Null-world destroy: a no-op (the shim null-checks), so double-free/teardown races are safe.
        MMDBulletNative.WorldDestroy(nint.Zero);

        var world = MMDBulletNative.WorldCreate(0f, -98f, 0f);
        try
        {
            var read = stackalloc float[16];
            for (var i = 0; i < 16; i++) read[i] = 7f;

            // Out-of-range / negative body indices: bounds-checked no-ops that must not touch the output.
            MMDBulletNative.BodyGetTransform(world, 99999, read);
            Assert.Equal(7f, read[0]);
            MMDBulletNative.BodyGetTransform(world, -1, read);
            Assert.Equal(7f, read[0]);

            var t = stackalloc float[16];
            t[0] = t[5] = t[10] = t[15] = 1f;
            MMDBulletNative.BodySetKinematicTransform(world, 99999, t);
            MMDBulletNative.BodyReset(world, -5, t);
            MMDBulletNative.BodySetKinematic(world, 12345, 1);

            // A spring constraint between two out-of-range bodies must not corrupt the world.
            var v3 = stackalloc float[3];
            MMDBulletNative.WorldAddSpringConstraint(world, 100, 200, t, t, v3, v3, v3, v3, v3, v3);

            // The world still steps cleanly after all the rejected calls.
            MMDBulletNative.WorldStep(world, 1f / 60f, 5, 1f / 60f);
        }
        finally
        {
            MMDBulletNative.WorldDestroy(world);
        }
    }
}
