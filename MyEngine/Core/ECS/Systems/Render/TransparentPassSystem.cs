#nullable enable

using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.ECS.Components;

namespace MyEngine.Core.ECS.Systems.Render;

public sealed class TransparentPassSystem : AEntitySetSystem<float>
{
    private readonly IRenderer _renderer;

    public TransparentPassSystem(World world, IRenderer renderer)
        : base(
            world.GetEntities()
                 .With<TransparentTag>()
                 .With<MeshComponent>()
                 .With<MaterialComponent>()
                 .With<TransformComponent>()
                 .AsSet())
    {
        _renderer = renderer;
    }

    protected override void Update(float deltaTime, in Entity entity)
    {
        ref readonly MeshComponent      mesh      = ref entity.Get<MeshComponent>();
        ref readonly MaterialComponent  material  = ref entity.Get<MaterialComponent>();
        ref readonly TransformComponent transform = ref entity.Get<TransformComponent>();

        DrawCall call = new()
        {
            Mesh              = mesh.Mesh,
            Albedo            = material.Albedo,
            Normal            = material.Normal,
            MetallicRoughness = material.MetallicRoughness,
            Emissive          = material.Emissive,
            WorldMatrix       = transform.WorldMatrix,
            Metallic          = material.MetallicFactor,
            Roughness         = material.RoughnessFactor,
            BaseColor         = material.BaseColorFactor,
            EmissiveFactor    = material.EmissiveFactor,
            IsShadowPass      = false,
            IsViewmodelPass   = false,
            IsTransparent     = true,
        };

        _renderer.Submit(in call);
    }
}

// Назначение:   ECS-система прозрачного прохода — собирает DrawCall с флагом IsTransparent для всех TransparentTag-сущностей
// Зависит от:   IRenderer, DrawCall, TransparentTag, MeshComponent, MaterialComponent, TransformComponent, DefaultEcs
// Используется: RenderPipeline, EngineLoop
