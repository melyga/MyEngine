#nullable enable

using System.Numerics;

namespace MyEngine.Core.Abstractions;

public readonly struct DrawCall
{
    public GpuMeshHandle    Mesh              { get; init; }
    public GpuTextureHandle Albedo            { get; init; }
    public GpuTextureHandle Normal            { get; init; }
    public GpuTextureHandle MetallicRoughness { get; init; }
    public GpuTextureHandle Emissive          { get; init; }
    public Matrix4x4        WorldMatrix       { get; init; }
    public float            Metallic          { get; init; }
    public float            Roughness         { get; init; }
    public Vector4          BaseColor         { get; init; }
    public Vector3          EmissiveFactor    { get; init; }
    public bool             IsShadowPass      { get; init; }
    public bool             IsViewmodelPass   { get; init; }
    public bool             IsTransparent     { get; init; }
}

public readonly struct UIDrawCall
{
    public GpuTextureHandle Texture  { get; init; }
    public Vertex[]         Vertices { get; init; }
    public uint[]           Indices  { get; init; }
    public Rect             Scissors { get; init; }
}

public readonly struct ParticleDrawCall
{
    public GpuTextureHandle   Texture       { get; init; }
    public int                InstanceCount { get; init; }
    public ParticleInstance[] Instances     { get; init; }
}

public readonly struct ParticleInstance
{
    public Vector3 Position { get; init; }
    public Vector4 Color    { get; init; }
    public float   Size     { get; init; }
}

public readonly struct CameraData
{
    public Matrix4x4 View       { get; init; }
    public Matrix4x4 Projection { get; init; }
    public Vector3   Position   { get; init; }
    public float     Fov        { get; init; }
    public float     Near       { get; init; }
    public float     Far        { get; init; }
}

public readonly struct LightData
{
    public enum LightType { Directional, Point, Spot }
    public LightType Type       { get; init; }
    public Vector3   Position   { get; init; }
    public Vector3   Direction  { get; init; }
    public Vector3   Color      { get; init; }
    public float     Intensity  { get; init; }
    public float     Radius     { get; init; }
    public float     InnerAngle { get; init; }
    public float     OuterAngle { get; init; }
}

public readonly struct Rect
{
    public float X      { get; init; }
    public float Y      { get; init; }
    public float Width  { get; init; }
    public float Height { get; init; }
    public Rect(float x, float y, float w, float h) { X=x; Y=y; Width=w; Height=h; }
}

public interface IRenderer : IDisposable
{
    void BeginFrame();
    void EndFrame();
    void Submit(in DrawCall call);
    void Submit(in UIDrawCall call);
    void Submit(in ParticleDrawCall call);
    GpuMeshHandle    LoadMesh(ReadOnlySpan<Vertex> verts, ReadOnlySpan<uint> indices);
    GpuTextureHandle LoadTexture(ReadOnlySpan<byte> rgba, int w, int h);
    void UnloadMesh(GpuMeshHandle h);
    void UnloadTexture(GpuTextureHandle h);
    void SetCamera(in CameraData camera);
    void SetLights(ReadOnlySpan<LightData> lights);
    void SetBoneMatrices(GpuMeshHandle mesh, ReadOnlySpan<Matrix4x4> bones);
    void SetAmbient(Vector3 color);
    void SetFog(Vector3 color, float density);
    void SetTime(float time);    // fix: добавлен для PostProcessSystem
    event Action<int,int> OnResized;
}

// Назначение:   Единственная точка входа рендеринга — изолирует всё DX12 за интерфейсом
// Зависит от:   GpuHandles, IModelLoader (Vertex), System.Numerics
// Используется: RenderSystem, ResourceManager, Sample.Game через DI
