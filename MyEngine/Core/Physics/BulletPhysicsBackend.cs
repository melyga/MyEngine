// MyEngine.Core/Physics/BulletPhysicsBackend.cs
#nullable enable

using BulletSharp;
using MyEngine.Core.Abstractions;
using NumQuaternion = System.Numerics.Quaternion;
using NumVector3 = System.Numerics.Vector3;
using BtMatrix4x4 = WaveEngine.Mathematics.Matrix4x4;
using BtQuaternion = WaveEngine.Mathematics.Quaternion;
using BtVector3 = WaveEngine.Mathematics.Vector3;

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
            Gravity = new BtVector3(0f, -9.81f, 0f)
        };
    }

    // ──────────────────────────────────────────────
    //  Вспомогательные методы конвертации
    // ──────────────────────────────────────────────
    private static BtVector3 ToBullet(NumVector3 v)
        => new(v.X, v.Y, v.Z);

    private static NumVector3 ToNumerics(BtVector3 v)
        => new(v.X, v.Y, v.Z);

    private static NumQuaternion ToNumerics(BtQuaternion q)
        => new(q.X, q.Y, q.Z, q.W);

    // Строит Matrix4x4 позиционирования из Vector3
    private static BtMatrix4x4 StartTransform(NumVector3 position)
        => BtMatrix4x4.CreateTranslation(ToBullet(position));

    // Создаёт RigidBody с общими настройками
    private static RigidBody BuildRigidBody(
        CollisionShape shape,
        float          mass,
        NumVector3     position)
    {
        bool isDynamic = mass > 0f;

        BtVector3 localInertia = default;
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
    public PhysicsBodyHandle CreateCapsule(float radius, float height, float mass, NumVector3 position)
    {
        var shape = new CapsuleShape(radius, height);
        var body  = BuildRigidBody(shape, mass, position);
        body.AngularFactor = new BtVector3(0f, 0f, 0f);
        return Register(body);
    }

    public PhysicsBodyHandle CreateBox(NumVector3 halfExtents, float mass, NumVector3 position)
    {
        var shape = new BoxShape(ToBullet(halfExtents));
        var body  = BuildRigidBody(shape, mass, position);
        return Register(body);
    }

    public PhysicsBodyHandle CreateStaticMesh(
        ReadOnlySpan<NumVector3> vertices,
        ReadOnlySpan<int>     indices)
    {
        // TriangleIndexVertexArray требует управляемые массивы
        var vertArray  = new BtVector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            vertArray[i] = ToBullet(vertices[i]);

        var idxArray = indices.ToArray();

        var meshData = new TriangleIndexVertexArray(idxArray, vertArray);
        var shape    = new BvhTriangleMeshShape(meshData, useQuantizedAabbCompression: true);

        // Статическое тело: mass = 0
        var body = BuildRigidBody(shape, mass: 0f, position: NumVector3.Zero);
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
    public void SetLinearVelocity(PhysicsBodyHandle handle, NumVector3 v)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body)) return;

        body.LinearVelocity = ToBullet(v);
        body.Activate(forceActivation: true);
    }

    public void ApplyImpulse(PhysicsBodyHandle handle, NumVector3 impulse)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body)) return;

        body.ApplyCentralImpulse(ToBullet(impulse));
        body.Activate(forceActivation: true);
    }

    public void SetPosition(PhysicsBodyHandle handle, NumVector3 pos)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body)) return;

        var transform  = body.WorldTransform;
        transform.Translation = ToBullet(pos);
        body.WorldTransform = transform;

        // Синхронизируем MotionState, чтобы Bullet не откатил позицию
        if (body.MotionState is not null)
            body.MotionState.WorldTransform = transform;
        body.Activate(forceActivation: true);
    }

    public void SetAngularFactor(PhysicsBodyHandle handle, NumVector3 factor)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body)) return;

        body.AngularFactor = ToBullet(factor);
    }

    // ──────────────────────────────────────────────
    //  IPhysicsBackend — чтение состояния
    // ──────────────────────────────────────────────
    public NumVector3 GetPosition(PhysicsBodyHandle handle)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body))
            return NumVector3.Zero;

        return ToNumerics(body.WorldTransform.Translation);
    }

    public NumQuaternion GetRotation(PhysicsBodyHandle handle)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body))
            return NumQuaternion.Identity;

        return ToNumerics(BtQuaternion.CreateFromRotationMatrix(body.WorldTransform));
    }

    public NumVector3 GetLinearVelocity(PhysicsBodyHandle handle)
    {
        if (!_bodies.TryGetValue(handle.Id, out var body))
            return NumVector3.Zero;

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

        var origin = body.WorldTransform.Translation;
        var to     = origin + new BtVector3(0f, -0.15f, 0f);

        using var cb = new ClosestRayResultCallback(ref origin, ref to);
        _world.RayTest(origin, to, cb);

        if (!cb.HasHit)
            return false;

        // Убеждаемся, что попали не в само же тело
        return !ReferenceEquals(cb.CollisionObject, body);
    }

    public bool Raycast(NumVector3 from, NumVector3 direction, float maxDistance, out RaycastHit hit)
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
            Distance = NumVector3.Distance(from, ToNumerics(cb.HitPointWorld)),
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
