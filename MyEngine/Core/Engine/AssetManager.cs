#nullable enable

using System.Collections.Generic;
using MyEngine.Core.Abstractions;

namespace MyEngine.Core.Engine;

public sealed class AssetManager : IAssetManager
{
    private readonly Dictionary<(System.Type Type, int Id), object> _assets = new();

    public void Register<T>(AssetHandle<T> handle, T asset) where T : class
    {
        if (handle.IsValid)
            _assets[(typeof(T), handle.Id)] = asset;
    }

    public bool TryGet<T>(AssetHandle<T> handle, out T? asset) where T : class
    {
        if (handle.IsValid && _assets.TryGetValue((typeof(T), handle.Id), out object? value))
        {
            asset = (T)value;
            return true;
        }

        asset = null;
        return false;
    }

    public T? Resolve<T>(AssetHandle<T> handle) where T : class =>
        TryGet(handle, out T? asset) ? asset : null;
}
