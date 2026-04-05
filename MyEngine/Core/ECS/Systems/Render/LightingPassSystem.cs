#nullable enable

using System.Numerics;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;

namespace MyEngine.Core.ECS.Systems.Render;

public sealed class LightingPassSystem : ISystem<float>
{
    private readonly IRenderer _renderer;

    // Ambient и fog выставляются снаружи до вызова Update.
    public Vector3 AmbientColor { get; set; } = new Vector3(0.05f, 0.05f, 0.05f);
    public Vector3 FogColor     { get; set; } = new Vector3(0.7f, 0.75f, 0.8f);
    public float   FogDensity   { get; set; } = 0.002f;

    public bool IsEnabled { get; set; } = true;

    public LightingPassSystem(IRenderer renderer)
    {
        _renderer = renderer;
    }

    public void Update(float state)
    {
        // Lighting pass запускается внутри Dx12Renderer.EndFrame автоматически.
        // Система передаёт актуальные параметры ambient и fog перед EndFrame,
        // чтобы CBV рендерера был обновлён к моменту исполнения пасса.
        _renderer.SetAmbient(AmbientColor);
        _renderer.SetFog(FogColor, FogDensity);
    }

    public void Dispose() { }
}

// Назначение:   Передаёт параметры ambient/fog в рендерер перед lighting pass в EndFrame
// Зависит от:   IRenderer, DefaultEcs.System.ISystem, System.Numerics
// Используется: RenderPipeline, EngineLoop
