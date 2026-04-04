#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.Engine;    // fix: UISystem живёт в Engine namespace

namespace MyEngine.Core.ECS.Systems.Render;

public sealed class UIRenderSystem : ISystem<float>
{
    private readonly IRenderer _renderer;
    private readonly UISystem  _ui;

    public bool IsEnabled { get; set; } = true;

    public UIRenderSystem(IRenderer renderer, UISystem ui)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _ui       = ui       ?? throw new ArgumentNullException(nameof(ui));
    }

    public void Update(float deltaTime)
    {
        foreach (Canvas canvas in _ui.ActiveCanvases)
        {
            // Phase 5: canvas.GetDrawCalls() → renderer.Submit(uiDrawCall)
            _ = canvas;
        }
    }

    public void Dispose() { }
}

// Назначение:   Отправляет UI draw-calls рендереру на основе активных канвасов
// Зависит от:   IRenderer, UISystem, DefaultEcs.System.ISystem
// Используется: SystemGroup (Render-группа)
