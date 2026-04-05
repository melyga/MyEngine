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

namespace MyEngine.Core.Rendering.Dx12.Passes;

internal sealed unsafe class ShadowPass : IDisposable
{
    private const int DirShadowSize = 2048;
    private const int SpotShadowSize = 1024;
    private const uint AllSubresources = 0xFFFF_FFFFu;
    private const uint D3D12DefaultShader4ComponentMapping = 5768u;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShadowConstants
    {
        public Matrix4x4 World;
        public Matrix4x4 LightViewProj;
    }

    private readonly RenderContext _ctx;
    private readonly MeshRegistry _meshRegistry;
    private readonly GpuBuffer<ShadowConstants> _shadowConstants;

    private ComPtr<ID3D12PipelineState> _pipelineState;
    private ComPtr<ID3D12RootSignature> _rootSignature;

    private ComPtr<ID3D12Resource> _dirShadowMap;
    private ComPtr<ID3D12Resource> _spotShadowMap;

    private readonly CpuDescriptorHandle _dirShadowDsv;
    private readonly CpuDescriptorHandle _spotShadowDsv;
    private readonly CpuDescriptorHandle _dirShadowSrvCpu;
    private readonly CpuDescriptorHandle _spotShadowSrvCpu;

    private bool _disposed;

    public GpuDescriptorHandle DirShadowSRV { get; }
    public GpuDescriptorHandle SpotShadowSRV { get; }
    public Matrix4x4 DirLightSpaceMatrix { get; private set; } = Matrix4x4.Identity;
    public Matrix4x4 SpotLightSpaceMatrix { get; private set; } = Matrix4x4.Identity;

    public ShadowPass(
        RenderContext ctx,
        ShaderCompiler shaderCompiler,
        MeshRegistry meshRegistry,
        DescriptorHeap dsvHeap,
        DescriptorHeap srvHeap)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (shaderCompiler is null) throw new ArgumentNullException(nameof(shaderCompiler));
        if (meshRegistry is null) throw new ArgumentNullException(nameof(meshRegistry));
        if (dsvHeap is null) throw new ArgumentNullException(nameof(dsvHeap));
        if (srvHeap is null) throw new ArgumentNullException(nameof(srvHeap));

        _ctx = ctx;
        _meshRegistry = meshRegistry;
        _shadowConstants = GpuBuffer<ShadowConstants>.CreateConstant(ctx, 1);

        _dirShadowDsv = dsvHeap.Allocate();
        _spotShadowDsv = dsvHeap.Allocate();

        _dirShadowSrvCpu = srvHeap.Allocate();
        DirShadowSRV = srvHeap.GetGpuHandle(srvHeap.Count - 1);

        _spotShadowSrvCpu = srvHeap.Allocate();
        SpotShadowSRV = srvHeap.GetGpuHandle(srvHeap.Count - 1);

        _dirShadowMap = CreateShadowMapResource(DirShadowSize);
        _spotShadowMap = CreateShadowMapResource(SpotShadowSize);

        CreateShadowViews(_dirShadowMap.Handle, _dirShadowDsv, _dirShadowSrvCpu);
        CreateShadowViews(_spotShadowMap.Handle, _spotShadowDsv, _spotShadowSrvCpu);

        string shadowVsPath = ResolveShaderPath("shadow.vs.hlsl");
        IDxcBlob* vsBlob = shaderCompiler.Compile(shadowVsPath, "VSMain", "vs_6_0");

        nint pos = SilkMarshal.StringToPtr("POSITION", NativeStringEncoding.UTF8);
        try
        {
            InputElementDesc[] inputLayout =
            [
                new InputElementDesc((byte*)pos, 0, Format.FormatR32G32B32Float, 0, 0, InputClassification.PerVertexData, 0),
            ];

            CreatePipeline(
                ctx.Device,
                vsBlob,
                inputLayout,
                out ID3D12RootSignature* root,
                out ID3D12PipelineState* pso);

            _rootSignature = new ComPtr<ID3D12RootSignature>(root);
            _pipelineState = new ComPtr<ID3D12PipelineState>(pso);
        }
        finally
        {
            SilkMarshal.Free(pos);
        }
    }

    public void RenderDirectional(
        ID3D12GraphicsCommandList* cmd,
        IReadOnlyList<DrawCall> drawCalls,
        Matrix4x4 lightViewProj)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cmd is null) throw new ArgumentNullException(nameof(cmd));
        if (drawCalls is null) throw new ArgumentNullException(nameof(drawCalls));

        DirLightSpaceMatrix = lightViewProj;
        RenderShadowMap(cmd, drawCalls, _dirShadowMap.Handle, _dirShadowDsv, DirShadowSize, lightViewProj);
    }

    public void RenderSpot(
        ID3D12GraphicsCommandList* cmd,
        IReadOnlyList<DrawCall> drawCalls,
        Matrix4x4 spotViewProj)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cmd is null) throw new ArgumentNullException(nameof(cmd));
        if (drawCalls is null) throw new ArgumentNullException(nameof(drawCalls));

        SpotLightSpaceMatrix = spotViewProj;
        RenderShadowMap(cmd, drawCalls, _spotShadowMap.Handle, _spotShadowDsv, SpotShadowSize, spotViewProj);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _spotShadowMap.Dispose();
        _dirShadowMap.Dispose();
        _pipelineState.Dispose();
        _rootSignature.Dispose();
        _shadowConstants.Dispose();
    }

    private void RenderShadowMap(
        ID3D12GraphicsCommandList* cmd,
        IReadOnlyList<DrawCall> drawCalls,
        ID3D12Resource* shadowResource,
        CpuDescriptorHandle shadowDsv,
        int shadowSize,
        Matrix4x4 lightViewProj)
    {
        ResourceBarrier toDepthWrite = MakeTransitionBarrier(
            shadowResource,
            ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource,
            ResourceStates.DepthWrite);
        cmd->ResourceBarrier(1, ref toDepthWrite);

        Viewport viewport = new(0f, 0f, shadowSize, shadowSize, 0f, 1f);
        Silk.NET.Maths.Box2D<int> scissor = new(0, 0, shadowSize, shadowSize);
        cmd->RSSetViewports(1, ref viewport);
        cmd->RSSetScissorRects(1, ref scissor);

        cmd->OMSetRenderTargets(0, (CpuDescriptorHandle*)null, false, &shadowDsv);
        cmd->ClearDepthStencilView(shadowDsv, ClearFlags.Depth, 1f, 0, 0, null);

        cmd->SetGraphicsRootSignature(_rootSignature.Handle);
        cmd->SetPipelineState(_pipelineState.Handle);
        cmd->IASetPrimitiveTopology((D3DPrimitiveTopology)4u); // D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST

        for (int i = 0; i < drawCalls.Count; i++)
        {
            DrawCall drawCall = drawCalls[i];
            (VertexBufferView vbv, IndexBufferView ibv, int indexCount) = _meshRegistry.GetBuffers(drawCall.Mesh);

            ShadowConstants constants = new()
            {
                World = drawCall.WorldMatrix,
                LightViewProj = lightViewProj,
            };

            ReadOnlySpan<ShadowConstants> span = MemoryMarshal.CreateReadOnlySpan(ref constants, 1);
            _shadowConstants.Update(span);
            cmd->SetGraphicsRootConstantBufferView(0, _shadowConstants.GpuAddress);

            cmd->IASetVertexBuffers(0, 1, ref vbv);
            cmd->IASetIndexBuffer(ref ibv);
            cmd->DrawIndexedInstanced((uint)indexCount, 1, 0, 0, 0);
        }

        ResourceBarrier toShaderRead = MakeTransitionBarrier(
            shadowResource,
            ResourceStates.DepthWrite,
            ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
        cmd->ResourceBarrier(1, ref toShaderRead);
    }

    private ComPtr<ID3D12Resource> CreateShadowMapResource(int size)
    {
        HeapProperties heapProps = new() { Type = HeapType.Default };

        ResourceDesc desc = new()
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (ulong)size,
            Height = (uint)size,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatR32Typeless,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowDepthStencil,
        };

        ClearValue clear = new()
        {
            Format = Format.FormatD32Float,
            Anonymous = new ClearValueUnion { DepthStencil = new DepthStencilValue { Depth = 1f, Stencil = 0 } },
        };

        void* ptr = null;
        Guid guid = typeof(ID3D12Resource).GUID;
        SilkMarshal.ThrowHResult(
            _ctx.Device->CreateCommittedResource(
                ref heapProps,
                HeapFlags.None,
                ref desc,
                ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource,
                &clear,
                ref guid,
                ref ptr));

        return new ComPtr<ID3D12Resource>((ID3D12Resource*)ptr);
    }

    private void CreateShadowViews(
        ID3D12Resource* resource,
        CpuDescriptorHandle dsvHandle,
        CpuDescriptorHandle srvHandle)
    {
        DepthStencilViewDesc dsvDesc = new()
        {
            Format = Format.FormatD32Float,
            ViewDimension = DsvDimension.Texture2D,
            Flags = DsvFlags.None,
            Anonymous = new DepthStencilViewDescUnion
            {
                Texture2D = new Tex2DDsv { MipSlice = 0 },
            },
        };

        ShaderResourceViewDesc srvDesc = new()
        {
            Format = Format.FormatR32Float,
            ViewDimension = SrvDimension.Texture2D,
            Shader4ComponentMapping = D3D12DefaultShader4ComponentMapping,
            Anonymous = new ShaderResourceViewDescUnion
            {
                Texture2D = new Tex2DSrv
                {
                    MostDetailedMip = 0,
                    MipLevels = 1,
                    PlaneSlice = 0,
                    ResourceMinLODClamp = 0f,
                },
            },
        };

        _ctx.Device->CreateDepthStencilView(resource, &dsvDesc, dsvHandle);
        _ctx.Device->CreateShaderResourceView(resource, &srvDesc, srvHandle);
    }

    private static void CreatePipeline(
        ID3D12Device* device,
        IDxcBlob* vsBlob,
        InputElementDesc[] inputLayout,
        out ID3D12RootSignature* root,
        out ID3D12PipelineState* pso)
    {
        root = CreateRootSignature(device);
        pso = null;

        GCHandle pin = GCHandle.Alloc(inputLayout, GCHandleType.Pinned);
        try
        {
            GraphicsPipelineStateDesc psoDesc = new()
            {
                PRootSignature = root,
                VS = new ShaderBytecode
                {
                    PShaderBytecode = vsBlob->GetBufferPointer(),
                    BytecodeLength = vsBlob->GetBufferSize(),
                },
                PS = default,
                BlendState = BuildBlendDesc(),
                SampleMask = uint.MaxValue,
                RasterizerState = BuildRasterizerDesc(),
                DepthStencilState = BuildDepthStencilDesc(),
                InputLayout = new InputLayoutDesc
                {
                    PInputElementDescs = (InputElementDesc*)pin.AddrOfPinnedObject(),
                    NumElements = (uint)inputLayout.Length,
                },
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                NumRenderTargets = 0,
                DSVFormat = Format.FormatD32Float,
                SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                NodeMask = 0,
                CachedPSO = default,
                Flags = PipelineStateFlags.None,
            };

            void* ptr = null;
            Guid guid = typeof(ID3D12PipelineState).GUID;
            SilkMarshal.ThrowHResult(device->CreateGraphicsPipelineState(ref psoDesc, ref guid, ref ptr));
            pso = (ID3D12PipelineState*)ptr;
        }
        catch
        {
            root->Release();
            throw;
        }
        finally
        {
            pin.Free();
        }

    }

    private static ID3D12RootSignature* CreateRootSignature(ID3D12Device* device)
    {
        RootParameter param = new()
        {
            ParameterType = RootParameterType.TypeCbv,
            ShaderVisibility = ShaderVisibility.Vertex,
        };
        param.Anonymous.Descriptor.ShaderRegister = 0;
        param.Anonymous.Descriptor.RegisterSpace = 0;

        RootSignatureDesc rsDesc = new()
        {
            NumParameters = 1,
            PParameters = &param,
            NumStaticSamplers = 0,
            PStaticSamplers = null,
            Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
        };

        void* sigBlob = null;
        void* errBlob = null;
        int hr = D3D12SerializeRootSignature(&rsDesc, D3DRootSignatureVersion.Version10, &sigBlob, &errBlob);
        if (hr < 0)
        {
            string msg = ReadBlobText(errBlob);
            ComRelease(errBlob);
            throw new InvalidOperationException($"D3D12SerializeRootSignature failed (hr=0x{hr:X8}): {msg}");
        }
        ComRelease(errBlob);

        GetBlobBuffer(sigBlob, out void* sigPtr, out nuint sigSize);

        void* rootPtr = null;
        Guid rootGuid = typeof(ID3D12RootSignature).GUID;
        int createHr = device->CreateRootSignature(0, sigPtr, sigSize, ref rootGuid, ref rootPtr);
        ComRelease(sigBlob);
        SilkMarshal.ThrowHResult(createHr);

        return (ID3D12RootSignature*)rootPtr;
    }

    private static BlendDesc BuildBlendDesc()
    {
        BlendDesc desc = new()
        {
            AlphaToCoverageEnable = 0,
            IndependentBlendEnable = 0,
        };

        RenderTargetBlendDesc rt = new()
        {
            BlendEnable = 0,
            LogicOpEnable = 0,
            SrcBlend = Blend.One,
            DestBlend = Blend.Zero,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.Zero,
            BlendOpAlpha = BlendOp.Add,
            LogicOp = LogicOp.Noop,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };

        for (int i = 0; i < 8; i++)
            desc.RenderTarget[i] = rt;

        return desc;
    }

    private static RasterizerDesc BuildRasterizerDesc() => new()
    {
        FillMode = FillMode.Solid,
        CullMode = CullMode.Back,
        FrontCounterClockwise = 0,
        DepthBias = 0,
        DepthBiasClamp = 0f,
        SlopeScaledDepthBias = 0f,
        DepthClipEnable = 1,
        MultisampleEnable = 0,
        AntialiasedLineEnable = 0,
        ForcedSampleCount = 0,
        ConservativeRaster = ConservativeRasterizationMode.Off,
    };

    private static DepthStencilDesc BuildDepthStencilDesc()
    {
        DepthStencilopDesc passThrough = new()
        {
            StencilFailOp = StencilOp.Keep,
            StencilDepthFailOp = StencilOp.Keep,
            StencilPassOp = StencilOp.Keep,
            StencilFunc = ComparisonFunc.Always,
        };

        return new DepthStencilDesc
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunc.LessEqual,
            StencilEnable = 0,
            StencilReadMask = 0xFF,
            StencilWriteMask = 0xFF,
            FrontFace = passThrough,
            BackFace = passThrough,
        };
    }

    private static ResourceBarrier MakeTransitionBarrier(
        ID3D12Resource* resource,
        ResourceStates before,
        ResourceStates after)
    {
        ResourceBarrier barrier = default;
        barrier.Type = ResourceBarrierType.Transition;
        barrier.Flags = ResourceBarrierFlags.None;
        barrier.Anonymous.Transition = new ResourceTransitionBarrier
        {
            PResource = resource,
            Subresource = AllSubresources,
            StateBefore = before,
            StateAfter = after,
        };
        return barrier;
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

    [DllImport("d3d12", EntryPoint = "D3D12SerializeRootSignature")]
    private static extern int D3D12SerializeRootSignature(
        RootSignatureDesc* pDesc,
        D3DRootSignatureVersion version,
        void** ppBlob,
        void** ppErrorBlob);

    private static void GetBlobBuffer(void* blob, out void* ptr, out nuint size)
    {
        void** vtbl = *(void***)blob;
        ptr = ((delegate* unmanaged[Stdcall]<void*, void*>)vtbl[3])(blob);
        size = ((delegate* unmanaged[Stdcall]<void*, nuint>)vtbl[4])(blob);
    }

    private static void ComRelease(void* obj)
    {
        if (obj == null) return;
        ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)obj)[2])(obj);
    }

    private static string ReadBlobText(void* blob)
    {
        if (blob == null) return "(null blob)";
        GetBlobBuffer(blob, out void* ptr, out nuint size);
        if (ptr == null || size == 0) return "(empty error blob)";
        return SilkMarshal.PtrToString((nint)ptr, NativeStringEncoding.UTF8) ?? "(invalid UTF-8 blob)";
    }
}

// Назначение:   DX12 shadow pass: создает directional/spot depth shadow maps, depth-only VS pipeline и рендерит DrawCall в карты теней.
// Зависит от:   RenderContext, DescriptorHeap, ShaderCompiler, MeshRegistry, IRenderer.DrawCall, GpuBuffer<T>, Silk.NET.Direct3D12/Silk.NET.DXGI
// Используется: Dx12Renderer (этап shadow map перед lighting pass)
