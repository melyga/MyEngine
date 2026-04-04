#nullable enable

using System;
using System.Numerics;
using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.ECS.Components;

namespace MyEngine.Core.ECS.Systems.Update;

public sealed class CameraSystem : AEntitySetSystem<float>
{
    private readonly InputManager _input;
    private readonly float        _aspectRatio;

    private float _yaw;
    private float _pitch;
    private const float Sensitivity = 0.1f;

    // fix: добавлены ViewMatrix и ProjectionMatrix для GeometryPassSystem
    public Matrix4x4 ViewMatrix       { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 ProjectionMatrix { get; private set; } = Matrix4x4.Identity;
    public Vector3   Forward          { get; private set; } = -Vector3.UnitZ;
    public Vector3   Right            { get; private set; } =  Vector3.UnitX;
    public Vector3   Up               { get; private set; } =  Vector3.UnitY;
    public Vector3   Position         { get; private set; }

    public CameraSystem(World world, InputManager input, float aspectRatio)
        : base(world.GetEntities()
                    .With<PlayerTag>()
                    .With<CameraComponent>()
                    .With<TransformComponent>()
                    .AsSet())
    {
        _input       = input;
        _aspectRatio = aspectRatio;
    }

    protected override void Update(float deltaTime, in Entity entity)
    {
        _yaw   += _input.MouseDelta.X * Sensitivity;
        _pitch  = Math.Clamp(_pitch + _input.MouseDelta.Y * Sensitivity, -89f, 89f);

        float yawRad   = _yaw   * MathF.PI / 180f;
        float pitchRad = _pitch * MathF.PI / 180f;

        Vector3 forward = Vector3.Normalize(new Vector3(
            MathF.Cos(pitchRad) * MathF.Sin(yawRad),
            MathF.Sin(pitchRad),
            MathF.Cos(pitchRad) * MathF.Cos(yawRad)));

        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up    = Vector3.Cross(right, forward);

        ref readonly TransformComponent transform = ref entity.Get<TransformComponent>();
        Vector3 pos = transform.Position;

        ref CameraComponent camera = ref entity.Get<CameraComponent>();

        Matrix4x4 view = Matrix4x4.CreateLookAt(pos, pos + forward, up);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
            camera.Fov * MathF.PI / 180f, _aspectRatio, camera.NearPlane, camera.FarPlane);

        camera.ViewMatrix       = view;
        camera.ProjectionMatrix = proj;

        // fix: сохраняем как публичные свойства для GeometryPassSystem
        ViewMatrix       = view;
        ProjectionMatrix = proj;
        Forward          = forward;
        Right            = right;
        Up               = up;
        Position         = pos;
    }
}

// Назначение:   Обновляет ViewMatrix и ProjectionMatrix камеры из мышиного ввода и позиции игрока
// Зависит от:   DefaultEcs, CameraComponent, TransformComponent, PlayerTag, InputManager
// Используется: GeometryPassSystem, InputSystem, ViewmodelSystem
