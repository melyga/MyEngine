#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MyEngine.Core.Rendering.Dx12;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;

namespace MyEngine.Core.Rendering.Dx12.Passes;

internal sealed unsafe class PostProcessPass : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PostProcessCB
    {
        public float Time;
        public float VignetteStr;
        public float GrainStr;
        public float _pad;
    }

    private readonly GpuBuffer<PostProcessCB> _constantBuffer;
    private ComPtr<ID3D12RootSignature> _rootSignature;
    private ComPtr<ID3D12PipelineState> _pipelineState;
    private bool _disposed;

    public PostProcessPass(RenderContext ctx, ShaderCompiler shaderCompiler)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (shaderCompiler is null) throw new ArgumentNullException(nameof(shaderCompiler));

        _constantBuffer = GpuBuffer<PostProcessCB>.CreateConstant(ctx, 1);

        string vsPath = ResolveShaderPath("fullscreen.vs.hlsl");
        string psPath = ResolveShaderPath("postprocess.ps.hlsl");
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
        GpuDescriptorHandle hdrSceneSrv,
        CpuDescriptorHandle backBufferRtv,
        int width,
        int height,
        float time)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cmd is null) throw new ArgumentNullException(nameof(cmd));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        PostProcessCB cb = new()
        {
            Time = time,
            VignetteStr = 0.4f,
            GrainStr = 0.03f,
            _pad = 0f,
        };
        _constantBuffer.Update(MemoryMarshal.CreateReadOnlySpan(ref cb, 1));

        Viewport viewport = new(0f, 0f, width, height, 0f, 1f);
        Box2D<int> scissor = new(0, 0, width, height);
        cmd->RSSetViewports(1, ref viewport);
        cmd->RSSetScissorRects(1, ref scissor);
        cmd->OMSetRenderTargets(1, &backBufferRtv, false, null);

        cmd->SetGraphicsRootSignature(_rootSignature.Handle);
        cmd->SetPipelineState(_pipelineState.Handle);
        cmd->SetGraphicsRootConstantBufferView(0, _constantBuffer.GpuAddress);
        cmd->SetGraphicsRootDescriptorTable(1, hdrSceneSrv);
        cmd->IASetPrimitiveTopology((D3DPrimitiveTopology)4u);
        cmd->DrawInstanced(6, 1, 0, 0);
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
        DescriptorRange sceneRange = new()
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 0,
            RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 0,
        };

        RootParameter* parameters = stackalloc RootParameter[2];
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
                    PDescriptorRanges = &sceneRange,
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
            NumParameters = 2,
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
            BlendState = BuildBlendDesc(),
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

// Назначение:   Постобработка fullscreen-quad: читает HDR SRV, пишет в backbuffer RTV и передаёт PostProcessCB (Time/Vignette/Grain) для Reinhard+vignette+grain.
// Зависит от:   RenderContext, ShaderCompiler, GpuBuffer<T>, Silk.NET.Direct3D12, Silk.NET.DXGI
// Используется: Dx12Renderer как финальный post-process pass перед Present
