#nullable enable

using System.Numerics;

namespace MyEngine.Core.ECS.Components;

public struct ParticleEmitterComponent
{
    public int     MaxParticles;
    public float   EmitRate;
    public float   Lifetime;
    public float   Speed;
    public Vector3 Direction;
    public float   Spread;
    public Vector4 ColorStart;
    public Vector4 ColorEnd;
    public float   SizeStart;
    public float   SizeEnd;
    public bool    IsLooping;
    public bool    IsPlaying;

    // fix: метод Default() заменён на свойство Default — единообразно с остальными компонентами
    public static ParticleEmitterComponent Default => new()
    {
        MaxParticles = 1000,
        EmitRate     = 10f,
        Lifetime     = 2f,
        Speed        = 1f,
        Direction    = Vector3.UnitY,
        Spread       = 15f,
        ColorStart   = Vector4.One,
        ColorEnd     = new Vector4(1f, 1f, 1f, 0f),
        SizeStart    = 0.1f,
        SizeEnd      = 0.0f,
        IsLooping    = true,
        IsPlaying    = false,
    };
}

// Назначение:   Компонент-эмиттер частиц — хранит параметры спауна и визуализации частиц
// Зависит от:   System.Numerics
// Используется: ParticleSystem, ParticleRenderSystem
