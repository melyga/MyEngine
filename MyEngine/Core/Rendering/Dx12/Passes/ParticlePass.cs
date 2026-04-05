#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using MyEngine.Core.Abstractions;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Dx12Range = Silk.NET.Direct3D12.Range;

namespace MyEngine.Core.Rendering.Dx12.Passes;

internal sealed unsafe class ParticlePass : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ParticlePassConstants
    {
        public Matrix4x4 ViewProjection;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleInstanceGpu
    {
        public Vector3 Position;
        public float Size;
        public Vector4 Color;
    }

    private readonly RenderContext _ctx;
    private readonly TextureRegistry _textureRegistry;
    private readonly GpuBuffer<ParticlePassConstants> _constantBuffer;
    private readonly CpuDescriptorHandle _instanceSrvCpu;
    private readonly GpuDescriptorHandle _instanceSrvGpu;
    private readonly List<ComPtr<ID3D12Resource>> _frameUploadResources = new();

    private ComPtr<ID3D12RootSignature> _rootSignature;
    private ComPtr<ID3D12PipelineState> _pipelineState;
    private bool _disposed;

    public ParticlePass(
        RenderContext ctx,
        ShaderCompiler shaderCompiler,
        TextureRegistry textureRegistry,
        DescriptorHeap srvHeap)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (shaderCompiler is null) throw new ArgumentNullException(nameof(shaderCompiler));
        if (textureRegistry is null) throw new ArgumentNullException(nameof(textureRegistry));
        if (srvHeap is null) throw new ArgumentNullException(nameof(srvHeap));

        _ctx = ctx;
        _textureRegistry = textureRegistry;
        _constantBuffer = GpuBuffer<ParticlePassConstants>.CreateConstant(ctx, 1);

        int instanceSrvIndex = srvHeap.Count;
        _instanceSrvCpu = srvHeap.Allocate();
        _instanceSrvGpu = srvHeap.GetGpuHandle(instanceSrvIndex);

        string vsPath = ResolveShaderPath("particles.vs.hlsl");
        string psPath = ResolveShaderPath("particles.ps.hlsl");

        IDxcBlob* vsBlob = shaderCompiler.Compile(vsPath, "VSMain", "vs_6_0");
        IDxcBlob* psBlob = shaderCompiler.Compile(psPath, "PSMain", "ps_6_0");

        ID3D12RootSignature* root = null;
        ID3D12PipelineState* pso = null;
        try
        {
            root = CreateRootSignature(ctx.Device);
            pso = CreatePipelineState(ctx.Device, root, vsBlob, psBlob);
            _rootSignature = new ComPtr<ID3D12RootSignature>(root);
            _pipelineState = new ComPtr<ID3D12PipelineState>(pso);
        }
        catch
        {
            if (pso != null) pso->Release();
            if (root != null) root->Release();
            throw;
        }
    }

    public void Render(
        ID3D12GraphicsCommandList* cmd,
        IReadOnlyList<ParticleDrawCall> emitters,
        CameraData camera,
        CpuDescriptorHandle backBufferRtv,
        CpuDescriptorHandle depthDsv,
        int width,
        int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cmd is null) throw new ArgumentNullException(nameof(cmd));
        if (emitters is null) throw new ArgumentNullException(nameof(emitters));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        ReleaseFrameUploads();

        Matrix4x4 viewProjection = camera.View * camera.Projection;
        ParticlePassConstants constants = new() { ViewProjection = viewProjection };
        _constantBuffer.Update(MemoryMarshal.CreateReadOnlySpan(ref constants, 1));

        Viewport viewport = new(0f, 0f, width, height, 0f, 1f);
        Box2D<int> scissor = new(0, 0, width, height);

        cmd->RSSetViewports(1, ref viewport);
        cmd->RSSetScissorRects(1, ref scissor);
        cmd->OMSetRenderTargets(1, &backBufferRtv, false, &depthDsv);

        cmd->SetGraphicsRootSignature(_rootSignature.Handle);
        cmd->SetPipelineState(_pipelineState.Handle);
        cmd->SetGraphicsRootConstantBufferView(0, _constantBuffer.GpuAddress);
        cmd->IASetPrimitiveTopology((D3DPrimitiveTopology)4u);

        for (int i = 0; i < emitters.Count; i++)
        {
            ParticleDrawCall emitter = emitters[i];
            if (emitter.InstanceCount <= 0 || emitter.Instances is null || emitter.Instances.Length == 0)
                continue;

            int instanceCount = Math.Min(emitter.InstanceCount, emitter.Instances.Length);
            ComPtr<ID3D12Resource> instanceBuffer = CreateInstanceUploadBuffer(emitter.Instances, instanceCount);
            _frameUploadResources.Add(instanceBuffer);

            ShaderResourceViewDesc instanceSrv = new()
            {
                Format = Format.FormatUnknown,
                ViewDimension = SrvDimension.Buffer,
                Shader4ComponentMapping = 5768u,
                Anonymous = new ShaderResourceViewDescUnion
                {
                    Buffer = new BufferSrv
                    {
                        FirstElement = 0,
                        NumElements = (uint)instanceCount,
                        StructureByteStride = (uint)sizeof(ParticleInstanceGpu),
                        Flags = BufferSrvFlags.None,
                    },
                },
            };

            _ctx.Device->CreateShaderResourceView(instanceBuffer.Handle, &instanceSrv, _instanceSrvCpu);

            cmd->SetGraphicsRootDescriptorTable(1, _instanceSrvGpu);
            cmd->SetGraphicsRootDescriptorTable(2, _textureRegistry.GetSRV(emitter.Texture));
            cmd->DrawInstanced(6, (uint)instanceCount, 0, 0);
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

    private ComPtr<ID3D12Resource> CreateInstanceUploadBuffer(
        ParticleInstance[] source,
        int count)
    {
        int byteSize = sizeof(ParticleInstanceGpu) * count;

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

        void* ptr = null;
        Guid guid = typeof(ID3D12Resource).GUID;
        SilkMarshal.ThrowHResult(
            _ctx.Device->CreateCommittedResource(
                ref heap,
                HeapFlags.None,
                ref desc,
                ResourceStates.GenericRead,
                (ClearValue*)null,
                ref guid,
                ref ptr));

        ComPtr<ID3D12Resource> resource = new((ID3D12Resource*)ptr);

        void* mapped = null;
        SilkMarshal.ThrowHResult(resource.Handle->Map(0, (Dx12Range*)null, &mapped));
        Span<ParticleInstanceGpu> dst = new(mapped, count);

        for (int i = 0; i < count; i++)
        {
            ParticleInstance src = source[i];
            dst[i] = new ParticleInstanceGpu
            {
                Position = src.Position,
                Size = src.Size,
                Color = src.Color,
            };
        }

        resource.Handle->Unmap(0, (Dx12Range*)null);
        return resource;
    }

    private void ReleaseFrameUploads()
    {
        for (int i = 0; i < _frameUploadResources.Count; i++)
            _frameUploadResources[i].Dispose();

        _frameUploadResources.Clear();
    }

    private static ID3D12RootSignature* CreateRootSignature(ID3D12Device* device)
    {
        DescriptorRange* ranges = stackalloc DescriptorRange[2];
        ranges[0] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 0,
            RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 0,
        };
        ranges[1] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 1,
            RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 0,
        };

        RootParameter* parameters = stackalloc RootParameter[3];
        parameters[0] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv,
            ShaderVisibility = ShaderVisibility.Vertex,
        };
        parameters[0].Anonymous.Descriptor.ShaderRegister = 0;
        parameters[0].Anonymous.Descriptor.RegisterSpace = 0;

        parameters[1] = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            ShaderVisibility = ShaderVisibility.Vertex,
            Anonymous = new RootParameterUnion
            {
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = &ranges[0],
                },
            },
        };
        parameters[2] = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            ShaderVisibility = ShaderVisibility.Pixel,
            Anonymous = new RootParameterUnion
            {
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = &ranges[1],
                },
            },
        };

        StaticSamplerDesc sampler = new()
        {
            Filter = Filter.MinMagLinearMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0f,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunc.Never,
            BorderColor = StaticBorderColor.TransparentBlack,
            MinLOD = 0f,
            MaxLOD = float.MaxValue,
            ShaderRegister = 0,
            RegisterSpace = 0,
            ShaderVisibility = ShaderVisibility.Pixel,
        };

        RootSignatureDesc desc = new()
        {
            NumParameters = 3,
            PParameters = parameters,
            NumStaticSamplers = 1,
            PStaticSamplers = &sampler,
            Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
        };

        return SerializeAndCreateRootSignature(device, desc);
    }

    private static ID3D12PipelineState* CreatePipelineState(
        ID3D12Device* device,
        ID3D12RootSignature* root,
        IDxcBlob* vsBlob,
        IDxcBlob* psBlob)
    {
        GraphicsPipelineStateDesc desc = new()
        {
            PRootSignature = root,
            VS = new ShaderBytecode
            {
                PShaderBytecode = vsBlob->GetBufferPointer(),
                BytecodeLength = vsBlob->GetBufferSize(),
            },
            PS = new ShaderBytecode
            {
                PShaderBytecode = psBlob->GetBufferPointer(),
                BytecodeLength = psBlob->GetBufferSize(),
            },
            BlendState = BuildAlphaBlendDesc(),
            SampleMask = uint.MaxValue,
            RasterizerState = new RasterizerDesc
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.None,
                FrontCounterClockwise = 0,
                DepthBias = 0,
                DepthBiasClamp = 0f,
                SlopeScaledDepthBias = 0f,
                DepthClipEnable = 1,
                MultisampleEnable = 0,
                AntialiasedLineEnable = 0,
                ForcedSampleCount = 0,
                ConservativeRaster = ConservativeRasterizationMode.Off,
            },
            DepthStencilState = BuildDepthReadOnlyDesc(),
            InputLayout = default,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            NumRenderTargets = 1,
            DSVFormat = Format.FormatD32Float,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            NodeMask = 0,
            CachedPSO = default,
            Flags = PipelineStateFlags.None,
        };
        desc.RTVFormats[0] = Format.FormatR8G8B8A8Unorm;

        void* ptr = null;
        Guid guid = typeof(ID3D12PipelineState).GUID;
        SilkMarshal.ThrowHResult(device->CreateGraphicsPipelineState(ref desc, ref guid, ref ptr));
        return (ID3D12PipelineState*)ptr;
    }

    private static BlendDesc BuildAlphaBlendDesc()
    {
        BlendDesc desc = new() { AlphaToCoverageEnable = 0, IndependentBlendEnable = 0 };
        RenderTargetBlendDesc rt = new()
        {
            BlendEnable = 1,
            LogicOpEnable = 0,
            SrcBlend = Blend.SrcAlpha,
            DestBlend = Blend.InvSrcAlpha,
            BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One,
            DestBlendAlpha = Blend.InvSrcAlpha,
            BlendOpAlpha = BlendOp.Add,
            LogicOp = LogicOp.Noop,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        for (int i = 0; i < 8; i++)
            desc.RenderTarget[i] = rt;
        return desc;
    }

    private static DepthStencilDesc BuildDepthReadOnlyDesc()
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
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunc.LessEqual,
            StencilEnable = 0,
            StencilReadMask = 0xFF,
            StencilWriteMask = 0xFF,
            FrontFace = passThrough,
            BackFace = passThrough,
        };
    }

    private static ID3D12RootSignature* SerializeAndCreateRootSignature(
        ID3D12Device* device,
        RootSignatureDesc desc)
    {
        void* sigBlob = null;
        void* errBlob = null;

        int hr = D3D12SerializeRootSignature(&desc, D3DRootSignatureVersion.Version10, &sigBlob, &errBlob);
        if (hr < 0)
        {
            string error = ReadBlobText(errBlob);
            ComRelease(errBlob);
            throw new InvalidOperationException($"D3D12SerializeRootSignature failed (hr=0x{hr:X8}): {error}");
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
        if (obj == null)
            return;
        ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)obj)[2])(obj);
    }

    private static string ReadBlobText(void* blob)
    {
        if (blob == null)
            return "(null blob)";

        GetBlobBuffer(blob, out void* ptr, out nuint size);
        if (ptr == null || size == 0)
            return "(empty error blob)";

        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>((byte*)ptr, (int)size)).TrimEnd('\0');
    }
}

// Назначение:   Рендер частиц через GPU instancing: один DrawInstanced на эмиттер, данные инстансов читаются из StructuredBuffer по SV_InstanceID.
// Зависит от:   RenderContext, ShaderCompiler, TextureRegistry, DescriptorHeap, IRenderer.ParticleDrawCall/CameraData, Silk.NET.Direct3D12
// Используется: Dx12Renderer как particle pass после прозрачной геометрии
