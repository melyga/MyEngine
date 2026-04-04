// MyEngine.Core/Abstractions/IPhysicsBackend.cs
#nullable enable

using System.Numerics;

namespace MyEngine.Core.Abstractions;

public interface IPhysicsBackend : IDisposable
{
    void Step(float dt);

    PhysicsBodyHandle CreateCapsule(float radius, float height, float mass, Vector3 position);

    PhysicsBodyHandle CreateBox(Vector3 halfExtents, float mass, Vector3 position);

    PhysicsBodyHandle CreateStaticMesh(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices);

    void Remove(PhysicsBodyHandle handle);

    void SetLinearVelocity(PhysicsBodyHandle h, Vector3 v);

    void ApplyImpulse(PhysicsBodyHandle h, Vector3 impulse);

    void SetPosition(PhysicsBodyHandle h, Vector3 pos);

    void SetAngularFactor(PhysicsBodyHandle h, Vector3 factor);

    Vector3 GetPosition(PhysicsBodyHandle h);

    Quaternion GetRotation(PhysicsBodyHandle h);

    Vector3 GetLinearVelocity(PhysicsBodyHandle h);

    bool IsGrounded(PhysicsBodyHandle h);

    bool Raycast(Vector3 from, Vector3 direction, float maxDistance, out RaycastHit hit);
}

// Назначение:   Абстракция физического бэкенда — создание тел, симуляция шага, запросы состояния и raycast
// Зависит от:   PhysicsBodyHandle, RaycastHit, System.Numerics
// Используется: PhysicsSystem, BulletPhysicsBackend, MyEngine.Core.Engine
