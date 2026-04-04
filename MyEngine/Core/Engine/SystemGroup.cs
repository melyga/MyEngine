#nullable enable

using DefaultEcs.System;

namespace MyEngine.Core.Engine;

/// <summary>
/// Группирует системы обновления и рендера, выполняя их последовательно.
/// </summary>
public sealed class SystemGroup : IDisposable
{
    private readonly List<ISystem<float>> _updateSystems = new();
    private readonly List<ISystem<float>> _renderSystems = new();

    public void AddUpdate(ISystem<float> system)
        => _updateSystems.Add(system);

    public void AddRender(ISystem<float> system)
        => _renderSystems.Add(system);

    public void Update(float dt)
    {
        foreach (ISystem<float> system in _updateSystems)
            system.Update(dt);
    }

    public void Render(float dt)
    {
        foreach (ISystem<float> system in _renderSystems)
            system.Update(dt);
    }

    public void Dispose()
    {
        foreach (ISystem<float> system in _updateSystems)
        {
            if (system is IDisposable d)
                d.Dispose();
        }

        foreach (ISystem<float> system in _renderSystems)
        {
            if (system is IDisposable d)
                d.Dispose();
        }

        _updateSystems.Clear();
        _renderSystems.Clear();
    }
}

// Назначение:   Упорядоченный контейнер систем обновления и рендера с единым Dispose
// Зависит от:   DefaultEcs.System.ISystem<T>
// Используется: EngineLoop, EngineBuilder — регистрация и запуск систем по фазам
