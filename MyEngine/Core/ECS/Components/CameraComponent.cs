using System.Numerics;

namespace MyEngine.Core.ECS.Components;

#nullable enable

public struct CameraComponent
{
    public float Fov;
    public float NearPlane;
    public float FarPlane;
    public Matrix4x4 ViewMatrix;
    public Matrix4x4 ProjectionMatrix;

    public static CameraComponent Default => new()
    {
        Fov = 90f,
        NearPlane = 0.1f,
        FarPlane = 500f,
        ViewMatrix = Matrix4x4.Identity,
        ProjectionMatrix = Matrix4x4.Identity,
    };
}

// Назначение:   Компонент камеры — хранит параметры проекции и матрицы вида/проекции
// Зависит от:   System.Numerics
// Используется: CameraSystem, RenderSystem
