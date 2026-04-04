#nullable enable

namespace MyEngine.Core.Abstractions;

public interface IAssetManager
{
    bool TryGet<T>(AssetHandle<T> handle, out T? asset) where T : class;
    T?   Resolve<T>(AssetHandle<T> handle)              where T : class;
}

// Назначение:   Контракт получения ассетов по handle для ECS-систем
// Зависит от:   AssetHandle<T>
// Используется: AnimationSystem, AudioSystem, AssetManager (реализует в Блоке 8)
