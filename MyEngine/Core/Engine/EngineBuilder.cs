#nullable enable

using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;

namespace MyEngine.Core.Engine;

/// <summary>
/// Конфигуратор движка: собирает параметры запуска, регистрирует плагины и системы,
/// затем запускает движок через <see cref="Run{TGame}"/>.
/// </summary>
public sealed class EngineBuilder
{
    private string _title     = "MyEngine";
    private int    _width     = 1280;
    private int    _height    = 720;
    private bool   _vsync     = true;
    private int    _targetFps = 60;

    private Func<IRenderer>?       _rendererFactory;
    private Func<IPhysicsBackend>? _physicsFactory;

    private readonly List<Func<IModelLoader>>  _modelLoaderFactories = new();
    private readonly List<IEnginePlugin>        _plugins              = new();
    private readonly List<Func<ISystem<float>>> _systemFactories      = new();

    // ── Fluent API ────────────────────────────────────────────────────────────

    public EngineBuilder WithTitle(string title)
    {
        _title = title;
        return this;
    }

    public EngineBuilder WithResolution(int width, int height)
    {
        _width  = width;
        _height = height;
        return this;
    }

    public EngineBuilder WithVSync(bool enabled)
    {
        _vsync = enabled;
        return this;
    }

    public EngineBuilder WithTargetFps(int fps)
    {
        _targetFps = fps;
        return this;
    }

    public EngineBuilder WithRenderer<T>() where T : IRenderer, new()
    {
        _rendererFactory = static () => new T();
        return this;
    }

    public EngineBuilder WithPhysics<T>() where T : IPhysicsBackend, new()
    {
        _physicsFactory = static () => new T();
        return this;
    }

    public EngineBuilder WithModelLoader<T>() where T : IModelLoader, new()
    {
        _modelLoaderFactories.Add(static () => new T());
        return this;
    }

    public EngineBuilder WithPlugin<T>() where T : IEnginePlugin, new()
    {
        _plugins.Add(new T());
        return this;
    }

    public EngineBuilder AddSystem<T>() where T : ISystem<float>, new()
    {
        _systemFactories.Add(static () => new T());
        return this;
    }

    // ── Run ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Инициализирует все подсистемы, регистрирует плагины и запускает игровой цикл.
    /// Возвращает управление после завершения цикла.
    /// </summary>
    public void Run<TGame>() where TGame : IGame, new()
    {
        // 1. Создать рендерер и физический бэкенд
        IRenderer renderer = _rendererFactory is not null
            ? _rendererFactory()
            : throw new InvalidOperationException(
                "IRenderer не зарегистрирован. Вызовите WithRenderer<T>() перед Run().");

        IPhysicsBackend physics = _physicsFactory is not null
            ? _physicsFactory()
            : throw new InvalidOperationException(
                "IPhysicsBackend не зарегистрирован. Вызовите WithPhysics<T>() перед Run().");

        // 2. Создать ECS World
        using World world = new();

        // 3. Зарегистрировать загрузчики моделей
        ModelLoaderRegistry loaderRegistry = new();
        foreach (Func<IModelLoader> factory in _modelLoaderFactories)
            loaderRegistry.Register(factory());

        // 4. Дать плагинам возможность зарегистрировать доп. сервисы
        foreach (IEnginePlugin plugin in _plugins)
            plugin.Register(this);

        // 5. Создать SystemGroup и наполнить кастомными системами из AddSystem<T>
        SystemGroup systemGroup = new();
        foreach (Func<ISystem<float>> factory in _systemFactories)
            systemGroup.AddUpdate(factory());

        // 6. Создать EngineContext
        EngineContext ctx = new(
            world:    world,
            renderer: renderer,
            physics:  physics,
            assets:   new AssetManager(),
            audio:    new AudioSystem(),
            ui:       new UISystem(),
            events:   new EventBus(),
            input:    new InputManager(),
            debug:    new DebugSystem(),
            timers:   new TimerSystem(),
            factory:  new EntityFactory());

        // 7. Инициализировать плагины (все системы уже созданы)
        foreach (IEnginePlugin plugin in _plugins)
            plugin.Initialize(ctx);

        // 8. Запустить цикл — EngineConfig объявлен в EngineLoop.cs
        EngineConfig config = new(
            Title:     _title,
            Width:     _width,
            Height:    _height,
            VSync:     _vsync,
            TargetFps: _targetFps);

        TGame game = new();

        try
        {
            // fix: передаём systemGroup — он вызывается внутри EngineLoop
            EngineLoop.Run(game, ctx, systemGroup, config);
        }
        finally
        {
            foreach (IEnginePlugin plugin in _plugins)
                plugin.Shutdown(ctx);

            systemGroup.Dispose();
            renderer.Dispose();
            physics.Dispose();
        }
    }
}

// ── ModelLoaderRegistry ───────────────────────────────────────────────────────

/// <summary>
/// Реестр загрузчиков моделей — выбирает нужный по расширению файла.
/// Полная реализация перенесётся в Assets/ в Блоке 8.
/// </summary>
public sealed class ModelLoaderRegistry
{
    private readonly List<IModelLoader> _loaders = new();

    public void Register(IModelLoader loader) => _loaders.Add(loader);

    public ModelData Load(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        foreach (IModelLoader loader in _loaders)
        {
            if (loader.CanLoad(ext))
                return loader.Load(path);
        }
        throw new NotSupportedException(
            $"Нет загрузчика для расширения '{ext}'. Зарегистрируйте IModelLoader через WithModelLoader<T>().");
    }

    public bool CanLoad(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        foreach (IModelLoader loader in _loaders)
        {
            if (loader.CanLoad(ext))
                return true;
        }
        return false;
    }
}

// Назначение:   Конфигуратор движка с Fluent API — собирает параметры, создаёт подсистемы, запускает цикл
// Зависит от:   IRenderer, IPhysicsBackend, IModelLoader, IEnginePlugin, ISystem<float>, EngineContext, SystemGroup, EngineLoop, DefaultEcs.World
// Используется: Sample.Game (точка входа Program.cs), тесты MyEngine.Tests
