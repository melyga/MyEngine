#nullable enable

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using MyEngine.Core.Rendering.Dx12;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace MyEngine.Core.Rendering.Dx12.Passes;

internal sealed unsafe class LightingPass : IDisposable
{
    private const uint D3D12DefaultShader4ComponentMapping = 5768u;

    [StructLayout(LayoutKind.Sequential)]
    private struct LightingConstantBuffer
    {
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos;
        public float _p;
        public Matrix4x4 DirLightSpaceMatrix;
        public Matrix4x4 SpotLightSpaceMatrix;
        public Vector3 AmbientColor;
        public float _p2;
        public Vector3 FogColor;
        public float FogDensity;
    }

    private readonly RenderContext _ctx;
    private readonly GpuBuffer<LightingConstantBuffer> _constantBuffer;
    private readonly GpuDescriptorHandle _depthSrvGpu;
    private readonly Vector3 _ambientColor = new(0.05f, 0.05f, 0.05f);
    private readonly Vector3 _fogColor = new(0.7f, 0.75f, 0.8f);
    private readonly float _fogDensity = 0.002f;

    private ComPtr<ID3D12RootSignature> _rootSignature;
    private ComPtr<ID3D12PipelineState> _pipelineState;
    private bool _disposed;

    public LightingPass(
        RenderContext ctx,
        ShaderCompiler shaderCompiler,
        DescriptorHeap srvHeap,
        GBuffer gbuffer)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (shaderCompiler is null) throw new ArgumentNullException(nameof(shaderCompiler));
        if (srvHeap is null) throw new ArgumentNullException(nameof(srvHeap));
        if (gbuffer is null) throw new ArgumentNullException(nameof(gbuffer));

        _ctx = ctx;
        _constantBuffer = GpuBuffer<LightingConstantBuffer>.CreateConstant(ctx, 1);

        string vsPath = ResolveShaderPath("fullscreen.vs.hlsl");
        string psPath = ResolveShaderPath("lighting.ps.hlsl");
        IDxcBlob* vsBlob = shaderCompiler.Compile(vsPath, "VSMain", "vs_6_0");
        IDxcBlob* psBlob = shaderCompiler.Compile(psPath, "PSMain", "ps_6_0");

        CpuDescriptorHandle depthSrvCpu = srvHeap.Allocate();
        _depthSrvGpu = srvHeap.GetGpuHandle(srvHeap.Count - 1);

        ShaderResourceViewDesc depthSrvDesc = new()
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
        _ctx.Device->CreateShaderResourceView(gbuffer.DepthResource, &depthSrvDesc, depthSrvCpu);

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
        GBuffer gbuffer,
        ShadowPass shadows,
        GpuDescriptorHandle ssaoSRV,
        LightManager lights,
        Matrix4x4 invViewProj,
        Vector3 cameraPos)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cmd is null) throw new ArgumentNullException(nameof(cmd));
        if (gbuffer is null) throw new ArgumentNullException(nameof(gbuffer));
        if (shadows is null) throw new ArgumentNullException(nameof(shadows));
        if (lights is null) throw new ArgumentNullException(nameof(lights));

        LightingConstantBuffer constants = new()
        {
            InvViewProj = invViewProj,
            CameraPos = cameraPos,
            _p = 0f,
            DirLightSpaceMatrix = shadows.DirLightSpaceMatrix,
            SpotLightSpaceMatrix = shadows.SpotLightSpaceMatrix,
            AmbientColor = _ambientColor,
            _p2 = 0f,
            FogColor = _fogColor,
            FogDensity = _fogDensity,
        };
        _constantBuffer.Update(MemoryMarshal.CreateReadOnlySpan(ref constants, 1));

        cmd->SetGraphicsRootSignature(_rootSignature.Handle);
        cmd->SetPipelineState(_pipelineState.Handle);
        cmd->SetGraphicsRootConstantBufferView(0, _constantBuffer.GpuAddress);

        lights.Bind(cmd, rootParamIndex: 1);

        cmd->SetGraphicsRootDescriptorTable(2, gbuffer.SRVHandles[0]);
        cmd->SetGraphicsRootDescriptorTable(3, gbuffer.SRVHandles[1]);
        cmd->SetGraphicsRootDescriptorTable(4, gbuffer.SRVHandles[2]);
        cmd->SetGraphicsRootDescriptorTable(5, gbuffer.SRVHandles[3]);
        cmd->SetGraphicsRootDescriptorTable(6, _depthSrvGpu);
        cmd->SetGraphicsRootDescriptorTable(7, shadows.DirShadowSRV);
        cmd->SetGraphicsRootDescriptorTable(8, shadows.SpotShadowSRV);
        cmd->SetGraphicsRootDescriptorTable(9, ssaoSRV);

        cmd->IASetPrimitiveTopology((D3DPrimitiveTopology)4u);
        cmd->DrawInstanced(3, 1, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _pipelineState.Dispose();
        _rootSignature.Dispose();
        _constantBuffer.Dispose();
    }

    private static ID3D12RootSignature* CreateRootSignature(ID3D12Device* device)
    {
        DescriptorRange* ranges = stackalloc DescriptorRange[8];
        for (uint i = 0; i < 8; i++)
        {
            ranges[i] = new DescriptorRange
            {
                RangeType = DescriptorRangeType.Srv,
                NumDescriptors = 1,
                BaseShaderRegister = i,
                RegisterSpace = 0,
                OffsetInDescriptorsFromTableStart = 0,
            };
        }

        RootParameter* parameters = stackalloc RootParameter[10];

        parameters[0] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv,
            ShaderVisibility = ShaderVisibility.Pixel,
        };
        parameters[0].Anonymous.Descriptor.ShaderRegister = 0;
        parameters[0].Anonymous.Descriptor.RegisterSpace = 0;

        parameters[1] = new RootParameter
        {
            ParameterType = RootParameterType.TypeSrv,
            ShaderVisibility = ShaderVisibility.Pixel,
        };
        parameters[1].Anonymous.Descriptor.ShaderRegister = 8;
        parameters[1].Anonymous.Descriptor.RegisterSpace = 0;

        for (int i = 0; i < 8; i++)
        {
            parameters[2 + i] = new RootParameter
            {
                ParameterType = RootParameterType.TypeDescriptorTable,
                ShaderVisibility = ShaderVisibility.Pixel,
                Anonymous = new RootParameterUnion
                {
                    DescriptorTable = new RootDescriptorTable
                    {
                        NumDescriptorRanges = 1,
                        PDescriptorRanges = &ranges[i],
                    },
                },
            };
        }

        StaticSamplerDesc* samplers = stackalloc StaticSamplerDesc[3];
        samplers[0] = MakeLinearClampSampler(0);
        samplers[1] = MakeComparisonShadowSampler(1);
        samplers[2] = MakeLinearClampSampler(2);

        RootSignatureDesc desc = new()
        {
            NumParameters = 10,
            PParameters = parameters,
            NumStaticSamplers = 3,
            PStaticSamplers = samplers,
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
            BlendState = BuildBlendDesc(),
            SampleMask = uint.MaxValue,
            RasterizerState = BuildRasterizerDesc(),
            DepthStencilState = BuildDepthDisabledDesc(),
            InputLayout = default,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            NumRenderTargets = 1,
            DSVFormat = Format.FormatUnknown,
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

    private static BlendDesc BuildBlendDesc()
    {
        BlendDesc desc = new() { AlphaToCoverageEnable = 0, IndependentBlendEnable = 0 };
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
    };

    private static DepthStencilDesc BuildDepthDisabledDesc()
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
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunc.Always,
            StencilEnable = 0,
            StencilReadMask = 0xFF,
            StencilWriteMask = 0xFF,
            FrontFace = passThrough,
            BackFace = passThrough,
        };
    }

    private static StaticSamplerDesc MakeLinearClampSampler(uint shaderRegister) => new()
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
        ShaderRegister = shaderRegister,
        RegisterSpace = 0,
        ShaderVisibility = ShaderVisibility.Pixel,
    };

    private static StaticSamplerDesc MakeComparisonShadowSampler(uint shaderRegister) => new()
    {
        Filter = Filter.ComparisonMinMagLinearMipPoint,
        AddressU = TextureAddressMode.Border,
        AddressV = TextureAddressMode.Border,
        AddressW = TextureAddressMode.Border,
        MipLODBias = 0f,
        MaxAnisotropy = 1,
        ComparisonFunc = ComparisonFunc.LessEqual,
        BorderColor = StaticBorderColor.OpaqueWhite,
        MinLOD = 0f,
        MaxLOD = 0f,
        ShaderRegister = shaderRegister,
        RegisterSpace = 0,
        ShaderVisibility = ShaderVisibility.Pixel,
    };

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

        return SilkMarshal.PtrToString((nint)ptr, NativeStringEncoding.UTF8) ?? "(invalid UTF-8 blob)";
    }
}

// Назначение:   DX12 lighting pass: собирает deferred-освещение fullscreen triangle из G-Buffer, shadow maps, SSAO и буфера источников света.
// Зависит от:   RenderContext, GBuffer, ShadowPass, LightManager, DescriptorHeap, ShaderCompiler, GpuBuffer<T>, Silk.NET.Direct3D12/Silk.NET.DXGI
// Используется: Dx12Renderer (этап освещения после geometry/shadow/ssao проходов)
