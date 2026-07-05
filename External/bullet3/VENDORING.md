# Vendored Bullet Physics — provenance

This directory is a **trimmed, vendored copy** of Bullet Physics, linked into the MMD physics shim
(`MediaFramework/Native/mmd_bullet`) to reproduce MikuMikuDance's physics exactly as babylon-mmd does.
It is checked in as plain tracked files (not a git submodule) — the same convention this repo uses for
`External/Classic.Avalonia`, chosen because a phantom submodule previously broke CI.

## Upstream

| | |
|---|---|
| Project | [bulletphysics/bullet3](https://github.com/bulletphysics/bullet3) |
| Version | **3.25** (see `VERSION`) |
| Commit  | `2c204c49e56ed15ec5fcfa71d199ab6d6570b3f5` (tag `3.25`) |
| Precision | single (`btScalar = float`) — `BT_USE_DOUBLE_PRECISION` left **undefined**, matching MMD/babylon-mmd |

## What was trimmed

Only the three libraries the shim compiles are kept, plus Bullet's own top-level aggregate/common files:

- `LinearMath/`, `BulletCollision/`, `BulletDynamics/`
- `btLinearMathAll.cpp`, `btBulletCollisionAll.cpp`, `btBulletDynamicsAll.cpp` (the aggregates the shim's
  CMake compiles — they `#include` exactly the translation units needed, so there is no dependency on
  Bullet's own CMake targets), and the matching `*Common.h` headers, and `CMakeLists.txt`.

Everything upstream ships outside those three libraries (`Bullet3*`, `BulletInverseDynamics`,
`BulletSoftBody`, `clew`, examples, Extras, demos, tests) was dropped — none of it is referenced.

## What was modified

**Exactly one file**, via `patches/constraint-fix.patch` (ported verbatim from babylon-mmd's
`src/Runtime/Optimized/wasm_src/bullet_src/constraint-fix.patch`):

- `BulletDynamics/ConstraintSolver/btGeneric6DofConstraint.cpp` — line ~746 uses
  `m_calculatedTransformA` instead of `m_calculatedTransformB` for the linear torque-decoupling arm, so
  the 6-DOF spring joints behave like MMD's Bullet. The change is applied inline (with an explanatory
  comment); the `.patch` file is kept only for auditability / re-derivation.

Every other file in the three vendored libraries is **byte-identical to upstream 3.25**.

## Verifying integrity

`MediaFramework/Native/mmd_bullet/verify-vendored-bullet.sh` sparse-clones upstream tag 3.25 and asserts
that the vendored tree differs from it *only* by `patches/constraint-fix.patch`. Run it after any Bullet
bump or if you suspect drift. To re-derive the vendored tree from scratch: sparse-checkout upstream `src`
at tag 3.25, keep the three libraries + aggregates, then
`git apply -p1 --directory=External/bullet3/src External/bullet3/patches/constraint-fix.patch`.

## Building

`MediaFramework/Native/mmd_bullet/build.sh` compiles the shim + the three aggregate `*All.cpp` into
`libmmd_bullet.so`. The build output (`build/`, `libmmd_bullet.so`) is git-ignored; CI builds it and
stages it into the natives artifact.
