#nullable enable

using System.Numerics;

namespace MyEngine.Core.ECS.Components;

public struct PointLightComponent
{
    public Vector3 Color;
    public float   Radius;
    public float   Intensity;
}

public struct SpotLightComponent
{
    public Vector3 Color;
    public Vector3 Direction;
    public float   InnerAngle;
    public float   OuterAngle;
    public float   Radius;
    public float   Intensity;
}

public struct DirectionalLightComponent
{
    public Vector3 Color;
    public Vector3 Direction;
    public float   Intensity;
}

// Назначение:   ECS-компоненты для точечного, конусного и направленного источников света
// Зависит от:   System.Numerics
// Используется: MyEngine.Core.Rendering, системы освещения, Sample.Game
