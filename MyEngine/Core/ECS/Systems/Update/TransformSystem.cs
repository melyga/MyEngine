using System.Numerics;
using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.ECS.Components;

namespace MyEngine.Core.ECS.Systems.Update;

#nullable enable

public sealed class TransformSystem : AEntitySetSystem<float>
{
    public TransformSystem(World world)
        : base(world.GetEntities().With<TransformComponent>().AsSet())
    {
    }

    protected override void Update(float deltaTime, in Entity entity)
    {
        ref TransformComponent transform = ref entity.Get<TransformComponent>();

        transform.WorldMatrix =
            Matrix4x4.CreateScale(transform.Scale)
            * Matrix4x4.CreateFromQuaternion(transform.Rotation)
            * Matrix4x4.CreateTranslation(transform.Position);
    }
}

// Назначение:   Пересчитывает WorldMatrix каждой сущности из Position, Rotation и Scale
// Зависит от:   TransformComponent, DefaultEcs, System.Numerics
// Используется: EngineLoop, SystemScheduler
