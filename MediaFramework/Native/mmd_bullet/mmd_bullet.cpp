// mmd_bullet — C ABI over Bullet 3.25 reproducing MikuMikuDance physics (babylon-mmd's Bullet runtime).
// See mmd_bullet.h. The construction/stepping/sync semantics are ported 1:1 from babylon-mmd's
// bwPhysicsWorld/bwRigidBody and its MMD physics runtime (mmd/mod.rs, mmdAmmoPhysics.ts).
#include "mmd_bullet.h"

#include "btBulletDynamicsCommon.h"
#include "BulletDynamics/ConstraintSolver/btGeneric6DofSpringConstraint.h"

#include <vector>
#include <cstring>

// MMD's overlap filter (bwOverlapFilterCallback): 16-bit group/mask AND, plus an "inverse group" in the
// high 16 bits (StaticFilter << 16) that suppresses collisions between two non-dynamic bodies — kinematic
// FollowBone plates and static colliders must not resolve against each other, only against dynamic cloth.
struct MmdOverlapFilter final : public btOverlapFilterCallback
{
    bool needBroadphaseCollision(btBroadphaseProxy* proxy0, btBroadphaseProxy* proxy1) const override
    {
        const uint16_t invGroup0 = (uint16_t)(proxy0->m_collisionFilterGroup >> 16);
        const uint16_t invGroup1 = (uint16_t)(proxy1->m_collisionFilterGroup >> 16);
        if ((invGroup0 & invGroup1) != 0)
            return false;

        const uint16_t group0 = (uint16_t)proxy0->m_collisionFilterGroup;
        const uint16_t mask0 = (uint16_t)proxy0->m_collisionFilterMask;
        const uint16_t group1 = (uint16_t)proxy1->m_collisionFilterGroup;
        const uint16_t mask1 = (uint16_t)proxy1->m_collisionFilterMask;
        bool collides = (group0 & mask1) != 0;
        collides = collides && (group1 & mask0);
        return collides;
    }
};

// Bullet only writes back the transform of ACTIVE bodies through the motion state; we read the body
// transform directly instead, so a settled (or deactivated) body still reports its pose.
struct MmdMotionState final : public btMotionState
{
    btTransform m_transform;
    explicit MmdMotionState(const btTransform& t) : m_transform(t) {}
    void getWorldTransform(btTransform& t) const override { t = m_transform; }
    void setWorldTransform(const btTransform& t) override { m_transform = t; }
};

struct MmdBody
{
    btRigidBody* body = nullptr;
    btCollisionShape* shape = nullptr;
    MmdMotionState* motion = nullptr;
    uint16_t group = 0;
    uint16_t mask = 0;
    int motionType = MMD_MOTION_DYNAMIC;
    bool kinematic = false; // current runtime kinematic state (dynamic bodies toggled via physics on/off)
};

struct mmd_world
{
    btDefaultCollisionConfiguration* config;
    btCollisionDispatcher* dispatcher;
    btDbvtBroadphase* broadphase;
    btSequentialImpulseConstraintSolver* solver;
    btDiscreteDynamicsWorld* world;
    MmdOverlapFilter filter;
    std::vector<MmdBody> bodies;
    std::vector<btTypedConstraint*> constraints;
};

static inline btTransform ReadTransform(const float* m)
{
    btTransform t;
    t.setFromOpenGLMatrix(m);
    return t;
}

static int RuntimeGroup(int group, bool nonDynamic)
{
    // Non-dynamic bodies (kinematic/static) carry StaticFilter in the high 16 bits so two of them never
    // collide (see MmdOverlapFilter). Dynamic bodies keep only their MMD group in the low 16 bits.
    return nonDynamic ? (group | (btBroadphaseProxy::StaticFilter << 16)) : group;
}

extern "C" MMD_API uint32_t mmd_abi_version(void)
{
    return MMD_ABI_VERSION;
}

extern "C" mmd_world* mmd_world_create(float gx, float gy, float gz)
{
    mmd_world* w = new mmd_world();
    w->config = new btDefaultCollisionConfiguration();
    w->dispatcher = new btCollisionDispatcher(w->config);
    w->broadphase = new btDbvtBroadphase();
    w->solver = new btSequentialImpulseConstraintSolver();
    w->world = new btDiscreteDynamicsWorld(w->dispatcher, w->broadphase, w->solver, w->config);
    w->world->setGravity(btVector3(gx, gy, gz));
    w->world->getPairCache()->setOverlapFilterCallback(&w->filter);
    return w;
}

extern "C" void mmd_world_destroy(mmd_world* w)
{
    if (!w)
        return;
    for (auto* c : w->constraints)
    {
        w->world->removeConstraint(c);
        delete c;
    }
    for (auto& b : w->bodies)
    {
        if (b.body)
        {
            w->world->removeRigidBody(b.body);
            delete b.body;
        }
        delete b.motion;
        delete b.shape;
    }
    delete w->world;
    delete w->solver;
    delete w->broadphase;
    delete w->dispatcher;
    delete w->config;
    delete w;
}

extern "C" int mmd_world_add_body(
    mmd_world* w,
    int shapeType, float sx, float sy, float sz,
    const float* transform16,
    int motionType,
    float mass, float linearDamping, float angularDamping, float friction, float restitution,
    uint16_t group, uint16_t mask,
    int additionalDamping, int noContactResponse, int disableDeactivation)
{
    if (!w)
        return -1;

    btCollisionShape* shape = nullptr;
    switch (shapeType)
    {
        case MMD_SHAPE_SPHERE:       shape = new btSphereShape(sx); break;
        case MMD_SHAPE_BOX:          shape = new btBoxShape(btVector3(sx, sy, sz)); break;
        case MMD_SHAPE_CAPSULE:      shape = new btCapsuleShape(sx, sy); break;
        case MMD_SHAPE_STATIC_PLANE: shape = new btStaticPlaneShape(btVector3(sx, sy, sz), 0.0f); break;
        default: return -1;
    }

    const bool dynamic = motionType == MMD_MOTION_DYNAMIC;
    btScalar bodyMass = dynamic ? mass : 0.0f;
    btVector3 localInertia(0, 0, 0);
    if (bodyMass != 0.0f)
        shape->calculateLocalInertia(bodyMass, localInertia);

    MmdMotionState* motion = new MmdMotionState(ReadTransform(transform16));

    btRigidBody::btRigidBodyConstructionInfo info(bodyMass, motion, shape, localInertia);
    info.m_linearDamping = linearDamping;
    info.m_angularDamping = angularDamping;
    info.m_friction = friction;
    info.m_restitution = restitution;
    info.m_additionalDamping = additionalDamping != 0;

    btRigidBody* body = new btRigidBody(info);
    body->setSleepingThresholds(0.0f, 0.0f);
    if (disableDeactivation)
        body->setActivationState(DISABLE_DEACTIVATION);

    bool nonDynamic = false;
    if (motionType == MMD_MOTION_KINEMATIC)
    {
        body->setCollisionFlags(body->getCollisionFlags() | btCollisionObject::CF_KINEMATIC_OBJECT);
        nonDynamic = true;
    }
    else if (motionType == MMD_MOTION_STATIC)
    {
        body->setCollisionFlags(body->getCollisionFlags() | btCollisionObject::CF_STATIC_OBJECT);
        nonDynamic = true;
    }
    if (noContactResponse)
        body->setCollisionFlags(body->getCollisionFlags() | btCollisionObject::CF_NO_CONTACT_RESPONSE);

    w->world->addRigidBody(body, RuntimeGroup(group, nonDynamic), mask);

    MmdBody b;
    b.body = body;
    b.shape = shape;
    b.motion = motion;
    b.group = group;
    b.mask = mask;
    b.motionType = motionType;
    b.kinematic = nonDynamic;
    w->bodies.push_back(b);
    return (int)(w->bodies.size() - 1);
}

extern "C" void mmd_world_add_spring_constraint(
    mmd_world* w, int bodyA, int bodyB,
    const float* frameA16, const float* frameB16,
    const float* linLower3, const float* linUpper3,
    const float* angLower3, const float* angUpper3,
    const float* springPos3, const float* springRot3)
{
    if (!w || bodyA < 0 || bodyB < 0 ||
        bodyA >= (int)w->bodies.size() || bodyB >= (int)w->bodies.size())
        return;

    btRigidBody* a = w->bodies[bodyA].body;
    btRigidBody* b = w->bodies[bodyB].body;
    btTransform frameA = ReadTransform(frameA16);
    btTransform frameB = ReadTransform(frameB16);

    btGeneric6DofSpringConstraint* c =
        new btGeneric6DofSpringConstraint(*a, *b, frameA, frameB, /*useLinearReferenceFrameA*/ true);

    // MMD ran Bullet 2.75, whose 6DOF constraint had no frame-offset handling; turning frame offset off
    // restores that behaviour (babylon-mmd's forceDisableOffsetForConstraintFrame path).
    c->setUseFrameOffset(false);
    for (int axis = 0; axis < 6; ++axis)
        c->setParam(BT_CONSTRAINT_STOP_ERP, 0.475f, axis);

    c->setLinearLowerLimit(btVector3(linLower3[0], linLower3[1], linLower3[2]));
    c->setLinearUpperLimit(btVector3(linUpper3[0], linUpper3[1], linUpper3[2]));
    c->setAngularLowerLimit(btVector3(angLower3[0], angLower3[1], angLower3[2]));
    c->setAngularUpperLimit(btVector3(angUpper3[0], angUpper3[1], angUpper3[2]));

    // Linear springs only where an authored stiffness is present; angular springs are always enabled
    // (all three axes), the MMD hair/skirt joint convention.
    for (int i = 0; i < 3; ++i)
    {
        if (springPos3[i] != 0.0f)
        {
            c->setStiffness(i, springPos3[i]);
            c->enableSpring(i, true);
        }
        else
        {
            c->enableSpring(i, false);
        }
    }
    for (int i = 0; i < 3; ++i)
    {
        c->setStiffness(3 + i, springRot3[i]);
        c->enableSpring(3 + i, true);
    }

    w->world->addConstraint(c, /*disableCollisionsBetweenLinkedBodies*/ false);
    w->constraints.push_back(c);
}

extern "C" void mmd_body_set_kinematic_transform(mmd_world* w, int body, const float* transform16)
{
    if (!w || body < 0 || body >= (int)w->bodies.size())
        return;
    w->bodies[body].motion->m_transform = ReadTransform(transform16);
}

extern "C" void mmd_body_reset(mmd_world* w, int body, const float* transform16)
{
    if (!w || body < 0 || body >= (int)w->bodies.size())
        return;
    MmdBody& b = w->bodies[body];
    btTransform t = ReadTransform(transform16);
    b.motion->m_transform = t;
    b.body->setWorldTransform(t);
    b.body->setInterpolationWorldTransform(t);
    b.body->setLinearVelocity(btVector3(0, 0, 0));
    b.body->setAngularVelocity(btVector3(0, 0, 0));
    b.body->setInterpolationLinearVelocity(btVector3(0, 0, 0));
    b.body->setInterpolationAngularVelocity(btVector3(0, 0, 0));
    b.body->clearForces();
    b.body->activate(true);
}

// Mirror bwPhysicsWorld::makeBodyKinematic / restoreBodyDynamic: flip CF_KINEMATIC_OBJECT and the
// broadphase inverse-group bit, then refresh the proxy so the change takes effect immediately.
extern "C" void mmd_body_set_kinematic(mmd_world* w, int body, int isKinematic)
{
    if (!w || body < 0 || body >= (int)w->bodies.size())
        return;
    MmdBody& b = w->bodies[body];
    if (b.motionType != MMD_MOTION_DYNAMIC)
        return; // authored kinematic/static bodies never toggle
    const bool want = isKinematic != 0;
    if (b.kinematic == want)
        return;
    b.kinematic = want;

    btRigidBody* body_ = b.body;
    btBroadphaseProxy* proxy = body_->getBroadphaseHandle();
    if (want)
    {
        body_->setCollisionFlags(body_->getCollisionFlags() | btCollisionObject::CF_KINEMATIC_OBJECT);
        if (proxy)
            proxy->m_collisionFilterGroup |= (btBroadphaseProxy::StaticFilter << 16);
    }
    else
    {
        body_->setLinearVelocity(btVector3(0, 0, 0));
        body_->setAngularVelocity(btVector3(0, 0, 0));
        body_->setCollisionFlags(body_->getCollisionFlags() & ~btCollisionObject::CF_KINEMATIC_OBJECT);
        if (proxy)
            proxy->m_collisionFilterGroup &= ~(btBroadphaseProxy::StaticFilter << 16);
    }
    if (proxy)
    {
        w->world->getPairCache()->cleanProxyFromPairs(proxy, w->dispatcher);
        w->world->refreshBroadphaseProxy(body_);
    }
    body_->activate(true);
}

extern "C" void mmd_body_get_transform(mmd_world* w, int body, float* out16)
{
    if (!w || body < 0 || body >= (int)w->bodies.size())
        return;
    w->bodies[body].body->getWorldTransform().getOpenGLMatrix(out16);
}

extern "C" void mmd_world_step(mmd_world* w, float timeStep, int maxSubSteps, float fixedTimeStep)
{
    if (!w)
        return;
    w->world->stepSimulation(timeStep, maxSubSteps, fixedTimeStep);
}
