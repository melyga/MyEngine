#nullable enable

using DefaultEcs.System;
using MyEngine.Core.Abstractions;

namespace MyEngine.Core.ECS.Systems.Render;

public sealed class PostProcessSystem : ISystem<float>
{
    private readonly IRenderer _renderer;
    private float _time;

    public bool IsEnabled { get; set; } = true;

    public PostProcessSystem(IRenderer renderer)
    {
        _renderer = renderer;
        _time     = 0f;
    }

    public void Update(float state)
    {
        _time += state;
        // fix: используем SetTime вместо SetFog-хака
        // SetTime добавлен в IRenderer в этом же блоке исправлений
        _renderer.SetTime(_time);
    }

    public void Dispose() { }
}

// Назначение:   Накапливает игровое время и передаёт в рендерер через SetTime для post-process CBV
// Зависит от:   IRenderer.SetTime, DefaultEcs.System.ISystem
// Используется: SystemGroup (Render-группа)
