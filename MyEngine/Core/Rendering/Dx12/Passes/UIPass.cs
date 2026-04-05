#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MyEngine.Core.Abstractions;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Dx12Range = Silk.NET.Direct3D12.Range;

namespace MyEngine.Core.Rendering.Dx12.Passes;

internal sealed unsafe class UIPass : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UiConstants
    {
        public Matrix4x4 Projection;
    }

    private readonly RenderContext _ctx;
    private readonly TextureRegistry _textureRegistry;
    private readonly GpuBuffer<UiConstants> _constantBuffer;
    private readonly List<ComPtr<ID3D12Resource>> _frameUploadResources = new();

    private ComPtr<ID3D12PipelineState> _pipelineState;
    private ComPtr<ID3D12RootSignature> _rootSignature;
    private bool _disposed;

    public UIPass(
        RenderContext ctx,
        ShaderCompiler shaderCompiler,
        TextureRegistry textureRegistry)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (shaderCompiler is null) throw new ArgumentNullException(nameof(shaderCompiler));
        if (textureRegistry is null) throw new ArgumentNullException(nameof(textureRegistry));

        _ctx = ctx;
        _textureRegistry = textureRegistry;
        _constantBuffer = GpuBuffer<UiConstants>.CreateConstant(ctx, 1);

        string vsPath = ResolveShaderPath("ui.vs.hlsl");
        string psPath = ResolveShaderPath("ui.ps.hlsl");

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
                .SetDepthFormat(Format.FormatUnknown)
                .SetCullMode(CullMode.None)
                .SetBlendAlpha()
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
        IReadOnlyList<UIDrawCall> drawCalls,
        int screenWidth,
        int screenHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cmd is null) throw new ArgumentNullException(nameof(cmd));
        if (drawCalls is null) throw new ArgumentNullException(nameof(drawCalls));
        if (screenWidth <= 0) throw new ArgumentOutOfRangeException(nameof(screenWidth));
        if (screenHeight <= 0) throw new ArgumentOutOfRangeException(nameof(screenHeight));

        ReleaseFrameUploads();

        Matrix4x4 ortho = Matrix4x4.CreateOrthographicOffCenter(
            0f, screenWidth,
            screenHeight, 0f,
            -1f, 1f);

        UiConstants constants = new() { Projection = ortho };
        _constantBuffer.Update(MemoryMarshal.CreateReadOnlySpan(ref constants, 1));

        Viewport viewport = new(0f, 0f, screenWidth, screenHeight, 0f, 1f);
        Box2D<int> fullScissor = new(0, 0, screenWidth, screenHeight);

        cmd->RSSetViewports(1, ref viewport);
        cmd->RSSetScissorRects(1, ref fullScissor);
        cmd->SetPipelineState(_pipelineState.Handle);
        cmd->SetGraphicsRootSignature(_rootSignature.Handle);
        cmd->SetGraphicsRootConstantBufferView(0, _constantBuffer.GpuAddress);
        cmd->IASetPrimitiveTopology((D3DPrimitiveTopology)4u);

        for (int i = 0; i < drawCalls.Count; i++)
        {
            UIDrawCall drawCall = drawCalls[i];
            if (drawCall.Vertices is null || drawCall.Indices is null)
                continue;
            if (drawCall.Vertices.Length == 0 || drawCall.Indices.Length == 0)
                continue;

            if (!TryBuildScissor(drawCall.Scissors, screenWidth, screenHeight, out Box2D<int> scissor))
                continue;

            cmd->RSSetScissorRects(1, ref scissor);

            ComPtr<ID3D12Resource> vbResource = CreateUploadBuffer<Vertex>(drawCall.Vertices);
            ComPtr<ID3D12Resource> ibResource = CreateUploadBuffer<uint>(drawCall.Indices);
            _frameUploadResources.Add(vbResource);
            _frameUploadResources.Add(ibResource);

            VertexBufferView vbv = new()
            {
                BufferLocation = vbResource.Handle->GetGPUVirtualAddress(),
                SizeInBytes = (uint)(drawCall.Vertices.Length * sizeof(Vertex)),
                StrideInBytes = (uint)sizeof(Vertex),
            };

            IndexBufferView ibv = new()
            {
                BufferLocation = ibResource.Handle->GetGPUVirtualAddress(),
                SizeInBytes = (uint)(drawCall.Indices.Length * sizeof(uint)),
                Format = Format.FormatR32Uint,
            };

            cmd->IASetVertexBuffers(0, 1, ref vbv);
            cmd->IASetIndexBuffer(ref ibv);
            cmd->SetGraphicsRootDescriptorTable(2, _textureRegistry.GetSRV(drawCall.Texture));
            cmd->DrawIndexedInstanced((uint)drawCall.Indices.Length, 1, 0, 0, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseFrameUploads();
        _pipelineState.Dispose();
        _rootSignature.Dispose();
        _constantBuffer.Dispose();
    }

    private ComPtr<ID3D12Resource> CreateUploadBuffer<T>(ReadOnlySpan<T> data)
        where T : unmanaged
    {
        int byteSize = sizeof(T) * data.Length;

        HeapProperties heap = new() { Type = HeapType.Upload };
        ResourceDesc desc = new()
        {
            Dimension = ResourceDimension.Buffer,
            Alignment = 0,
            Width = (ulong)byteSize,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatUnknown,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Layout = TextureLayout.LayoutRowMajor,
            Flags = ResourceFlags.None,
        };

        void* resourcePtr = null;
        Guid resourceGuid = typeof(ID3D12Resource).GUID;
        SilkMarshal.ThrowHResult(
            _ctx.Device->CreateCommittedResource(
                ref heap,
                HeapFlags.None,
                ref desc,
                ResourceStates.GenericRead,
                (ClearValue*)null,
                ref resourceGuid,
                ref resourcePtr));

        ComPtr<ID3D12Resource> resource = new((ID3D12Resource*)resourcePtr);

        void* mapped = null;
        SilkMarshal.ThrowHResult(resource.Handle->Map(0, (Dx12Range*)null, &mapped));
        fixed (T* src = data)
            Unsafe.CopyBlockUnaligned(mapped, src, (uint)byteSize);
        resource.Handle->Unmap(0, (Dx12Range*)null);

        return resource;
    }

    private void ReleaseFrameUploads()
    {
        for (int i = 0; i < _frameUploadResources.Count; i++)
            _frameUploadResources[i].Dispose();

        _frameUploadResources.Clear();
    }

    private static bool TryBuildScissor(
        Rect rect,
        int screenWidth,
        int screenHeight,
        out Box2D<int> scissor)
    {
        int left = Math.Clamp((int)MathF.Floor(rect.X), 0, screenWidth);
        int top = Math.Clamp((int)MathF.Floor(rect.Y), 0, screenHeight);
        int right = Math.Clamp((int)MathF.Ceiling(rect.X + rect.Width), 0, screenWidth);
        int bottom = Math.Clamp((int)MathF.Ceiling(rect.Y + rect.Height), 0, screenHeight);

        if (right <= left || bottom <= top)
        {
            scissor = default;
            return false;
        }

        scissor = new Box2D<int>(left, top, right, bottom);
        return true;
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

// Назначение:   UI pass для DX12: ортографическая проекция, scissor per draw-call, временные upload VB/IB и отрисовка индексами поверх кадра.
// Зависит от:   RenderContext, PipelineBuilder, ShaderCompiler, TextureRegistry, IRenderer.UIDrawCall, GpuBuffer<T>, Silk.NET.Direct3D12
// Используется: Dx12Renderer (этап отрисовки пользовательского интерфейса после world-проходов)
