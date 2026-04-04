// MyEngine.Core/Abstractions/PhysicsBodyHandle.cs
#nullable enable

namespace MyEngine.Core.Abstractions;

public readonly struct PhysicsBodyHandle : IEquatable<PhysicsBodyHandle>
{
    internal int Id { get; }

    internal PhysicsBodyHandle(int id) => Id = id;

    public bool IsValid => Id >= 0;

    public static PhysicsBodyHandle Invalid => new(-1);

    public bool Equals(PhysicsBodyHandle other) => Id == other.Id;

    public override bool Equals(object? obj) => obj is PhysicsBodyHandle other && Equals(other);

    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString() => IsValid ? $"PhysicsBodyHandle({Id})" : "PhysicsBodyHandle(Invalid)";

    public static bool operator ==(PhysicsBodyHandle left, PhysicsBodyHandle right) => left.Equals(right);

    public static bool operator !=(PhysicsBodyHandle left, PhysicsBodyHandle right) => !left.Equals(right);
}

// Назначение:   Непрозрачный идентификатор физического тела, безопасно передаваемый через компоненты ECS
// Зависит от:   System
// Используется: MyEngine.Core.Components, MyEngine.Core.Physics.IPhysicsBackend
