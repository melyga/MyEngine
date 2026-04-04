#nullable enable

using System;

namespace MyEngine.Core.Abstractions;

public readonly struct GpuMeshHandle : IEquatable<GpuMeshHandle>
{
    internal int Id { get; }

    internal GpuMeshHandle(int id) => Id = id;

    public bool IsValid => Id >= 0;

    public static GpuMeshHandle Invalid => new(-1);

    public bool Equals(GpuMeshHandle other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is GpuMeshHandle other && Equals(other);

    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString() => IsValid ? $"GpuMeshHandle({Id})" : "GpuMeshHandle(Invalid)";

    public static bool operator ==(GpuMeshHandle left, GpuMeshHandle right) => left.Equals(right);

    public static bool operator !=(GpuMeshHandle left, GpuMeshHandle right) => !left.Equals(right);
}

public readonly struct GpuTextureHandle : IEquatable<GpuTextureHandle>
{
    internal int Id { get; }

    internal GpuTextureHandle(int id) => Id = id;

    public bool IsValid => Id >= 0;

    public static GpuTextureHandle Invalid => new(-1);

    public bool Equals(GpuTextureHandle other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is GpuTextureHandle other && Equals(other);

    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString() => IsValid ? $"GpuTextureHandle({Id})" : "GpuTextureHandle(Invalid)";

    public static bool operator ==(GpuTextureHandle left, GpuTextureHandle right) => left.Equals(right);

    public static bool operator !=(GpuTextureHandle left, GpuTextureHandle right) => !left.Equals(right);
}

// Назначение:   Типобезопасные хэндлы GPU-ресурсов для передачи между слоями без утечки DX12-объектов
// Зависит от:   System (IEquatable<T>)
// Используется: MeshComponent, MaterialComponent, Rendering-системы, ResourceManager
