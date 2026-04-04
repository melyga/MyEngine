#nullable enable

using System.Numerics;
using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.ECS.Components;

namespace MyEngine.Core.ECS.Systems.Render;

public sealed class ShadowPassSystem : AEntitySetSystem<float>
{
    private readonly IRenderer _renderer;

    public ShadowPassSystem(World world, IRenderer renderer)
        : base(
            world.GetEntities()
                 .With<CastsShadowTag>()
                 .With<MeshComponent>()
                 .With<TransformComponent>()
                 .AsSet())
    {
        _renderer = renderer;
    }

    protected override void Update(float deltaTime, in Entity entity)
    {
        ref readonly MeshComponent      mesh      = ref entity.Get<MeshComponent>();
        ref readonly TransformComponent transform = ref entity.Get<TransformComponent>();

        _renderer.Submit(new DrawCall
        {
            Mesh              = mesh.Mesh,
            WorldMatrix       = transform.WorldMatrix,
            IsShadowPass      = true,
            IsViewmodelPass   = false,
            IsTransparent     = false,
            Albedo            = default,
            Normal            = default,
            MetallicRoughness = default,
            Emissive          = default,
            Metallic          = 0f,
            Roughness         = 0f,
            BaseColor         = Vector4.Zero,
            EmissiveFactor    = Vector3.Zero,
        });
    }
}

// Назначение:   ECS-система теневого прохода — сабмитит DrawCall только с геометрией, без текстур и PBR-данных
// Зависит от:   IRenderer, DrawCall, MeshComponent, TransformComponent, CastsShadowTag, DefaultEcs
// Используется: RenderPipeline, EngineLoop
