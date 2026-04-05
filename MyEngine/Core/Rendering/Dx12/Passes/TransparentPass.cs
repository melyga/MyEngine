#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using MyEngine.Core.Abstractions;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

namespace MyEngine.Core.Rendering.Dx12.Passes;

internal sealed unsafe class TransparentPass : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SceneConstantBuffer
    {
        public Matrix4x4 World;
        public Matrix4x4 View;
        public Matrix4x4 Projection;
        public Matrix4x4 WorldInverseTranspose;
        public Vector4 BaseColorFactor;
        public float Metallic;
        public float Roughness;
        public Vector3 EmissiveFactor;
        public float _pad;
    }

    private readonly MeshRegistry _meshRegistry;
    private readonly TextureRegistry _textureRegistry;
    private readonly GpuBuffer<SceneConstantBuffer> _sceneBuffer;

    private ComPtr<ID3D12PipelineState> _pipelineState;
    private ComPtr<ID3D12RootSignature> _rootSignature;
    private bool _disposed;

    public TransparentPass(
        RenderContext ctx,
        ShaderCompiler shaderCompiler,
        MeshRegistry meshRegistry,
        TextureRegistry textureRegistry)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (shaderCompiler is null) throw new ArgumentNullException(nameof(shaderCompiler));
        if (meshRegistry is null) throw new ArgumentNullException(nameof(meshRegistry));
        if (textureRegistry is null) throw new ArgumentNullException(nameof(textureRegistry));

        _meshRegistry = meshRegistry;
        _textureRegistry = textureRegistry;
        _sceneBuffer = GpuBuffer<SceneConstantBuffer>.CreateConstant(ctx, 1);

        string vsPath = ResolveShaderPath("gbuffer.vs.hlsl");
        string psPath = ResolveShaderPath("gbuffer.ps.hlsl");

        IDxcBlob* vsBlob = shaderCompiler.Compile(vsPath, "VSMain", "vs_6_0");
        IDxcBlob* psBlob = shaderCompiler.Compile(psPath, "PSMain", "ps_6_0");

        nint pos = SilkMarshal.StringToPtr("POSITION", NativeStringEncoding.UTF8);
        nint nrm = SilkMarshal.StringToPtr("NORMAL", NativeStringEncoding.UTF8);
        nint uv0 = SilkMarshal.StringToPtr("TEXCOORD", NativeStringEncoding.UTF8);
        nint tan = SilkMarshal.StringToPtr("TANGENT", NativeStringEncoding.UTF8);
        nint wei = SilkMarshal.StringToPtr("BLENDWEIGHTS", NativeStringEncoding.UTF8);
        nint idx = SilkMarshal.StringToPtr("BLENDINDICES", NativeStringEncoding.UTF8);

        try
        {
            InputElementDesc[] inputLayout =
            [
                new InputElementDesc((byte*)pos, 0, Format.FormatR32G32B32Float,    0, 0,  InputClassification.PerVertexData, 0),
                new InputElementDesc((byte*)nrm, 0, Format.FormatR32G32B32Float,    0, 12, InputClassification.PerVertexData, 0),
                new InputElementDesc((byte*)uv0, 0, Format.FormatR32G32Float,       0, 24, InputClassification.PerVertexData, 0),
                new InputElementDesc((byte*)tan, 0, Format.FormatR32G32B32A32Float, 0, 32, InputClassification.PerVertexData, 0),
                new InputElementDesc((byte*)wei, 0, Format.FormatR32G32B32A32Float, 0, 48, InputClassification.PerVertexData, 0),
                new InputElementDesc((byte*)idx, 0, Format.FormatR32G32B32A32Sint,  0, 64, InputClassification.PerVertexData, 0),
            ];

            PipelineBuilder.PipelineBuildResult build = new PipelineBuilder()
                .SetVertexShader(vsBlob)
                .SetPixelShader(psBlob)
                .SetInputLayout(inputLayout)
                .SetRenderTargetFormats(Format.FormatR8G8B8A8Unorm)
                .SetDepthFormat(Format.FormatD32Float)
                .EnableDepthTest(writeDepth: false)
                .SetBlendAlpha()
                .SetCullMode(CullMode.Back)
                .Build(ctx);

            _pipelineState = new ComPtr<ID3D12PipelineState>(build.PipelineState);
            _rootSignature = new ComPtr<ID3D12RootSignature>(build.RootSignature);
        }
        finally
        {
            SilkMarshal.Free(pos);
            SilkMarshal.Free(nrm);
            SilkMarshal.Free(uv0);
            SilkMarshal.Free(tan);
            SilkMarshal.Free(wei);
            SilkMarshal.Free(idx);
        }
    }

    public void Render(
        ID3D12GraphicsCommandList* cmd,
        IReadOnlyList<DrawCall> drawCalls,
        CameraData camera,
        CpuDescriptorHandle backBufferRtv,
        CpuDescriptorHandle depthDsv,
        int width,
        int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cmd is null) throw new ArgumentNullException(nameof(cmd));
        if (drawCalls is null) throw new ArgumentNullException(nameof(drawCalls));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        Viewport viewport = new(0f, 0f, width, height, 0f, 1f);
        Box2D<int> scissor = new(0, 0, width, height);

        cmd->RSSetViewports(1, ref viewport);
        cmd->RSSetScissorRects(1, ref scissor);
        cmd->OMSetRenderTargets(1, &backBufferRtv, false, &depthDsv);

        cmd->SetPipelineState(_pipelineState.Handle);
        cmd->SetGraphicsRootSignature(_rootSignature.Handle);
        cmd->IASetPrimitiveTopology((D3DPrimitiveTopology)4u);

        for (int i = 0; i < drawCalls.Count; i++)
        {
            DrawCall drawCall = drawCalls[i];
            (VertexBufferView vbv, IndexBufferView ibv, int indexCount) = _meshRegistry.GetBuffers(drawCall.Mesh);

            Matrix4x4 worldInvTranspose = Matrix4x4.Identity;
            if (Matrix4x4.Invert(drawCall.WorldMatrix, out Matrix4x4 worldInv))
                worldInvTranspose = Matrix4x4.Transpose(worldInv);

            SceneConstantBuffer cb = new()
            {
                World = drawCall.WorldMatrix,
                View = camera.View,
                Projection = camera.Projection,
                WorldInverseTranspose = worldInvTranspose,
                BaseColorFactor = drawCall.BaseColor,
                Metallic = drawCall.Metallic,
                Roughness = drawCall.Roughness,
                EmissiveFactor = drawCall.EmissiveFactor,
                _pad = 0f,
            };

            _sceneBuffer.Update(MemoryMarshal.CreateReadOnlySpan(ref cb, 1));
            cmd->SetGraphicsRootConstantBufferView(0, _sceneBuffer.GpuAddress);
            cmd->SetGraphicsRootDescriptorTable(2, _textureRegistry.GetSRV(drawCall.Albedo));

            cmd->IASetVertexBuffers(0, 1, ref vbv);
            cmd->IASetIndexBuffer(ref ibv);
            cmd->DrawIndexedInstanced((uint)indexCount, 1, 0, 0, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _rootSignature.Dispose();
        _pipelineState.Dispose();
        _sceneBuffer.Dispose();
    }

    private static string ResolveShaderPath(string fileName)
    {
        if (File.Exists(fileName))
            return Path.GetFullPath(fileName);

        string current = Directory.GetCurrentDirectory();
        string fromCurrent = Path.Combine(current, fileName);
        if (File.Exists(fromCurrent))
            return fromCurrent;

        foreach (string path in Directory.EnumerateFiles(current, fileName, SearchOption.AllDirectories))
            return path;

        string baseDir = AppContext.BaseDirectory;
        string fromBase = Path.Combine(baseDir, fileName);
        if (File.Exists(fromBase))
            return fromBase;

        foreach (string path in Directory.EnumerateFiles(baseDir, fileName, SearchOption.AllDirectories))
            return path;

        throw new FileNotFoundException($"Shader file not found: {fileName}");
    }
}

// Назначение:   Рендер прозрачной геометрии в forward PBR с alpha-blend PSO на тех же шейдерах, что и GeometryPass.
// Зависит от:   RenderContext, PipelineBuilder, ShaderCompiler, MeshRegistry, TextureRegistry, IRenderer.DrawCall/CameraData
// Используется: Dx12Renderer как прозрачный проход после deferred-lighting
