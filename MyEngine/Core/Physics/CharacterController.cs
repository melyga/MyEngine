// MyEngine.Core/Physics/CharacterController.cs
#nullable enable

using BulletSharp;
using NumVector3 = System.Numerics.Vector3;
using BtMatrix4x4 = WaveEngine.Mathematics.Matrix4x4;
using BtVector3 = WaveEngine.Mathematics.Vector3;

namespace MyEngine.Core.Physics;

internal sealed class CharacterController : IDisposable
{
    private readonly DiscreteDynamicsWorld _world;
    private bool _disposed;

    public RigidBody Body { get; }
    public bool IsGrounded { get; private set; }

    public CharacterController(
        DiscreteDynamicsWorld world,
        NumVector3 spawnPos,
        float radius = 0.4f,
        float height = 1.0f)
    {
        _world = world;

        var shape = new CapsuleShape(radius, height);

        BtMatrix4x4 startTransform = BtMatrix4x4.CreateTranslation(new BtVector3(spawnPos.X, spawnPos.Y, spawnPos.Z));
        var motionState = new DefaultMotionState(startTransform);

        shape.CalculateLocalInertia(80f, out BtVector3 localInertia);

        var constructionInfo = new RigidBodyConstructionInfo(80f, motionState, shape, localInertia)
        {
            Friction = 0f
        };

        Body = new RigidBody(constructionInfo);
        constructionInfo.Dispose();

        Body.AngularFactor      = new BtVector3(0f, 0f, 0f);
        Body.ActivationState    = ActivationState.DisableDeactivation;

        _world.AddRigidBody(Body);
    }

    public void Move(NumVector3 direction, float speed)
    {
        float y = Body.LinearVelocity.Y;
        Body.LinearVelocity = new BtVector3(direction.X * speed, y, direction.Z * speed);
    }

    public void Jump(float impulse)
    {
        if (IsGrounded)
        {
            Body.ApplyCentralImpulse(new BtVector3(0f, impulse, 0f));
        }
    }

    public void Update()
    {
        IsGrounded = PerformDownRaycast(0.15f);
    }

    public bool TryStepUp(NumVector3 forward, float stepHeight)
    {
        BtVector3 position = Body.WorldTransform.Translation;

        BtVector3 dir      = BtVector3.Normalize(new BtVector3(forward.X, forward.Y, forward.Z));
        BtVector3 rayStart = position;
        BtVector3 rayEnd   = position + dir * (0.5f + stepHeight);

        using var forwardCb = new ClosestRayResultCallback(ref rayStart, ref rayEnd);
        _world.RayTest(rayStart, rayEnd, forwardCb);

        if (!forwardCb.HasHit)
            return false;

        float hitDistance = BtVector3.Distance(rayStart, rayEnd) * forwardCb.ClosestHitFraction;

        if (hitDistance > stepHeight)
            return false;

        BtVector3 elevated = position + new BtVector3(0f, stepHeight, 0f);
        BtMatrix4x4 newTransform = BtMatrix4x4.CreateTranslation(elevated);
        Body.WorldTransform             = newTransform;
        Body.MotionState.WorldTransform = newTransform;

        return true;
    }

    private bool PerformDownRaycast(float extraDistance)
    {
        BtVector3 position = Body.WorldTransform.Translation;
        BtVector3 rayStart = position;
        BtVector3 rayEnd   = position - new BtVector3(0f, extraDistance, 0f);

        using var cb = new ClosestRayResultCallback(ref rayStart, ref rayEnd);
        _world.RayTest(rayStart, rayEnd, cb);

        return cb.HasHit && !ReferenceEquals(cb.CollisionObject, Body);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _world.RemoveRigidBody(Body);

        Body.MotionState?.Dispose();
        Body.CollisionShape?.Dispose();
        Body.Dispose();
    }
}

// Назначение:   Внутренний контроллер персонажа на базе BulletSharp — капсула, движение, прыжок, ступенька
// Зависит от:   BulletSharp (DiscreteDynamicsWorld, RigidBody, CapsuleShape, DefaultMotionState, ClosestRayResultCallback)
// Используется: BulletPhysicsBackend
