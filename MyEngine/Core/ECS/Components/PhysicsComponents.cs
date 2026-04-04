// MyEngine.Core/ECS/Components/PhysicsComponents.cs
#nullable enable

using System.Numerics;
using MyEngine.Core.Abstractions;

namespace MyEngine.Core.ECS.Components;

public struct RigidBodyComponent
{
    public PhysicsBodyHandle Body;
    public bool IsKinematic;
}

public struct CharacterComponent
{
    public float MoveSpeed;
    public float JumpImpulse;
    public float StepHeight;
    public float SlopeLimit;
    public bool IsGrounded;
    public bool IsCrouching;
    public Vector3 MoveDirection;

    public static CharacterComponent Default => new()
    {
        MoveSpeed    = 4.5f,
        JumpImpulse  = 5.5f,
        StepHeight   = 0.35f,
        SlopeLimit   = 45f,
        IsGrounded   = false,
        IsCrouching  = false,
        MoveDirection = Vector3.Zero
    };
}

// Назначение:   ECS-компоненты физического тела и персонажа для передачи между системами через DefaultEcs
// Зависит от:   MyEngine.Core.Abstractions.PhysicsBodyHandle, System.Numerics
// Используется: MyEngine.Core.Physics, MyEngine.Core.ECS.Systems.CharacterSystem, MyEngine.Core.ECS.Systems.InputSystem
