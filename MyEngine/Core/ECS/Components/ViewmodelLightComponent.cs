#nullable enable

namespace MyEngine.Core.ECS.Components;

public struct ViewmodelLightComponent
{
    public bool IsFlashlight;
    public bool IsOn;
    public float Intensity;
}

// Назначение:   Компонент источника света, привязанного к viewmodel (фонарик или точечный свет)
// Зависит от:   —
// Используется: MyEngine.Core.ECS.Systems (LightSystem, ViewmodelRenderSystem)
