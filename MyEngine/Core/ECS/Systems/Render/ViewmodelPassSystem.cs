#nullable enable

using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.ECS.Components;

namespace MyEngine.Core.ECS.Systems.Render;

public sealed class ViewmodelPassSystem : AEntitySetSystem<float>
{
    private readonly IRenderer _renderer;

    public ViewmodelPassSystem(World world, IRenderer renderer)
        : base(world.GetEntities()
            .With<ViewmodelTag>()
            .With<MeshComponent>()
            .With<MaterialComponent>()
            .With<TransformComponent>()
            .AsSet())
    {
        _renderer = renderer;
    }

    protected override void Update(float state, in Entity entity)
    {
        ref readonly var mesh      = ref entity.Get<MeshComponent>();
        ref readonly var material  = ref entity.Get<MaterialComponent>();
        ref readonly var transform = ref entity.Get<TransformComponent>();

        _renderer.Submit(new DrawCall
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
            IsViewmodelPass   = true,
            IsTransparent     = false,
        });
    }
}

// Назначение:   Рендерит viewmodel-сущности с флагом IsViewmodelPass=true (ClearDepth + FOV 55°)
// Зависит от:   DefaultEcs, IRenderer, ViewmodelTag, MeshComponent, MaterialComponent, TransformComponent
// Используется: RenderPipeline, GameLoop
