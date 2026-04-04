#nullable enable

using System.Numerics;

namespace MyEngine.Core.Engine;

/// <summary>
/// Управляет активными Canvas объектами и инжектирует ввод.
/// Полная реализация (layout engine, widgets) — в Блоке 5 (UI/).
/// </summary>
public sealed class UISystem
{
    private readonly List<Canvas> _canvases = new();

    public IReadOnlyList<Canvas> ActiveCanvases => _canvases;

    public Canvas CreateCanvas()
    {
        var c = new Canvas();
        _canvases.Add(c);
        return c;
    }

    public void DestroyCanvas(Canvas c) => _canvases.Remove(c);

    public void InjectInput(Vector2 pos, bool click, bool down)
    {
        // Phase 5: forward events to focused widget
        _ = pos; _ = click; _ = down;
    }
}

/// <summary>STUB — корневой контейнер UI. Полная реализация в Блоке 5.</summary>
public sealed class Canvas { }

// Назначение:   Управляет Canvas объектами и инжектирует ввод в UI систему
// Зависит от:   System.Collections.Generic, System.Numerics
// Используется: EngineContext.UI, UIRenderSystem, Sample.Game
