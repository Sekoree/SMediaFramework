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
set(CMAKE_CXX_FLAGS_RELEASE "-O3 -DNDEBUG")

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
target_compile_options(mmd_bullet PRIVATE -w)

set_target_properties(mmd_bullet PROPERTIES OUTPUT_NAME mmd_bullet PREFIX "lib")
