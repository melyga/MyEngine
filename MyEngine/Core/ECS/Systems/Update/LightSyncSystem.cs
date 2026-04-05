#nullable enable

using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.ECS.Components;

namespace MyEngine.Core.ECS.Systems.Update;

public sealed class LightSyncSystem : AEntitySetSystem<float>
{
    private readonly IRenderer  _renderer;
    private readonly EntitySet  _pointQuery;
    private readonly EntitySet  _spotQuery;
    private readonly EntitySet  _dirQuery;

    public LightSyncSystem(World world, IRenderer renderer)
        : base(world.GetEntities().AsSet())   // базовый EntitySet не используется напрямую
    {
        _renderer   = renderer;
        _pointQuery = world.GetEntities()
                           .With<PointLightComponent>()
                           .With<TransformComponent>()
                           .AsSet();
        _spotQuery  = world.GetEntities()
                           .With<SpotLightComponent>()
                           .With<TransformComponent>()
                           .AsSet();
        _dirQuery   = world.GetEntities()
                           .With<DirectionalLightComponent>()
                           .AsSet();
    }

    protected override void Update(float deltaTime, in Entity _ignored)
    {
        // метод не используется — вся логика в Update(float)
    }

    protected override void Update(float deltaTime, ReadOnlySpan<Entity> entities)
    {
        var lights = new List<LightData>();

        // ── 1. DirectionalLight → в начало ──────────────────────────────────
        foreach (Entity entity in _dirQuery.GetEntities())
        {
            ref readonly DirectionalLightComponent dir =
                ref entity.Get<DirectionalLightComponent>();

            lights.Add(new LightData
            {
                Type      = LightData.LightType.Directional,
                Direction = Vector3.Normalize(dir.Direction),
                Color     = dir.Color,
                Intensity = dir.Intensity,
            });
        }

        // ── 2. SpotLight с ViewmodelTag + IsOn → первым после Directional ───
        var deferredSpots = new List<LightData>();

        foreach (Entity entity in _spotQuery.GetEntities())
        {
            ref readonly SpotLightComponent  spot = ref entity.Get<SpotLightComponent>();
            ref readonly TransformComponent  tr   = ref entity.Get<TransformComponent>();

            bool isViewmodelActive =
                entity.Has<ViewmodelTag>() &&
                entity.Has<ViewmodelLightComponent>() &&
                entity.Get<ViewmodelLightComponent>().IsOn;

            var ld = new LightData
            {
                Type       = LightData.LightType.Spot,
                Position   = tr.Position,
                Direction  = Vector3.Normalize(spot.Direction),
                Color      = spot.Color,
                Intensity  = isViewmodelActive
                                 ? entity.Get<ViewmodelLightComponent>().Intensity
                                 : spot.Intensity,
                Radius     = spot.Radius,
                InnerAngle = spot.InnerAngle,
                OuterAngle = spot.OuterAngle,
            };

            if (isViewmodelActive)
                lights.Insert(0, ld);          // viewmodel-фонарик — самым первым
            else
                deferredSpots.Add(ld);
        }

        lights.AddRange(deferredSpots);

        // ── 3. PointLight ────────────────────────────────────────────────────
        foreach (Entity entity in _pointQuery.GetEntities())
        {
            ref readonly PointLightComponent point = ref entity.Get<PointLightComponent>();
            ref readonly TransformComponent  tr    = ref entity.Get<TransformComponent>();

            lights.Add(new LightData
            {
                Type      = LightData.LightType.Point,
                Position  = tr.Position,
                Color     = point.Color,
                Intensity = point.Intensity,
                Radius    = point.Radius,
            });
        }

        // ── 4. Отправка в рендерер ───────────────────────────────────────────
        _renderer.SetLights(CollectionsMarshal.AsSpan(lights));
    }

    public override void Dispose()
    {
        _pointQuery.Dispose();
        _spotQuery.Dispose();
        _dirQuery.Dispose();
        base.Dispose();
    }
}

// Назначение:   Собирает LightData из ECS-компонентов и передаёт IRenderer.SetLights каждый кадр
// Зависит от:   LightComponents, ViewmodelLightComponent, TransformComponent, Tags, IRenderer, DefaultEcs
// Используется: MyEngine.Core игровой цикл (UpdateSystemGroup)
