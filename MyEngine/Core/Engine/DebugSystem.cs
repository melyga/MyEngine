#nullable enable

namespace MyEngine.Core.Engine;

/// <summary>
/// Управляет состоянием debug-оверлея и консоли.
/// Полная реализация (FPS overlay, профилировщик, команды) — в Debug/DebugSystem.cs Блока 9.
/// </summary>
public sealed class DebugSystem
{
    public bool ShowOverlay { get; set; }
    public bool ShowConsole { get; set; }

    public void ExecuteCommand(string cmd)
    {
        // STUB: команды r.wireframe, physics.debug, timescale и т.д. — Блок 9
        _ = cmd;
    }
}

// Назначение:   Хранит состояние debug-оверлея, консоли и диспетчер команд
// Зависит от:   —
// Используется: EngineContext.Debug, DebugRenderSystem
