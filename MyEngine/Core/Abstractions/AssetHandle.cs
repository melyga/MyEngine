#nullable enable

using System;

namespace MyEngine.Core.Abstractions;

public readonly struct AssetHandle<T> : IEquatable<AssetHandle<T>>
{
    internal int Id { get; }

    internal AssetHandle(int id) => Id = id;

    public bool IsValid => Id >= 0;

    public static AssetHandle<T> Invalid => new(-1);

    public static implicit operator bool(AssetHandle<T> h) => h.IsValid;

    public bool Equals(AssetHandle<T> other) => Id == other.Id;

    public override bool Equals(object? obj) =>
        obj is AssetHandle<T> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Id, typeof(T));

    public override string ToString() => $"AssetHandle<{typeof(T).Name}>({Id})";

    public static bool operator ==(AssetHandle<T> left, AssetHandle<T> right) =>
        left.Equals(right);

    public static bool operator !=(AssetHandle<T> left, AssetHandle<T> right) =>
        !left.Equals(right);
}

// Назначение:   Типобезопасный идентификатор загруженного ассета, не хранящий ссылок на GPU/физику
// Зависит от:   System
// Используется: IAssetManager, компоненты MeshComponent / TextureComponent / AudioComponent
