#nullable enable

using System.Numerics;
using MyEngine.Core.Abstractions;

namespace MyEngine.Core.ECS.Components;

public struct MaterialComponent
{
    public GpuTextureHandle Albedo;
    public GpuTextureHandle Normal;
    public GpuTextureHandle MetallicRoughness;
    public GpuTextureHandle Emissive;
    public float MetallicFactor;
    public float RoughnessFactor;
    public Vector4 BaseColorFactor;
    public Vector3 EmissiveFactor;
}

// Назначение:   ECS-компонент, описывающий PBR-материал сущности через GPU-хэндлы и скалярные факторы
// Зависит от:   GpuHandles.cs (GpuTextureHandle), System.Numerics (Vector3, Vector4)
// Используется: MeshRenderSystem, MaterialSystem, ResourceManager, Sample.Game
