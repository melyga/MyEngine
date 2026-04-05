#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using MyEngine.Core.Abstractions;
using Silk.NET.Direct3D12;

namespace MyEngine.Core.Rendering.Dx12;

public sealed unsafe class Dx12Renderer : IRenderer
{
    private const int SrvHeapCapacity = 4096;
    private const int RtvHeapCapacity = 64;
    private const int DsvHeapCapacity = 16;

    private readonly RenderContext _ctx;
    private readonly DescriptorHeap _srvHeap;
    private readonly DescriptorHeap _rtvHeap;
    private readonly DescriptorHeap _dsvHeap;
    private readonly GBuffer _gBuffer;
    private readonly MeshRegistry _meshRegistry;
    private readonly TextureRegistry _textureRegistry;
    private readonly ShaderCompiler _shaderCompiler;

    private readonly List<LightData> _lights = new();

    private readonly List<DrawCall> _drawQueue = new();
    private readonly List<UIDrawCall> _uiQueue = new();
    private readonly List<ParticleDrawCall> _particleQueue = new();

    private CameraData _camera;
    private Vector3 _ambientColor;
    private Vector3 _fogColor;
    private float _fogDensity;
    private float _time;

    private bool _frameBegun;
    private bool _disposed;

    public event Action<int, int>? OnResized;

    public Dx12Renderer()
    {
        _ctx = new RenderContext("MyEngine", 1280, 720, vsync: true);

        _srvHeap = new DescriptorHeap(_ctx, DescriptorHeapType.CbvSrvUav, SrvHeapCapacity, shaderVisible: true);
        _rtvHeap = new DescriptorHeap(_ctx, DescriptorHeapType.Rtv, RtvHeapCapacity, shaderVisible: false);
        _dsvHeap = new DescriptorHeap(_ctx, DescriptorHeapType.Dsv, DsvHeapCapacity, shaderVisible: false);

        _gBuffer = new GBuffer(_ctx, _rtvHeap, _dsvHeap, _srvHeap, _ctx.Width, _ctx.Height);
        _meshRegistry = new MeshRegistry(_ctx);
        _textureRegistry = new TextureRegistry(_ctx, _srvHeap);

        _shaderCompiler = new ShaderCompiler();
        CompileAllShadersAtStartup();

        _ambientColor = new Vector3(0.05f, 0.05f, 0.05f);
        _fogColor = new Vector3(0.7f, 0.75f, 0.8f);
        _fogDensity = 0.002f;
        _camera = new CameraData
        {
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.Identity,
            Position = Vector3.Zero,
            Fov = 90f,
            Near = 0.1f,
            Far = 500f,
        };
    }

    public void Submit(in DrawCall call)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _drawQueue.Add(call);
    }

    public void Submit(in UIDrawCall call)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _uiQueue.Add(call);
    }

    public void Submit(in ParticleDrawCall call)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _particleQueue.Add(call);
    }

    public GpuMeshHandle LoadMesh(ReadOnlySpan<Vertex> verts, ReadOnlySpan<uint> indices)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _meshRegistry.Register(_ctx, verts, indices);
    }

    public GpuTextureHandle LoadTexture(ReadOnlySpan<byte> rgba, int w, int h)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _textureRegistry.Register(_ctx, _srvHeap, rgba, w, h);
    }

    public void UnloadMesh(GpuMeshHandle h)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _meshRegistry.Unregister(h);
    }

    public void UnloadTexture(GpuTextureHandle h)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _textureRegistry.Unregister(h);
    }

    public void SetCamera(in CameraData camera)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _camera = camera;
    }

    public void SetLights(ReadOnlySpan<LightData> lights)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lights.Clear();
        for (int i = 0; i < lights.Length; i++)
            _lights.Add(lights[i]);
    }

    public void SetBoneMatrices(GpuMeshHandle mesh, ReadOnlySpan<Matrix4x4> bones)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _meshRegistry.SetBoneMatrices(mesh, bones);
    }

    public void SetAmbient(Vector3 color)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ambientColor = color;
    }

    public void SetFog(Vector3 color, float density)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _fogColor = color;
        _fogDensity = density;
    }

    public void SetTime(float time)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _time = time;
    }

    public void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _ctx.BeginFrame();
        _frameBegun = true;

        _drawQueue.Clear();
        _uiQueue.Clear();
        _particleQueue.Clear();
    }

    public void EndFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_frameBegun)
            BeginFrame();

        List<DrawCall> shadowCalls = FilterDrawCalls(static c => c.IsShadowPass);
        List<DrawCall> geometryCalls = FilterDrawCalls(static c => !c.IsShadowPass && !c.IsTransparent && !c.IsViewmodelPass);
        List<DrawCall> transparentCalls = FilterDrawCalls(static c => c.IsTransparent);
        List<DrawCall> viewmodelCalls = FilterDrawCalls(static c => c.IsViewmodelPass);

        RenderShadowPass(shadowCalls);

        _gBuffer.BeginGeometryPass(_ctx.CommandList);
        RenderGeometryPass(geometryCalls);
        _gBuffer.EndGeometryPass(_ctx.CommandList);

        RenderSsaoPass(_gBuffer.SRVHandles[1]);
        RenderLightingPass(_gBuffer, _lights);
        RenderTransparentPass(transparentCalls);
        RenderParticlePass(_particleQueue);
        RenderViewmodelPass(viewmodelCalls);
        RenderUiPass(_uiQueue);
        RenderPostProcessPass(_time);

        _ctx.EndFrame();
        _frameBegun = false;
    }

    public void Resize(int w, int h)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (w <= 0) throw new ArgumentOutOfRangeException(nameof(w));
        if (h <= 0) throw new ArgumentOutOfRangeException(nameof(h));

        _ctx.Resize(w, h);
        _gBuffer.Resize(w, h);
        OnResized?.Invoke(w, h);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shaderCompiler.Dispose();
        _textureRegistry.Dispose();
        _meshRegistry.Dispose();
        _gBuffer.Dispose();
        _dsvHeap.Dispose();
        _rtvHeap.Dispose();
        _srvHeap.Dispose();
        _ctx.Dispose();
    }

    private List<DrawCall> FilterDrawCalls(Func<DrawCall, bool> predicate)
    {
        var filtered = new List<DrawCall>();

        for (int i = 0; i < _drawQueue.Count; i++)
        {
            DrawCall call = _drawQueue[i];
            if (predicate(call))
                filtered.Add(call);
        }

        return filtered;
    }

    private void CompileAllShadersAtStartup()
    {
        foreach (string shaderPath in EnumerateShaderPaths())
        {
            string source = File.ReadAllText(shaderPath);

            if (source.Contains("VSMain(", StringComparison.Ordinal))
                _shaderCompiler.Compile(shaderPath, "VSMain", "vs_6_0");

            if (source.Contains("PSMain(", StringComparison.Ordinal))
                _shaderCompiler.Compile(shaderPath, "PSMain", "ps_6_0");

            if (source.Contains("CSMain(", StringComparison.Ordinal))
                _shaderCompiler.Compile(shaderPath, "CSMain", "cs_6_0");

            if (source.Contains("GSMain(", StringComparison.Ordinal))
                _shaderCompiler.Compile(shaderPath, "GSMain", "gs_6_0");

            if (source.Contains("HSMain(", StringComparison.Ordinal))
                _shaderCompiler.Compile(shaderPath, "HSMain", "hs_6_0");

            if (source.Contains("DSMain(", StringComparison.Ordinal))
                _shaderCompiler.Compile(shaderPath, "DSMain", "ds_6_0");
        }
    }

    private static IEnumerable<string> EnumerateShaderPaths()
    {
        string current = Directory.GetCurrentDirectory();

        if (Directory.Exists(current))
        {
            foreach (string shader in Directory.EnumerateFiles(current, "*.hlsl", SearchOption.AllDirectories))
                yield return shader;
        }

        string baseDir = AppContext.BaseDirectory;
        if (!string.Equals(baseDir, current, StringComparison.OrdinalIgnoreCase) && Directory.Exists(baseDir))
        {
            foreach (string shader in Directory.EnumerateFiles(baseDir, "*.hlsl", SearchOption.AllDirectories))
                yield return shader;
        }
    }

    private void RenderShadowPass(IReadOnlyList<DrawCall> shadowCalls)
    {
        _ = shadowCalls;
    }

    private void RenderGeometryPass(IReadOnlyList<DrawCall> geometryCalls)
    {
        _ = geometryCalls;
    }

    private void RenderSsaoPass(GpuDescriptorHandle normalSrv)
    {
        _ = normalSrv;
    }

    private void RenderLightingPass(GBuffer gbuffer, IReadOnlyList<LightData> lights)
    {
        _ = gbuffer;
        _ = lights;
        _ = _camera;
        _ = _ambientColor;
        _ = _fogColor;
        _ = _fogDensity;
    }

    private void RenderTransparentPass(IReadOnlyList<DrawCall> transparentCalls)
    {
        _ = transparentCalls;
    }

    private void RenderParticlePass(IReadOnlyList<ParticleDrawCall> particleCalls)
    {
        _ = particleCalls;
    }

    private void RenderViewmodelPass(IReadOnlyList<DrawCall> viewmodelCalls)
    {
        _ = viewmodelCalls;
    }

    private void RenderUiPass(IReadOnlyList<UIDrawCall> uiCalls)
    {
        _ = uiCalls;
    }

    private void RenderPostProcessPass(float time)
    {
        _ = time;
    }
}

// Назначение:   Основной DX12-рендерер: управляет кадром, ресурсными реестрами и порядком рендер-проходов через IRenderer.
// Зависит от:   IRenderer, RenderContext, DescriptorHeap, GBuffer, MeshRegistry, TextureRegistry, ShaderCompiler, System.Numerics
// Используется: EngineBuilder.WithRenderer<T>(), ECS Render/Update системы через интерфейс IRenderer
