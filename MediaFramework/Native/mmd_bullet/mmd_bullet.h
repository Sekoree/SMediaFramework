// mmd_bullet — a compact C ABI over real Bullet 3.25 that reproduces MikuMikuDance's physics exactly
// as babylon-mmd's Bullet runtime does. The managed MMDPhysics wrapper (S.Media.Source.MMD) P/Invokes
// this so the file's Bullet-tuned rigid bodies, 6-DOF spring joints and collision groups mean precisely
// what MMD's own Bullet world means. One world per model; bodies/constraints are integer handles owned
// by the world (freed with it). Transforms are 16-float column-major (OpenGL) matrices — the same layout
// System.Numerics.Matrix4x4 has in memory (row-major row-vector == column-major column-vector).
#ifndef MMD_BULLET_H
#define MMD_BULLET_H

#include <stdint.h>

// Export the C ABI from the shared library. GCC/Clang export public symbols by default, but MSVC exports
// NOTHING from a DLL unless annotated — without this the managed [LibraryImport] finds the DLL but fails
// with EntryPointNotFoundException. The .cpp defines each function `extern "C"` and includes this header,
// so the dllexport on the declaration propagates to the definition.
#if defined(_WIN32)
  #define MMD_API __declspec(dllexport)
#else
  #define MMD_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Shape types (match PMX rigid-body shapeType).
enum { MMD_SHAPE_SPHERE = 0, MMD_SHAPE_BOX = 1, MMD_SHAPE_CAPSULE = 2, MMD_SHAPE_STATIC_PLANE = 3 };

// Motion types (match babylon-mmd MotionType).
enum { MMD_MOTION_DYNAMIC = 0, MMD_MOTION_STATIC = 1, MMD_MOTION_KINEMATIC = 2 };

typedef struct mmd_world mmd_world;

// Create/destroy a physics world with the given gravity (MMD default: 0,-98,0).
MMD_API mmd_world* mmd_world_create(float gx, float gy, float gz);
MMD_API void       mmd_world_destroy(mmd_world* world);

// Add a rigid body. Returns its integer handle (>=0), or -1 on failure.
// transform16 is the body's initial world transform (shape origin), column-major.
MMD_API int mmd_world_add_body(
    mmd_world* world,
    int shape_type, float size_x, float size_y, float size_z,
    const float* transform16,
    int motion_type,
    float mass, float linear_damping, float angular_damping, float friction, float restitution,
    uint16_t collision_group, uint16_t collision_mask,
    int additional_damping, int no_contact_response, int disable_deactivation);

// Add a Generic6DofSpring constraint between two bodies (MMD Spring6Dof joint). Frames are the joint
// frame expressed in each body's local space (column-major). Limits/springs are per-axis (x,y,z).
// Angular springs are always enabled (MMD convention); linear springs enable only where stiffness != 0.
MMD_API void mmd_world_add_spring_constraint(
    mmd_world* world, int body_a, int body_b,
    const float* frame_a16, const float* frame_b16,
    const float* linear_lower3, const float* linear_upper3,
    const float* angular_lower3, const float* angular_upper3,
    const float* spring_position3, const float* spring_rotation3);

// Drive a body's kinematic target (writes its motion state). For FollowBone bodies and dynamic bodies
// that are temporarily kinematic (physics disabled / warm-up), call this every frame before stepping.
MMD_API void mmd_body_set_kinematic_transform(mmd_world* world, int body, const float* transform16);

// Hard-reset a body onto a world transform and zero its velocities (seek / re-base).
MMD_API void mmd_body_reset(mmd_world* world, int body, const float* transform16);

// Toggle a dynamic body between kinematic-follow and free-dynamic (VMD physics on/off, and the
// one-frame warm-up after a reset). No-op for authored-kinematic (FollowBone) bodies.
MMD_API void mmd_body_set_kinematic(mmd_world* world, int body, int is_kinematic);

// Read a body's current world transform (column-major) into out16.
MMD_API void mmd_body_get_transform(mmd_world* world, int body, float* out16);

// Advance the simulation. Reference MMD cadence: max_sub_steps=5, fixed_time_step=1/60.
MMD_API void mmd_world_step(mmd_world* world, float time_step, int max_sub_steps, float fixed_time_step);

#ifdef __cplusplus
}
#endif

#endif // MMD_BULLET_H
