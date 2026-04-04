#nullable enable

using MyEngine.Core.Abstractions;

namespace MyEngine.Core.ECS.Components;

public struct MeshComponent
{
    public GpuMeshHandle Mesh;
    public bool CastShadow;
}

// Назначение:   ECS-компонент, связывающий сущность с GPU-мешем и настройкой теней
// Зависит от:   GpuMeshHandle (MyEngine.Core.Abstractions)
// Используется: RenderSystem, ShadowSystem, MeshLoader
