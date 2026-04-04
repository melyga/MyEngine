// MyEngine.Core/Physics/BulletPhysicsBackend.cs
#nullable enable

using System.Numerics;
using BulletSharp;
using MyEngine.Core.Abstractions;

namespace MyEngine.Core.Physics;

public sealed class BulletPhysicsBackend : IPhysicsBackend
{
    // ──────────────────────────────────────────────
    //  Bullet-объекты жизненного цикла
    // ──────────────────────────────────────────────
    private readonly DefaultCollisionConfiguration      _configuration;
    private readonly CollisionDispatcher                _dispatcher;
    private readonly DbvtBroadphase                     _broadphase;
    private readonly SequentialImpulseConstraintSolver  _solver;
    private readonly DiscreteDynamicsWorld              _world;

    // Хранилище: handle-id → RigidBody
    private readonly Dictionary<int, RigidBody> _bodies = new();
    private int _nextId;

    // ──────────────────────────────────────────────
    //  Конструктор
    // ──────────────────────────────────────────────
    public BulletPhysicsBackend()
    {
        _configuration = new DefaultCollisionConfiguration();
        _dispatcher    = new CollisionDispatcher(_configuration);
        _broadphase    = new DbvtBroadphase();
        _solver        = new SequentialImpulseConstraintSolver();

        _world = new DiscreteDynamicsWorld(
            _dispatcher,
            _broadphase,
            _solver,
            _configuration)
        {
            Gravity = new BulletSharp.Math.Vector3(0f, -9.81f, 0f)
        };
    }

    // ──────────────────────────────────────────────
    //  Вспомогательные методы конвертации
    // ──────────────────────────────────────────────
    private static BulletSharp.Math.Vector3 ToBullet(Vector3 v)
        => new(v.X, v.Y, v.Z);

    private static Vector3 ToNumerics(BulletSharp.Math.Vector3 v)
        => new(v.X, v.Y, v.Z);

    private static Quaternion ToNumerics(BulletSharp.Math.Quaternion q)
        => new(q.X, q.Y, q.Z, q.W);

    // Строит Matrix4x4 позиционирования из Vector3
    private static BulletSharp.Math.Matrix StartTransform(Vector3 position)
    {
        var m = BulletSharp.Math.Matrix.Identity;
        m.Origin = ToBullet(position);
        return m;
    }

    // Создаёт RigidBody с общими настройками
    private static RigidBody BuildRigidBody(
        CollisionShape shape,
        float          mass,
        Vector3        position)
    {
        bool isDynamic = mass > 0f;

        BulletSharp.Math.Vector3 localInertia = BulletSharp.Math.Vector3.Zero;
        if (isDynamic)
            shape.CalculateLocalInertia(mass, out localInertia);

        using var motionState = new DefaultMotionState(StartTransform(position));

        var rbInfo = new RigidBodyConstructionInfo(mass, motionState, shape, localInertia);
        var body   = new RigidBody(rbInfo);

        return body;
    }

    // Регистрирует тело в словаре и World; возвращает handle
    private PhysicsBodyHandle Register(RigidBody body)
    {
        int id = _nextId++;
        _bodies[id] = body;
        _world.AddRigidBody(body);
        return new PhysicsBodyHandle(id);
    }

    // ──────────────────────────────────────────────
    //  IPhysicsBackend — создание тел
    // ──────────────────────────────────────────────
    public PhysicsBodyHandle CreateCapsule(float radius, float height, float mass, Vector3 position)
    {
        var shape = new CapsuleShape(radius, height);
        var body  = BuildRigidBody(shape, mass, position);
        body.AngularFactor = BulletSharp.Math.Vector3.Zero;
        return Register(body);
    }

    public PhysicsBodyHandle CreateBox(Vector3 halfExtents, float mass, Vector3 position)
    {
        var shape = new BoxShape(ToBullet(halfExtents));
        var body  = BuildRigidBody(shape, mass, position);
        return Register(body);
    }

    public PhysicsBodyHandle CreateStaticMesh(
        ReadOnlySpan<Vector3> vertices,
        ReadOnlySpan<int>     indices)
    {
        // TriangleIndexVertexArray требует управляемые массивы
        var vertArray  = new BulletSharp.Math.Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            vertArray[i] = ToBullet(vertices[i]);

        var idxArray = indices.ToArray();

        var meshData = new TriangleIndexVertexArray(idxArray, vertArray);
        var shape    = new BvhTriangleMeshShape(meshData, useQuantizedAabbCompression: true);

        // Статическое тело: mass = 0
        var body = BuildRigidBody(shape, mass: 0f, position: Vector3.Zero);
        return Register(body);
    }

    // ──────────────────────────────────────────────
    //  IPhysicsBackend — удаление
    // ──────────────────────────────────────────────
    public void Remove(PhysicsBodyHandle handle)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body))
            return;

        _world.RemoveRigidBody(body);
        body.CollisionShape?.Dispose();
        body.MotionState?.Dispose();
        body.Dispose();
        _bodies.Remove(handle.Id);
    }

    // ──────────────────────────────────────────────
    //  IPhysicsBackend — управление телом
    // ──────────────────────────────────────────────
    public void SetLinearVelocity(PhysicsBodyHandle handle, Vector3 v)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body)) return;

        body.LinearVelocity = ToBullet(v);
        body.Activate(forceActivation: true);
    }

    public void ApplyImpulse(PhysicsBodyHandle handle, Vector3 impulse)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body)) return;

        body.ApplyCentralImpulse(ToBullet(impulse));
        body.Activate(forceActivation: true);
    }

    public void SetPosition(PhysicsBodyHandle handle, Vector3 pos)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body)) return;

        var transform  = body.WorldTransform;
        transform.Origin = ToBullet(pos);
        body.WorldTransform = transform;

        // Синхронизируем MotionState, чтобы Bullet не откатил позицию
        body.MotionState?.SetWorldTransform(ref transform);
        body.Activate(forceActivation: true);
    }

    public void SetAngularFactor(PhysicsBodyHandle handle, Vector3 factor)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body)) return;

        body.AngularFactor = ToBullet(factor);
    }

    // ──────────────────────────────────────────────
    //  IPhysicsBackend — чтение состояния
    // ──────────────────────────────────────────────
    public Vector3 GetPosition(PhysicsBodyHandle handle)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body))
            return Vector3.Zero;

        return ToNumerics(body.WorldTransform.Origin);
    }

    public Quaternion GetRotation(PhysicsBodyHandle handle)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body))
            return Quaternion.Identity;

        body.WorldTransform.Decompose(
            out _,
            out BulletSharp.Math.Quaternion rotation,
            out _);

        return ToNumerics(rotation);
    }

    public Vector3 GetLinearVelocity(PhysicsBodyHandle handle)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body))
            return Vector3.Zero;

        return ToNumerics(body.LinearVelocity);
    }

    // ──────────────────────────────────────────────
    //  IPhysicsBackend — запросы
    // ──────────────────────────────────────────────

    // IsGrounded: луч вниз на 0.15 м от текущей позиции тела
    public bool IsGrounded(PhysicsBodyHandle handle)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body))
            return false;

        var origin = body.WorldTransform.Origin;
        var to     = origin + new BulletSharp.Math.Vector3(0f, -0.15f, 0f);

        using var cb = new ClosestRayResultCallback(ref origin, ref to);
        _world.RayTest(origin, to, cb);

        if (!cb.HasHit)
            return false;

        // Убеждаемся, что попали не в само же тело
        return !ReferenceEquals(cb.CollisionObject, body);
    }

    public bool Raycast(Vector3 from, Vector3 direction, float maxDistance, out RaycastHit hit)
    {
        var bulletFrom = ToBullet(from);
        var bulletTo   = ToBullet(from + direction * maxDistance);

        using var cb = new ClosestRayResultCallback(ref bulletFrom, ref bulletTo);
        _world.RayTest(bulletFrom, bulletTo, cb);

        if (!cb.HasHit)
        {
            hit = default;
            return false;
        }

        // Ищем handle по совпадению объекта
        int foundId = -1;
        foreach (var kvp in _bodies)
        {
            if (ReferenceEquals(kvp.Value, cb.CollisionObject))
            {
                foundId = kvp.Key;
                break;
            }
        }

        hit = new RaycastHit
        {
            Point    = ToNumerics(cb.HitPointWorld),
            Normal   = ToNumerics(cb.HitNormalWorld),
            Distance = Vector3.Distance(from, ToNumerics(cb.HitPointWorld)),
            Body     = foundId >= 0 ? new PhysicsBodyHandle(foundId) : PhysicsBodyHandle.Invalid
        };

        return true;
    }

    // ──────────────────────────────────────────────
    //  IPhysicsBackend — шаг симуляции
    // ──────────────────────────────────────────────
    public void Step(float dt)
        => _world.StepSimulation(dt, maxSubSteps: 10, fixedTimeStep: 1f / 60f);

    // ──────────────────────────────────────────────
    //  IDisposable
    // ──────────────────────────────────────────────
    public void Dispose()
    {
        // Удаляем все тела из мира перед освобождением ресурсов
        foreach (var kvp in _bodies)
        {
            var body = kvp.Value;
            _world.RemoveRigidBody(body);
            body.CollisionShape?.Dispose();
            body.MotionState?.Dispose();
            body.Dispose();
        }
        _bodies.Clear();

        _world.Dispose();
        _solver.Dispose();
        _broadphase.Dispose();
        _dispatcher.Dispose();
        _configuration.Dispose();
    }
}

// Назначение:   Реализация IPhysicsBackend через BulletSharp — управление телами, симуляция, raycast
// Зависит от:   IPhysicsBackend, PhysicsBodyHandle, RaycastHit, BulletSharp, System.Numerics
// Используется: MyEngine.Core.Engine, PhysicsSystem, любой код, создающий физические тела
