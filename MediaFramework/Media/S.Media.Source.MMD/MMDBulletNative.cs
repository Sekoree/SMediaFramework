using System.Runtime.InteropServices;

namespace S.Media.Source.MMD;

/// <summary>
/// Direct P/Invoke binding for <c>libmmd_bullet</c> - a compact C ABI over real Bullet 3.25 that
/// reproduces MikuMikuDance's physics exactly as babylon-mmd's Bullet runtime does (see the native
/// <c>MediaFramework/Native/mmd_bullet</c> shim). Handles are opaque <see cref="nint"/> world pointers;
/// bodies/constraints are integer handles owned by the world. Transforms are 16-float column-major
/// matrices - bit-identical to <see cref="System.Numerics.Matrix4x4"/>'s in-memory layout, so no
/// transpose is needed at the boundary. <see cref="MMDPhysics"/> is the managed surface; this is the raw ABI.
/// </summary>
internal static unsafe partial class MMDBulletNative
{
    private const string Library = "mmd_bullet";

    /// <summary>ABI version this binding was generated against. MUST equal the shim's <c>MMD_ABI_VERSION</c>
    /// (see <c>mmd_bullet.h</c>) - bumped together on any breaking change to the signatures below (MMD-03).</summary>
    public const uint AbiVersion = 1;

    private static readonly Lazy<bool> LazyAvailable = new(ProbeAvailable);

    /// <summary>True when the native shim is present AND reports an ABI version matching this binding (MMD-03).
    /// Probed once and cached. A missing library, a pre-versioning shim (no <c>mmd_abi_version</c> export), a
    /// wrong-architecture library, or a version mismatch all read as unavailable - callers then run without
    /// physics rather than mis-calling an incompatible ABI across the boundary (which could corrupt memory).</summary>
    public static bool IsAvailable => LazyAvailable.Value;

    private static bool ProbeAvailable()
    {
        try
        {
            return NativeAbiVersion() == AbiVersion;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [LibraryImport(Library, EntryPoint = "mmd_abi_version")]
    public static partial uint NativeAbiVersion();

    // Shape types (match PMXRigidShape / native MMD_SHAPE_*).
    public const int ShapeSphere = 0;
    public const int ShapeBox = 1;
    public const int ShapeCapsule = 2;
    public const int ShapeStaticPlane = 3;

    // Motion types (match native MMD_MOTION_*).
    public const int MotionDynamic = 0;
    public const int MotionStatic = 1;
    public const int MotionKinematic = 2;

    [LibraryImport(Library, EntryPoint = "mmd_world_create")]
    public static partial nint WorldCreate(float gravityX, float gravityY, float gravityZ);

    [LibraryImport(Library, EntryPoint = "mmd_world_destroy")]
    public static partial void WorldDestroy(nint world);

    [LibraryImport(Library, EntryPoint = "mmd_world_add_body")]
    public static partial int WorldAddBody(
        nint world,
        int shapeType, float sizeX, float sizeY, float sizeZ,
        float* transform16,
        int motionType,
        float mass, float linearDamping, float angularDamping, float friction, float restitution,
        ushort collisionGroup, ushort collisionMask,
        int additionalDamping, int noContactResponse, int disableDeactivation);

    [LibraryImport(Library, EntryPoint = "mmd_world_add_spring_constraint")]
    public static partial void WorldAddSpringConstraint(
        nint world, int bodyA, int bodyB,
        float* frameA16, float* frameB16,
        float* linearLower3, float* linearUpper3,
        float* angularLower3, float* angularUpper3,
        float* springPosition3, float* springRotation3);

    [LibraryImport(Library, EntryPoint = "mmd_body_set_kinematic_transform")]
    public static partial void BodySetKinematicTransform(nint world, int body, float* transform16);

    [LibraryImport(Library, EntryPoint = "mmd_body_reset")]
    public static partial void BodyReset(nint world, int body, float* transform16);

    [LibraryImport(Library, EntryPoint = "mmd_body_set_kinematic")]
    public static partial void BodySetKinematic(nint world, int body, int isKinematic);

    [LibraryImport(Library, EntryPoint = "mmd_body_get_transform")]
    public static partial void BodyGetTransform(nint world, int body, float* out16);

    [LibraryImport(Library, EntryPoint = "mmd_world_step")]
    public static partial void WorldStep(nint world, float timeStep, int maxSubSteps, float fixedTimeStep);
}
