#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.ECS.Components;

namespace MyEngine.Core.ECS.Systems.Update;

public sealed class ParticleSystem : AEntitySetSystem<float>
{
    // ── Nested particle state ────────────────────────────────────────────────

    public struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector4 Color;
        public float   Size;
        public float   Age;
        public float   Lifetime;
        public bool    Alive;
    }

    // ── Private fields ───────────────────────────────────────────────────────

    private readonly Dictionary<Entity, Particle[]> _pools        = new();
    private readonly Dictionary<Entity, float>      _accumulators = new();
    private readonly Random                         _rng          = new(Environment.TickCount);

    // ── Constructor ──────────────────────────────────────────────────────────

    public ParticleSystem(World world)
        : base(world
            .GetEntities()
            .With<ParticleEmitterComponent>()
            .With<TransformComponent>()
            .AsSet())
    { }

    // ── Public API (called by ParticleRenderSystem) ──────────────────────────

    public ReadOnlySpan<Particle> GetParticles(Entity entity)
    {
        return _pools.TryGetValue(entity, out Particle[]? pool)
            ? new ReadOnlySpan<Particle>(pool)
            : ReadOnlySpan<Particle>.Empty;
    }

    // ── Per-entity update ────────────────────────────────────────────────────

    protected override void Update(float dt, in Entity entity)
    {
        ref readonly ParticleEmitterComponent emitter =
            ref entity.Get<ParticleEmitterComponent>();

        if (!emitter.IsPlaying)
            return;

        ref readonly TransformComponent transform =
            ref entity.Get<TransformComponent>();

        EnsurePool(entity, emitter.MaxParticles);

        SpawnParticles(entity, in emitter, in transform, dt);
        SimulatePool(_pools[entity], in emitter, dt);
    }

    // ── Post-update cleanup ──────────────────────────────────────────────────

    protected override void PostUpdate(float state)
    {
        List<Entity>? toRemove = null;

        foreach (Entity key in _pools.Keys)
        {
            if (!key.IsAlive
                || !key.Has<ParticleEmitterComponent>()
                || !key.Has<TransformComponent>())
            {
                toRemove ??= new List<Entity>();
                toRemove.Add(key);
            }
        }

        if (toRemove is null)
            return;

        foreach (Entity key in toRemove)
        {
            _pools.Remove(key);
            _accumulators.Remove(key);
        }
    }

    // ── Pool management ──────────────────────────────────────────────────────

    private void EnsurePool(in Entity entity, int maxParticles)
    {
        if (_pools.TryGetValue(entity, out Particle[]? existing))
        {
            if (existing.Length == maxParticles)
                return;

            Particle[] resized = new Particle[maxParticles];
            Array.Copy(existing, resized, Math.Min(existing.Length, maxParticles));
            _pools[entity] = resized;
        }
        else
        {
            _pools[entity]        = new Particle[maxParticles];
            _accumulators[entity] = 0f;
        }
    }

    // ── Spawn ────────────────────────────────────────────────────────────────

    private void SpawnParticles(
        in Entity                   entity,
        in ParticleEmitterComponent emitter,
        in TransformComponent       transform,
        float                       dt)
    {
        _accumulators.TryGetValue(entity, out float acc);
        acc += emitter.EmitRate * dt;

        int toSpawn = (int)MathF.Floor(acc);
        acc -= toSpawn;
        _accumulators[entity] = acc;

        if (toSpawn <= 0)
            return;

        Particle[] pool      = _pools[entity];
        int        poolLen   = pool.Length;
        int        spawned   = 0;
        float      spreadRad = emitter.Spread * (MathF.PI / 180f);

        Vector3 dir = emitter.Direction == Vector3.Zero
            ? Vector3.UnitY
            : Vector3.Normalize(emitter.Direction);

        for (int i = 0; i < poolLen && spawned < toSpawn; i++)
        {
            if (pool[i].Alive)
                continue;

            float   speedMult = RangeRandom(0.8f, 1.2f);
            float   lifeMult  = RangeRandom(0.8f, 1.2f);
            Vector3 spawnDir  = RandomInCone(dir, spreadRad);

            pool[i] = new Particle
            {
                Position = transform.Position,
                Velocity = spawnDir * (emitter.Speed * speedMult),
                Color    = emitter.ColorStart,
                Size     = emitter.SizeStart,
                Age      = 0f,
                Lifetime = emitter.Lifetime * lifeMult,
                Alive    = true,
            };

            spawned++;
        }
    }

    // ── Simulation ───────────────────────────────────────────────────────────

    private static void SimulatePool(
        Particle[]                  pool,
        in ParticleEmitterComponent emitter,
        float                       dt)
    {
        int poolLen = pool.Length;

        for (int i = 0; i < poolLen; i++)
        {
            if (!pool[i].Alive)
                continue;

            pool[i].Age += dt;

            if (pool[i].Age >= pool[i].Lifetime)
            {
                pool[i].Alive = false;
                continue;
            }

            float t = pool[i].Age / pool[i].Lifetime;

            pool[i].Position += pool[i].Velocity * dt;
            pool[i].Color     = Vector4.Lerp(emitter.ColorStart, emitter.ColorEnd, t);
            pool[i].Size      = emitter.SizeStart + ((emitter.SizeEnd - emitter.SizeStart) * t);
        }
    }

    // ── Math helpers ─────────────────────────────────────────────────────────

    private Vector3 RandomInCone(Vector3 dir, float halfAngleRad)
    {
        // Uniform distribution on a spherical cap within halfAngleRad of dir.
        double cosMax   = Math.Cos(halfAngleRad);
        double cosTheta = cosMax + _rng.NextDouble() * (1.0 - cosMax);
        double sinTheta = Math.Sqrt(1.0 - cosTheta * cosTheta);
        double phi      = _rng.NextDouble() * 2.0 * Math.PI;

        // Build an orthonormal frame: dir = Z, perpX = X, perpY = Y
        Vector3 perpX = MathF.Abs(dir.X) < 0.9f
            ? Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitX))
            : Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY));
        Vector3 perpY = Vector3.Cross(dir, perpX);

        return Vector3.Normalize(
            (float)(sinTheta * Math.Cos(phi)) * perpX +
            (float)(sinTheta * Math.Sin(phi)) * perpY +
            (float)cosTheta                   * dir);
    }

    private float RangeRandom(float min, float max) =>
        min + (float)_rng.NextDouble() * (max - min);
}

// Назначение:   Система обновления частиц — спаун, симуляция и хранение пулов по сущности
// Зависит от:   DefaultEcs, ParticleEmitterComponent, TransformComponent, System.Numerics
// Используется: ParticleRenderSystem (GetParticles), SystemRunner / GameLoop
