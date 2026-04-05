#nullable enable

using System.Numerics;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;

namespace MyEngine.Core.ECS.Systems.Render;

public sealed class SsaoPassSystem : ISystem<float>
{
    private readonly IRenderer _renderer;

    public bool IsEnabled { get; set; } = true;

    public SsaoPassSystem(IRenderer renderer)
    {
        _renderer = renderer;
    }

    public void Update(float state)
    {
        // SSAO — внутренний pass рендерера.
        // Система отправляет сигнальный DrawCall с IsShadowPass=false,
        // чтобы Dx12Renderer знал: SSAO-буфер нужно обновить перед lighting pass.
        _renderer.Submit(new DrawCall
        {
            Mesh              = default,
            Albedo            = default,
            Normal            = default,
            MetallicRoughness = default,
            Emissive          = default,
            WorldMatrix       = Matrix4x4.Identity,
            Metallic          = 0f,
            Roughness         = 1f,
            BaseColor         = Vector4.One,
            EmissiveFactor    = Vector3.Zero,
            IsShadowPass      = false,
            IsViewmodelPass   = false,
            IsTransparent     = false,
        });
    }

    public void Dispose() { }
}

// Назначение:   Сигнализирует Dx12Renderer о необходимости SSAO-пасса через Submit
// Зависит от:   IRenderer, DefaultEcs.System.ISystem, System.Numerics
// Используется: RenderPipeline, EngineLoop
