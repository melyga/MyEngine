// MyEngine.Core/Physics/CharacterController.cs
#nullable enable

using System.Numerics;
using BulletSharp;

namespace MyEngine.Core.Physics;

internal sealed class CharacterController : IDisposable
{
    private readonly DiscreteDynamicsWorld _world;
    private bool _disposed;

    public RigidBody Body { get; }
    public bool IsGrounded { get; private set; }

    public CharacterController(
        DiscreteDynamicsWorld world,
        Vector3 spawnPos,
        float radius = 0.4f,
        float height = 1.0f)
    {
        _world = world;

        var shape = new CapsuleShape(radius, height);

        Matrix4x4 startTransform = Matrix4x4.CreateTranslation(spawnPos);
        var motionState = new DefaultMotionState(startTransform);

        shape.CalculateLocalInertia(80f, out Vector3 localInertia);

        var constructionInfo = new RigidBodyConstructionInfo(80f, motionState, shape, localInertia)
        {
            Friction = 0f
        };

        Body = new RigidBody(constructionInfo);
        constructionInfo.Dispose();

        Body.AngularFactor      = Vector3.Zero;
        Body.ActivationState    = ActivationState.DisableDeactivation;

        _world.AddRigidBody(Body);
    }

    public void Move(Vector3 direction, float speed)
    {
        float y = Body.LinearVelocity.Y;
        Body.LinearVelocity = new Vector3(direction.X * speed, y, direction.Z * speed);
    }

    public void Jump(float impulse)
    {
        if (IsGrounded)
        {
            Body.ApplyCentralImpulse(Vector3.UnitY * impulse);
        }
    }

    public void Update()
    {
        IsGrounded = PerformDownRaycast(0.15f);
    }

    public bool TryStepUp(Vector3 forward, float stepHeight)
    {
        Vector3 position = Body.WorldTransform.Translation;

        Vector3 dir      = Vector3.Normalize(forward);
        Vector3 rayStart = position;
        Vector3 rayEnd   = position + dir * (0.5f + stepHeight);

        using var forwardCb = new ClosestRayResultCallback(ref rayStart, ref rayEnd);
        _world.RayTest(rayStart, rayEnd, forwardCb);

        if (!forwardCb.HasHit)
            return false;

        float hitDistance = Vector3.Distance(rayStart, rayEnd) * forwardCb.ClosestHitFraction;

        if (hitDistance > stepHeight)
            return false;

        Vector3 elevated = position + Vector3.UnitY * stepHeight;
        Matrix4x4 newTransform = Matrix4x4.CreateTranslation(elevated);
        Body.WorldTransform             = newTransform;
        Body.MotionState.WorldTransform = newTransform;

        return true;
    }

    private bool PerformDownRaycast(float extraDistance)
    {
        Vector3 position = Body.WorldTransform.Translation;
        Vector3 rayStart = position;
        Vector3 rayEnd   = position - Vector3.UnitY * extraDistance;

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
