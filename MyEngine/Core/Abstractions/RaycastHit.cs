#nullable enable

using System.Numerics;

namespace MyEngine.Core.Abstractions;

/// <summary>
/// Результат пересечения луча с физическим телом.
/// </summary>
public readonly struct RaycastHit
{
    public Vector3           Point    { get; init; }
    public Vector3           Normal   { get; init; }
    public float             Distance { get; init; }
    public PhysicsBodyHandle Body     { get; init; }
}

// Назначение:   Хранит результат raycast-запроса — точку удара, нормаль, дистанцию и хэндл тела
// Зависит от:   PhysicsBodyHandle
// Используется: IPhysicsBackend, PhysicsSystem, любой код, выполняющий raycast
