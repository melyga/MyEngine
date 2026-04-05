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
using Silk.NET.Maths;

namespace MyEngine.Core.Rendering.Dx12.Passes;

internal sealed unsafe class SsaoPass : IDisposable
{
    private const uint AllSubresources = 0xFFFF_FFFFu;
    private const uint D3D12DefaultShader4ComponentMapping = 5768u;

    [StructLayout(LayoutKind.Sequential)]
    private struct SsaoConstants
    {
        public Matrix4x4 Projection;
        public Matrix4x4 InverseProjection;
        public Vector4 NoiseScaleRadiusBias;
        public Vector4 Kernel0;
        public Vector4 Kernel1;
        public Vector4 Kernel2;
        public Vector4 Kernel3;
        public Vector4 Kernel4;
        public Vector4 Kernel5;
        public Vector4 Kernel6;
        public Vector4 Kernel7;
        public Vector4 Kernel8;
        public Vector4 Kernel9;
        public Vector4 Kernel10;
        public Vector4 Kernel11;
        public Vector4 Kernel12;
        public Vector4 Kernel13;
        public Vector4 Kernel14;
        public Vector4 Kernel15;
    }

    private readonly RenderContext _ctx;
    private readonly GpuBuffer<SsaoConstants> _constantBuffer;
    private readonly GpuTexture2D _noiseTexture;

    private readonly CpuDescriptorHandle _aoRtv;
    private readonly CpuDescriptorHandle _blurRtv;
    private readonly CpuDescriptorHandle _aoSrvCpu;
    private readonly CpuDescriptorHandle _blurSrvCpu;
    private readonly GpuDescriptorHandle _aoSrvGpu;
    private readonly GpuDescriptorHandle _blurSrvGpu;

    private readonly Vector4[] _kernel = new Vector4[16];

    private ComPtr<ID3D12Resource> _aoResource;
    private ComPtr<ID3D12Resource> _blurredAoResource;

    private ComPtr<ID3D12RootSignature> _ssaoRootSignature;
    private ComPtr<ID3D12RootSignature> _blurRootSignature;
    private ComPtr<ID3D12PipelineState> _ssaoPipeline;
    private ComPtr<ID3D12PipelineState> _blurPipeline;

    private readonly int _width;
    private readonly int _height;

    private bool _disposed;

    public GpuDescriptorHandle BlurredAoSRV => _blurSrvGpu;

    public SsaoPass(
        RenderContext ctx,
        ShaderCompiler shaderCompiler,
        DescriptorHeap rtvHeap,
        DescriptorHeap srvHeap,
        int width,
        int height)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (shaderCompiler is null) throw new ArgumentNullException(nameof(shaderCompiler));
        if (rtvHeap is null) throw new ArgumentNullException(nameof(rtvHeap));
        if (srvHeap is null) throw new ArgumentNullException(nameof(srvHeap));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        _ctx = ctx;
        _width = width;
        _height = height;

        GenerateKernel();
        _noiseTexture = CreateNoiseTexture(srvHeap);

        _aoRtv = rtvHeap.Allocate();
        _blurRtv = rtvHeap.Allocate();

        int aoSrvIndex = srvHeap.Count;
        _aoSrvCpu = srvHeap.Allocate();
        _aoSrvGpu = srvHeap.GetGpuHandle(aoSrvIndex);

        int blurSrvIndex = srvHeap.Count;
        _blurSrvCpu = srvHeap.Allocate();
        _blurSrvGpu = srvHeap.GetGpuHandle(blurSrvIndex);

        _aoResource = CreateAoRenderTarget(width, height);
        _blurredAoResource = CreateAoRenderTarget(width, height);

        CreateAoViews(_aoResource.Handle, _aoRtv, _aoSrvCpu);
        CreateAoViews(_blurredAoResource.Handle, _blurRtv, _blurSrvCpu);

        _constantBuffer = GpuBuffer<SsaoConstants>.CreateConstant(ctx, 1);

        string ssaoPath = ResolveShaderPath("ssao.ps.hlsl");
        string blurPath = ResolveShaderPath("ssao_blur.ps.hlsl");

        IDxcBlob* ssaoVs = shaderCompiler.Compile(ssaoPath, "VSMain", "vs_6_0");
        IDxcBlob* ssaoPs = shaderCompiler.Compile(ssaoPath, "PSMain", "ps_6_0");
        IDxcBlob* blurPs = shaderCompiler.Compile(blurPath, "PSMain", "ps_6_0");

        ID3D12RootSignature* ssaoRoot = null;
        ID3D12RootSignature* blurRoot = null;

        try
        {
            ssaoRoot = CreateSsaoRootSignature(ctx.Device);
            blurRoot = CreateBlurRootSignature(ctx.Device);

            ID3D12PipelineState* ssaoPso = CreateFullscreenPso(ctx.Device, ssaoRoot, ssaoVs, ssaoPs);
            ID3D12PipelineState* blurPso = CreateFullscreenPso(ctx.Device, blurRoot, ssaoVs, blurPs);

            _ssaoRootSignature = new ComPtr<ID3D12RootSignature>(ssaoRoot);
            _blurRootSignature = new ComPtr<ID3D12RootSignature>(blurRoot);
            _ssaoPipeline = new ComPtr<ID3D12PipelineState>(ssaoPso);
            _blurPipeline = new ComPtr<ID3D12PipelineState>(blurPso);
        }
        catch
        {
            if (blurRoot != null) blurRoot->Release();
            if (ssaoRoot != null) ssaoRoot->Release();
            throw;
        }
    }

    public void Render(
        ID3D12GraphicsCommandList* cmd,
        GpuDescriptorHandle normalSRV,
        GpuDescriptorHandle depthSRV,
        Matrix4x4 proj,
        Matrix4x4 invProj)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cmd is null) throw new ArgumentNullException(nameof(cmd));

        SsaoConstants constants = BuildConstants(proj, invProj);
        _constantBuffer.Update(MemoryMarshal.CreateReadOnlySpan(ref constants, 1));

        ResourceBarrier toRt = MakeTransitionBarrier(
            _aoResource.Handle,
            ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource,
            ResourceStates.RenderTarget);
        cmd->ResourceBarrier(1, ref toRt);

        Viewport vp = new(0f, 0f, _width, _height, 0f, 1f);
        Box2D<int> sc = new(0, 0, _width, _height);
        cmd->RSSetViewports(1, ref vp);
        cmd->RSSetScissorRects(1, ref sc);

        CpuDescriptorHandle aoRtv = _aoRtv;
        cmd->OMSetRenderTargets(1, &aoRtv, false, null);
        float* clear = stackalloc float[4] { 1f, 1f, 1f, 1f };
        cmd->ClearRenderTargetView(_aoRtv, clear, 0, null);

        cmd->SetGraphicsRootSignature(_ssaoRootSignature.Handle);
        cmd->SetPipelineState(_ssaoPipeline.Handle);
        cmd->SetGraphicsRootConstantBufferView(0, _constantBuffer.GpuAddress);
        cmd->SetGraphicsRootDescriptorTable(1, normalSRV);
        cmd->SetGraphicsRootDescriptorTable(2, depthSRV);
        cmd->SetGraphicsRootDescriptorTable(3, _noiseTexture.SRVGpu);
        cmd->IASetPrimitiveTopology((D3DPrimitiveTopology)4u);
        cmd->DrawInstanced(3, 1, 0, 0);

        ResourceBarrier toSrv = MakeTransitionBarrier(
            _aoResource.Handle,
            ResourceStates.RenderTarget,
            ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
        cmd->ResourceBarrier(1, ref toSrv);
    }

    public void Blur(ID3D12GraphicsCommandList* cmd)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cmd is null) throw new ArgumentNullException(nameof(cmd));

        ResourceBarrier toRt = MakeTransitionBarrier(
            _blurredAoResource.Handle,
            ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource,
            ResourceStates.RenderTarget);
        cmd->ResourceBarrier(1, ref toRt);

        Viewport vp = new(0f, 0f, _width, _height, 0f, 1f);
        Box2D<int> sc = new(0, 0, _width, _height);
        cmd->RSSetViewports(1, ref vp);
        cmd->RSSetScissorRects(1, ref sc);

        CpuDescriptorHandle blurRtv = _blurRtv;
        cmd->OMSetRenderTargets(1, &blurRtv, false, null);
        float* clear = stackalloc float[4] { 1f, 1f, 1f, 1f };
        cmd->ClearRenderTargetView(_blurRtv, clear, 0, null);

        cmd->SetGraphicsRootSignature(_blurRootSignature.Handle);
        cmd->SetPipelineState(_blurPipeline.Handle);
        cmd->SetGraphicsRootDescriptorTable(0, _aoSrvGpu);
        cmd->IASetPrimitiveTopology((D3DPrimitiveTopology)4u);
        cmd->DrawInstanced(3, 1, 0, 0);

        ResourceBarrier toSrv = MakeTransitionBarrier(
            _blurredAoResource.Handle,
            ResourceStates.RenderTarget,
            ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
        cmd->ResourceBarrier(1, ref toSrv);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _blurPipeline.Dispose();
        _ssaoPipeline.Dispose();
        _blurRootSignature.Dispose();
        _ssaoRootSignature.Dispose();
        _blurredAoResource.Dispose();
        _aoResource.Dispose();
        _noiseTexture.Dispose();
        _constantBuffer.Dispose();
    }

    private void GenerateKernel()
    {
        var random = new Random(42);

        for (int i = 0; i < _kernel.Length; i++)
        {
            Vector3 sample = new(
                NextFloat(random, -1f, 1f),
                NextFloat(random, -1f, 1f),
                NextFloat(random, 0f, 1f));

            if (sample.LengthSquared() < 1e-6f)
                sample = Vector3.UnitZ;

            sample = Vector3.Normalize(sample);
            sample *= NextFloat(random, 0f, 1f);

            float t = i / (float)(_kernel.Length - 1);
            float scale = Lerp(0.1f, 1f, t * t);
            sample *= scale;

            _kernel[i] = new Vector4(sample, 0f);
        }
    }

    private GpuTexture2D CreateNoiseTexture(DescriptorHeap srvHeap)
    {
        Span<byte> noise = stackalloc byte[4 * 4 * 4];
        var random = new Random(777);

        for (int i = 0; i < 16; i++)
        {
            float x = NextFloat(random, -1f, 1f);
            float y = NextFloat(random, -1f, 1f);

            int o = i * 4;
            noise[o + 0] = EncodeSigned(x);
            noise[o + 1] = EncodeSigned(y);
            noise[o + 2] = EncodeSigned(0f);
            noise[o + 3] = 255;
        }

        return GpuTexture2D.FromBytes(_ctx, srvHeap, noise, 4, 4);
    }

    private static byte EncodeSigned(float value)
    {
        float clamped = Math.Clamp(value, -1f, 1f);
        float remapped = (clamped * 0.5f) + 0.5f;
        return (byte)Math.Clamp((int)MathF.Round(remapped * 255f), 0, 255);
    }

    private SsaoConstants BuildConstants(Matrix4x4 proj, Matrix4x4 invProj)
    {
        return new SsaoConstants
        {
            Projection = proj,
            InverseProjection = invProj,
            NoiseScaleRadiusBias = new Vector4(_width / 4f, _height / 4f, 0.5f, 0.025f),
            Kernel0 = _kernel[0],
            Kernel1 = _kernel[1],
            Kernel2 = _kernel[2],
            Kernel3 = _kernel[3],
            Kernel4 = _kernel[4],
            Kernel5 = _kernel[5],
            Kernel6 = _kernel[6],
            Kernel7 = _kernel[7],
            Kernel8 = _kernel[8],
            Kernel9 = _kernel[9],
            Kernel10 = _kernel[10],
            Kernel11 = _kernel[11],
            Kernel12 = _kernel[12],
            Kernel13 = _kernel[13],
            Kernel14 = _kernel[14],
            Kernel15 = _kernel[15],
        };
    }

    private ComPtr<ID3D12Resource> CreateAoRenderTarget(int width, int height)
    {
        HeapProperties heap = new() { Type = HeapType.Default };
        ResourceDesc desc = new()
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (ulong)width,
            Height = (uint)height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatR8Unorm,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowRenderTarget,
        };

        void* ptr = null;
        Guid guid = typeof(ID3D12Resource).GUID;
        SilkMarshal.ThrowHResult(
            _ctx.Device->CreateCommittedResource(
                ref heap,
                HeapFlags.None,
                ref desc,
                ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource,
                (ClearValue*)null,
                ref guid,
                ref ptr));

        return new ComPtr<ID3D12Resource>((ID3D12Resource*)ptr);
    }

    private void CreateAoViews(
        ID3D12Resource* resource,
        CpuDescriptorHandle rtv,
        CpuDescriptorHandle srv)
    {
        RenderTargetViewDesc rtvDesc = new()
        {
            Format = Format.FormatR8Unorm,
            ViewDimension = RtvDimension.Texture2D,
            Anonymous = new RenderTargetViewDescUnion
            {
                Texture2D = new Tex2DRtv { MipSlice = 0, PlaneSlice = 0 },
            },
        };

        ShaderResourceViewDesc srvDesc = new()
        {
            Format = Format.FormatR8Unorm,
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

        _ctx.Device->CreateRenderTargetView(resource, &rtvDesc, rtv);
        _ctx.Device->CreateShaderResourceView(resource, &srvDesc, srv);
    }

    private static ID3D12RootSignature* CreateSsaoRootSignature(ID3D12Device* device)
    {
        DescriptorRange* ranges = stackalloc DescriptorRange[3];
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
        ranges[2] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 2,
            RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 0,
        };

        RootParameter* parameters = stackalloc RootParameter[4];

        parameters[0] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv,
            ShaderVisibility = ShaderVisibility.Pixel,
        };
        parameters[0].Anonymous.Descriptor.ShaderRegister = 0;
        parameters[0].Anonymous.Descriptor.RegisterSpace = 0;

        parameters[1] = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            ShaderVisibility = ShaderVisibility.Pixel,
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
        parameters[3] = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            ShaderVisibility = ShaderVisibility.Pixel,
            Anonymous = new RootParameterUnion
            {
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = &ranges[2],
                },
            },
        };

        StaticSamplerDesc* samplers = stackalloc StaticSamplerDesc[2];
        samplers[0] = MakeLinearClampSampler(0);
        samplers[1] = MakePointWrapSampler(1);

        RootSignatureDesc desc = new()
        {
            NumParameters = 4,
            PParameters = parameters,
            NumStaticSamplers = 2,
            PStaticSamplers = samplers,
            Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
        };

        return SerializeAndCreateRootSignature(device, desc);
    }

    private static ID3D12RootSignature* CreateBlurRootSignature(ID3D12Device* device)
    {
        DescriptorRange sourceRange = new()
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 0,
            RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 0,
        };

        RootParameter parameter = new()
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            ShaderVisibility = ShaderVisibility.Pixel,
            Anonymous = new RootParameterUnion
            {
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = &sourceRange,
                },
            },
        };
        StaticSamplerDesc sampler = MakeLinearClampSampler(0);

        RootSignatureDesc desc = new()
        {
            NumParameters = 1,
            PParameters = &parameter,
            NumStaticSamplers = 1,
            PStaticSamplers = &sampler,
            Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
        };

        return SerializeAndCreateRootSignature(device, desc);
    }

    private static ID3D12PipelineState* CreateFullscreenPso(
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
        desc.RTVFormats[0] = Format.FormatR8Unorm;

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

    private static StaticSamplerDesc MakePointWrapSampler(uint shaderRegister) => new()
    {
        Filter = Filter.MinMagMipPoint,
        AddressU = TextureAddressMode.Wrap,
        AddressV = TextureAddressMode.Wrap,
        AddressW = TextureAddressMode.Wrap,
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

    private static float NextFloat(Random random, float min, float max)
    {
        return min + ((float)random.NextDouble() * (max - min));
    }

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

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

// Назначение:   SSAO-проход DX12: генерирует kernel/noise, рендерит AO в R8_UNORM и выполняет blur в отдельную текстуру.
// Зависит от:   RenderContext, GBuffer (как источник normal/depth SRV), PipelineBuilder/ShaderCompiler (подготовка DXIL/PSO), DescriptorHeap, GpuBuffer<T>, GpuTexture2D
// Используется: Dx12Renderer (этапы SSAO Render + Blur перед lighting pass)
