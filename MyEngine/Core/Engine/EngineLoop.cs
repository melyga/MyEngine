#nullable enable

using Silk.NET.Maths;
using Silk.NET.Windowing;
using MyEngine.Core.Abstractions;

namespace MyEngine.Core.Engine;

/// <summary>
/// Неизменяемая конфигурация окна и игрового цикла.
/// </summary>
public sealed record EngineConfig(
    string Title,
    int    Width,
    int    Height,
    bool   VSync,
    int    TargetFps);

/// <summary>
/// Главный игровой цикл на базе Silk.NET IWindow.
/// Создаёт и владеет окном, управляет fixed-timestep и variable-update,
/// делегирует рендер и логику через IGame, SystemGroup и IRenderer.
/// </summary>
public sealed class EngineLoop : IDisposable
{
    private const float FixedDt    = 1f / 60f;
    private const float MaxFrameDt = 0.1f;

    private readonly IGame         _game;
    private readonly EngineContext _ctx;
    private readonly SystemGroup   _systems;    // fix: добавлено поле
    private readonly IWindow       _window;

    private double _accumulator;
    private bool   _disposed;

    private EngineLoop(IGame game, EngineContext ctx, SystemGroup systems, IWindow window)
    {
        _game    = game;
        _ctx     = ctx;
        _systems = systems;    // fix: сохраняем
        _window  = window;

        _window.Load    += OnLoad;
        _window.Update  += OnUpdate;
        _window.Render  += OnRender;
        _window.Closing += OnClose;
    }

    // ── Публичная точка входа ────────────────────────────────────────────────

    /// <summary>
    /// Создаёт Silk.NET-окно, инициализирует движок и запускает блокирующий игровой цикл.
    /// Возвращает управление после закрытия окна.
    /// </summary>
    public static void Run(
        IGame            game,
        EngineContext    ctx,
        SystemGroup      systems,    // fix: добавлен параметр
        EngineConfig     config)
    {
        WindowOptions options = WindowOptions.Default with
        {
            Title                   = config.Title,
            Size                    = new Vector2D<int>(config.Width, config.Height),
            FramesPerSecond         = config.TargetFps,
            UpdatesPerSecond        = config.TargetFps,
            VSync                   = config.VSync,
            API                     = GraphicsAPI.None,  // DX12 управляется рендерером напрямую
            ShouldSwapAutomatically = false,
        };

        IWindow window = Window.Create(options);

        using EngineLoop loop = new(game, ctx, systems, window);  // fix: передаём systems
        window.Run();
    }

    // ── Обработчики Silk.NET ─────────────────────────────────────────────────

    private void OnLoad()
    {
        _game.OnLoad(_ctx);
    }

    private void OnUpdate(double dt)
    {
        float safeDt = Math.Min((float)dt, MaxFrameDt);

        _ctx.DeltaTime  = safeDt;
        _ctx.Time      += safeDt;
        _ctx.FrameCount++;

        // Fixed timestep — физика и детерминированная логика
        _accumulator += safeDt;
        while (_accumulator >= FixedDt)
        {
            _game.OnFixedUpdate(_ctx, FixedDt);
            _accumulator -= FixedDt;
        }

        // fix: вызываем Update-системы каждый кадр
        _systems.Update(safeDt);
        _game.OnUpdate(_ctx, safeDt);
    }

    private void OnRender(double dt)
    {
        _ctx.Renderer.BeginFrame();

        // fix: вызываем Render-системы (ShadowPass, GeometryPass и т.д.)
        _systems.Render((float)dt);

        _game.OnRender(_ctx, (float)dt);
        _ctx.Renderer.EndFrame();
    }

    private void OnClose()
    {
        _game.OnShutdown(_ctx);
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _window.Load    -= OnLoad;
        _window.Update  -= OnUpdate;
        _window.Render  -= OnRender;
        _window.Closing -= OnClose;

        _window.Dispose();
    }
}

// Назначение:   Главный игровой цикл — создаёт Silk.NET IWindow, управляет fixed-timestep, запускает системы и IGame
// Зависит от:   Silk.NET.Windowing, IGame, EngineContext, SystemGroup, IRenderer, EngineConfig
// Используется: EngineBuilder.Run<TGame>()
