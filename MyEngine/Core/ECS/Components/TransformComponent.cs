using System.Numerics;

namespace MyEngine.Core.ECS.Components;

#nullable enable

public struct TransformComponent
{
    public Vector3    Position;
    public Quaternion Rotation;
    public Vector3    Scale;
    public Matrix4x4  WorldMatrix;

    public static TransformComponent Default =>
        new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
}

// Назначение:   Хранит позицию, вращение, масштаб и мировую матрицу трансформации сущности
// Зависит от:   System.Numerics
// Используется: TransformSystem, RenderSystem, PhysicsSyncSystem
