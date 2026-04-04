#nullable enable

using System;
using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.ECS.Components;
using MyEngine.Core.ECS.Systems.Update;

namespace MyEngine.Core.ECS.Systems.Render;

public sealed class ParticleRenderSystem : AEntitySetSystem<float>
{
    // ── Private fields ───────────────────────────────────────────────────────

    private readonly IRenderer      _renderer;
    private readonly ParticleSystem _sim;

    // Переиспользуемый буфер инстансов — избегает аллокаций в горячем пути
    private ParticleInstance[] _instanceBuffer = new ParticleInstance[1024];

    // ── Constructor ──────────────────────────────────────────────────────────

    public ParticleRenderSystem(World world, IRenderer renderer, ParticleSystem sim)
        : base(world
            .GetEntities()
            .With<ParticleEmitterComponent>()
            .With<TransformComponent>()
            .AsSet())
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _sim      = sim      ?? throw new ArgumentNullException(nameof(sim));
    }

    // ── Per-entity update ────────────────────────────────────────────────────

    protected override void Update(float dt, in Entity entity)
    {
        ref readonly ParticleEmitterComponent emitter =
            ref entity.Get<ParticleEmitterComponent>();

        if (!emitter.IsPlaying)
            return;

        ReadOnlySpan<ParticleSystem.Particle> particles = _sim.GetParticles(entity);

        if (particles.IsEmpty)
            return;

        // Считаем живые частицы для точного размера массива инстансов
        int liveCount = 0;
        for (int i = 0; i < particles.Length; i++)
        {
            if (particles[i].Alive)
                liveCount++;
        }

        if (liveCount == 0)
            return;

        // Расширяем буфер при необходимости (степень двойки, без shrink)
        if (_instanceBuffer.Length < liveCount)
        {
            int newSize = _instanceBuffer.Length;
            while (newSize < liveCount)
                newSize <<= 1;
            _instanceBuffer = new ParticleInstance[newSize];
        }

        // Заполняем инстансы из живых частиц
        int idx = 0;
        for (int i = 0; i < particles.Length && idx < liveCount; i++)
        {
            ref readonly ParticleSystem.Particle p = ref particles[i];

            if (!p.Alive)
                continue;

            _instanceBuffer[idx++] = new ParticleInstance
            {
                Position = p.Position,
                Color    = p.Color,
                Size     = p.Size,
            };
        }

        // Копируем точный срез, чтобы не передавать «хвост» буфера
        ParticleInstance[] instanceArray = new ParticleInstance[liveCount];
        Array.Copy(_instanceBuffer, instanceArray, liveCount);

        _renderer.Submit(new ParticleDrawCall
        {
            Texture       = default,        // текстура задаётся через компонент при расширении
            InstanceCount = liveCount,
            Instances     = instanceArray,
        });
    }
}

// Назначение:   Собирает живые частицы из ParticleSystem и отправляет ParticleDrawCall в IRenderer
// Зависит от:   DefaultEcs, IRenderer, ParticleSystem, ParticleEmitterComponent, TransformComponent
// Используется: SystemRunner / GameLoop (рендер-фаза)
