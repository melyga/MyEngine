#nullable enable

using DefaultEcs;
using MyEngine.Core.Abstractions;
using MyEngine.Core.ECS.Systems.Update;

namespace MyEngine.Core.Engine;

public sealed class EngineContext
{
    public World           EcsWorld   { get; }
    public IRenderer       Renderer   { get; }
    public IPhysicsBackend Physics    { get; }
    public IAssetManager   Assets     { get; }
    public UISystem        UI         { get; }   // fix: реальный класс из Engine/UISystem.cs
    public EventBus        Events     { get; }
    public InputManager    Input      { get; }
    public DebugSystem     Debug      { get; }   // fix: реальный класс из Engine/DebugSystem.cs
    public TimerSystem     Timers     { get; }
    public EntityFactory   Factory    { get; }

    public float Time       { get; internal set; }
    public float DeltaTime  { get; internal set; }
    public int   FrameCount { get; internal set; }

    public EngineContext(
        World world, IRenderer renderer, IPhysicsBackend physics,
        IAssetManager assets, UISystem ui, EventBus events,
        InputManager input, DebugSystem debug, TimerSystem timers,
        EntityFactory factory)
    {
        EcsWorld = world; Renderer = renderer; Physics = physics;
        Assets   = assets; UI     = ui;       Events  = events;
        Input    = input;  Debug  = debug;    Timers  = timers;
        Factory  = factory;
    }
}

public sealed class EventBus {
    public void Publish<T>(T evt)           { }
    public void Subscribe<T>(Action<T> h)   { }
    public void Unsubscribe<T>(Action<T> h) { }
}

public sealed class EntityFactory{ }

// Назначение:   Контекст движка — единая точка доступа ко всем подсистемам
// Зависит от:   DefaultEcs.World, IRenderer, IPhysicsBackend, IAssetManager, UISystem, DebugSystem, InputManager
// Используется: IGame, IEnginePlugin, EngineLoop, все ECS системы
