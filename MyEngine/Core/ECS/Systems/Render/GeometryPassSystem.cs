#nullable enable

using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.ECS.Components;
using MyEngine.Core.ECS.Systems.Update;

namespace MyEngine.Core.ECS.Systems.Render;

public sealed class GeometryPassSystem : AEntitySetSystem<float>
{
    private readonly IRenderer    _renderer;
    private readonly CameraSystem _camera;

    public GeometryPassSystem(World world, IRenderer renderer, CameraSystem camera)
        : base(
            world.GetEntities()
                 .With<MeshComponent>()
                 .With<MaterialComponent>()
                 .With<TransformComponent>()
                 .Without<ViewmodelTag>()
                 .Without<UIEntityTag>()
                 .AsSet())
    {
        _renderer = renderer;
        _camera   = camera;
    }

    protected override void PreUpdate(float deltaTime)
    {
        _renderer.SetCamera(new CameraData
        {
            View       = _camera.ViewMatrix,
            Projection = _camera.ProjectionMatrix,
            Position   = _camera.Position,
            Fov        = 90f,
            Near       = 0.1f,
            Far        = 500f,
        });
    }

    protected override void Update(float deltaTime, in Entity entity)
    {
        ref readonly MeshComponent      mesh      = ref entity.Get<MeshComponent>();
        ref readonly MaterialComponent  material  = ref entity.Get<MaterialComponent>();
        ref readonly TransformComponent transform = ref entity.Get<TransformComponent>();

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
            IsViewmodelPass   = false,
            IsTransparent     = false,
        });
    }
}

// Назначение:   Собирает и отправляет DrawCall-ы непрозрачной геометрии, исключая UI и вьюмодель
// Зависит от:   DefaultEcs, IRenderer, CameraSystem, MeshComponent, MaterialComponent, TransformComponent, ViewmodelTag, UIEntityTag
// Используется: EngineLoop (render pipeline), RenderGraph
