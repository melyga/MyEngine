// MyEngine.Core/ECS/Systems/Update/PhysicsSyncSystem.cs
#nullable enable

using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.ECS.Components;

namespace MyEngine.Core.ECS.Systems.Update;

public sealed class PhysicsSyncSystem : AEntitySetSystem<float>
{
    private readonly IPhysicsBackend _physics;
    private bool _stepped;

    public PhysicsSyncSystem(World world, IPhysicsBackend physics)
        : base(world.GetEntities()
                    .With<RigidBodyComponent>()
                    .With<TransformComponent>()
                    .AsSet())
    {
        _physics = physics;
    }

    protected override void PreUpdate(float state)
    {
        _stepped = false;
    }

    protected override void Update(float dt, ReadOnlySpan<Entity> entities)
    {
        if (!_stepped)
        {
            _physics.Step(dt);
            _stepped = true;
        }

        foreach (Entity entity in entities)
        {
            ref readonly RigidBodyComponent body      = ref entity.Get<RigidBodyComponent>();
            ref          TransformComponent transform = ref entity.Get<TransformComponent>();

            transform.Position = _physics.GetPosition(body.Body);
            transform.Rotation = _physics.GetRotation(body.Body);
        }
    }
}

// Назначение:   Синхронизирует позицию и вращение из физического бэкенда в TransformComponent после шага симуляции
// Зависит от:   IPhysicsBackend, RigidBodyComponent, TransformComponent, DefaultEcs
// Используется: MyEngine.Core.Engine, игровой цикл Update-фазы
