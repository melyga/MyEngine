#nullable enable

using MyEngine.Core.Engine;

namespace MyEngine.Core.Abstractions;

/// <summary>
/// Entry point contract for any game built on top of MyEngine.
/// Sample.Game implements this interface; Core never references the concrete type.
/// </summary>
public interface IGame
{
    /// <summary>Called once after the engine has fully initialised all subsystems.</summary>
    void OnLoad(EngineContext ctx);

    /// <summary>Called every frame. <paramref name="dt"/> is the elapsed time in seconds since the last frame.</summary>
    void OnUpdate(EngineContext ctx, float dt);

    /// <summary>Called at a fixed timestep for physics-driven or deterministic logic.</summary>
    void OnFixedUpdate(EngineContext ctx, float fixedDt);

    /// <summary>Called every frame after <see cref="OnUpdate"/>; intended for submitting draw commands.</summary>
    void OnRender(EngineContext ctx, float dt);

    /// <summary>Called once when the engine is shutting down; release game-side resources here.</summary>
    void OnShutdown(EngineContext ctx);
}

// Назначение:   Контракт точки входа для любой игры, реализующей логику поверх MyEngine.Core
// Зависит от:   MyEngine.Core.Engine.EngineContext
// Используется: Sample.Game (реализация), MyEngine.Core (EngineLoop запускает методы интерфейса)
