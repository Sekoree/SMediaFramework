# Builds libmmd_bullet.so — the MMD physics shim linked against a trimmed, vendored Bullet 3.25
# (../../../External/bullet3, with babylon-mmd's constraint-fix.patch applied inline). Bullet's aggregate
# *All.cpp files pull in exactly the LinearMath/Collision/Dynamics translation units we need, so there is
# no dependency on Bullet's own CMake targets.
cmake_minimum_required(VERSION 3.16)
project(mmd_bullet CXX)

set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()
if(NOT MSVC)
    set(CMAKE_CXX_FLAGS_RELEASE "-O3 -DNDEBUG")  # MSVC keeps its default /O2 /DNDEBUG (-O3 is a GCC/Clang flag)
endif()

set(BULLET_SRC "${CMAKE_CURRENT_SOURCE_DIR}/../../../External/bullet3/src")

add_library(mmd_bullet SHARED
    mmd_bullet.cpp
    "${BULLET_SRC}/btLinearMathAll.cpp"
    "${BULLET_SRC}/btBulletCollisionAll.cpp"
    "${BULLET_SRC}/btBulletDynamicsAll.cpp")

target_include_directories(mmd_bullet PRIVATE "${BULLET_SRC}")

# Bullet is single-precision (btScalar = float) when BT_USE_DOUBLE_PRECISION is UNDEFINED (an #ifdef,
# not a value test) — leave it undefined to match MMD/babylon-mmd, whose whole physics is float.

# Bullet's own sources trip a lot of harmless warnings; silence them so real shim warnings stand out.
target_compile_options(mmd_bullet PRIVATE $<IF:$<CXX_COMPILER_ID:MSVC>,/w,-w>)

# MSVC auto-defines BT_USE_SSE but leaves BT_USE_SSE_IN_API off, so Bullet's SSE math in
# btMatrix3x3/btQuaternion can't convert __m128 -> btVector3 and fails to compile (C2440/C2679). Bullet's
# own clang-on-Windows path sets __BT_DISABLE_SSE__ to fall back to the portable SCALAR math; do the same
# on MSVC so Windows uses the exact scalar code path Linux already builds — deterministic across platforms,
# and no 16-byte-alignment requirement on embedded btVector3s. (Must apply to EVERY TU: it changes the
# btVector3 layout, so a mixed SSE/scalar build would be an ODR/ABI mismatch — PRIVATE covers all sources.)
if(MSVC)
    target_compile_definitions(mmd_bullet PRIVATE __BT_DISABLE_SSE__)
endif()

# Use CMake's per-platform default library names — libmmd_bullet.so (Linux), libmmd_bullet.dylib (macOS),
# mmd_bullet.dll (Windows). Do NOT force a "lib" prefix: on Windows the managed [LibraryImport("mmd_bullet")]
# resolver looks for mmd_bullet.dll and would miss a libmmd_bullet.dll.
