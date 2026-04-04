#nullable enable

using MyEngine.Core.Engine;

namespace MyEngine.Core.Abstractions;

/// <summary>
/// Точка расширения движка: плагин регистрирует сервисы,
/// инициализируется после всех систем и корректно завершает работу.
/// </summary>
public interface IEnginePlugin
{
    /// <summary>Уникальное читаемое имя плагина.</summary>
    string Name { get; }

    /// <summary>
    /// Вызывается во время конфигурации, до создания любых систем.
    /// Используется для регистрации сервисов, фабрик и системных зависимостей.
    /// </summary>
    void Register(EngineBuilder builder);

    /// <summary>
    /// Вызывается после того, как все системы инициализированы и движок готов.
    /// Используется для подписки на события и пост-инициализации ресурсов.
    /// </summary>
    void Initialize(EngineContext ctx);

    /// <summary>
    /// Вызывается при остановке движка до уничтожения систем.
    /// Используется для освобождения ресурсов плагина.
    /// </summary>
    void Shutdown(EngineContext ctx);
}

// Назначение:   Контракт плагина — регистрация, инициализация, завершение
// Зависит от:   MyEngine.Core.Engine.EngineBuilder, MyEngine.Core.Engine.EngineContext
// Используется: EngineBuilder, Sample.Game
