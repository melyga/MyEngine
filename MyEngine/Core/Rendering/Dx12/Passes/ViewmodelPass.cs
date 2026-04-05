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

internal sealed unsafe class ViewmodelPass : IDisposable
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ViewmodelFovConstants
    {
        public float FovRadians;
        public Vector3 _pad;
    }

    private readonly RenderContext _ctx;
    private readonly MeshRegistry _meshRegistry;
    private readonly TextureRegistry _textureRegistry;
    private readonly DescriptorHeap _dsvHeap;
    private readonly GpuBuffer<SceneConstantBuffer> _sceneBuffer;
    private readonly GpuBuffer<ViewmodelFovConstants> _fovBuffer;
    private readonly CpuDescriptorHandle _viewmodelDsv;

    private ComPtr<ID3D12RootSignature> _rootSignature;
    private ComPtr<ID3D12PipelineState> _pipelineState;
    private ComPtr<ID3D12Resource> _depthResource;

    private int _width;
    private int _height;
    private bool _disposed;

    public ViewmodelPass(
        RenderContext ctx,
        ShaderCompiler shaderCompiler,
        MeshRegistry meshRegistry,
        TextureRegistry textureRegistry,
        DescriptorHeap dsvHeap)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (shaderCompiler is null) throw new ArgumentNullException(nameof(shaderCompiler));
        if (meshRegistry is null) throw new ArgumentNullException(nameof(meshRegistry));
        if (textureRegistry is null) throw new ArgumentNullException(nameof(textureRegistry));
        if (dsvHeap is null) throw new ArgumentNullException(nameof(dsvHeap));

        _ctx = ctx;
        _meshRegistry = meshRegistry;
        _textureRegistry = textureRegistry;
        _dsvHeap = dsvHeap;
        _sceneBuffer = GpuBuffer<SceneConstantBuffer>.CreateConstant(ctx, 1);
        _fovBuffer = GpuBuffer<ViewmodelFovConstants>.CreateConstant(ctx, 1);

        _viewmodelDsv = _dsvHeap.Allocate();
        _width = ctx.Width;
        _height = ctx.Height;
        _depthResource = CreateDepthResource(_width, _height);
        CreateDepthView();

        string vsPath = ResolveShaderPath("viewmodel.vs.hlsl");
        string psPath = ResolveShaderPath("viewmodel.ps.hlsl");
        IDxcBlob* vsBlob = shaderCompiler.Compile(vsPath, "VSMain", "vs_6_0");
        IDxcBlob* psBlob = shaderCompiler.Compile(psPath, "PSMain", "ps_6_0");

        nint pos = SilkMarshal.StringToPtr("POSITION", NativeStringEncoding.UTF8);
        nint nrm = SilkMarshal.StringToPtr("NORMAL", NativeStringEncoding.UTF8);
        nint uv0 = SilkMarshal.StringToPtr("TEXCOORD", NativeStringEncoding.UTF8);
        nint tan = SilkMarshal.StringToPtr("TANGENT", NativeStringEncoding.UTF8);
        nint wei = SilkMarshal.StringToPtr("BLENDWEIGHTS", NativeStringEncoding.UTF8);
        nint idx = SilkMarshal.StringToPtr("BLENDINDICES", NativeStringEncoding.UTF8);

        ID3D12RootSignature* root = null;
        ID3D12PipelineState* pso = null;
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

            root = CreateRootSignature(_ctx.Device);
            pso = CreatePipelineState(_ctx.Device, root, vsBlob, psBlob, inputLayout);

            _rootSignature = new ComPtr<ID3D12RootSignature>(root);
            _pipelineState = new ComPtr<ID3D12PipelineState>(pso);
        }
        catch
        {
            if (pso != null) pso->Release();
            if (root != null) root->Release();
            throw;
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
        CpuDescriptorHandle backBufferRtv)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cmd is null) throw new ArgumentNullException(nameof(cmd));
        if (drawCalls is null) throw new ArgumentNullException(nameof(drawCalls));

        if (_ctx.Width != _width || _ctx.Height != _height)
            Resize(_ctx.Width, _ctx.Height);

        ViewmodelFovConstants fovConstants = new()
        {
            FovRadians = MathF.PI * (55f / 180f),
            _pad = Vector3.Zero,
        };
        _fovBuffer.Update(MemoryMarshal.CreateReadOnlySpan(ref fovConstants, 1));

        Viewport viewport = new(0f, 0f, _width, _height, 0f, 1f);
        Box2D<int> scissor = new(0, 0, _width, _height);
        cmd->RSSetViewports(1, ref viewport);
        cmd->RSSetScissorRects(1, ref scissor);
        CpuDescriptorHandle depthDsv = _viewmodelDsv;
        cmd->OMSetRenderTargets(1, &backBufferRtv, false, &depthDsv);
        cmd->ClearDepthStencilView(_viewmodelDsv, ClearFlags.Depth, 1f, 0, 0, null);

        cmd->SetGraphicsRootSignature(_rootSignature.Handle);
        cmd->SetPipelineState(_pipelineState.Handle);
        cmd->SetGraphicsRootConstantBufferView(1, _fovBuffer.GpuAddress);
        cmd->IASetPrimitiveTopology((D3DPrimitiveTopology)4u);

        float aspect = (float)_width / _height;
        float near = camera.Near > 0f ? camera.Near : 0.01f;
        float far = camera.Far > near ? camera.Far : 500f;
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            fovConstants.FovRadians,
            aspect,
            near,
            far);

        for (int i = 0; i < drawCalls.Count; i++)
        {
            DrawCall drawCall = drawCalls[i];
            (VertexBufferView vbv, IndexBufferView ibv, int indexCount) = _meshRegistry.GetBuffers(drawCall.Mesh);

            Matrix4x4 worldInvTranspose = Matrix4x4.Identity;
            if (Matrix4x4.Invert(drawCall.WorldMatrix, out Matrix4x4 worldInv))
                worldInvTranspose = Matrix4x4.Transpose(worldInv);

            SceneConstantBuffer scene = new()
            {
                World = drawCall.WorldMatrix,
                View = camera.View,
                Projection = projection,
                WorldInverseTranspose = worldInvTranspose,
                BaseColorFactor = drawCall.BaseColor,
                Metallic = drawCall.Metallic,
                Roughness = drawCall.Roughness,
                EmissiveFactor = drawCall.EmissiveFactor,
                _pad = 0f,
            };

            _sceneBuffer.Update(MemoryMarshal.CreateReadOnlySpan(ref scene, 1));
            cmd->SetGraphicsRootConstantBufferView(0, _sceneBuffer.GpuAddress);
            cmd->SetGraphicsRootDescriptorTable(2, _textureRegistry.GetSRV(drawCall.Albedo));

            cmd->IASetVertexBuffers(0, 1, ref vbv);
            cmd->IASetIndexBuffer(ref ibv);
            cmd->DrawIndexedInstanced((uint)indexCount, 1, 0, 0, 0);
        }
    }

    public void Resize(int w, int h)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (w <= 0) throw new ArgumentOutOfRangeException(nameof(w));
        if (h <= 0) throw new ArgumentOutOfRangeException(nameof(h));
        if (w == _width && h == _height)
            return;

        _depthResource.Dispose();
        _width = w;
        _height = h;
        _depthResource = CreateDepthResource(w, h);
        CreateDepthView();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _depthResource.Dispose();
        _pipelineState.Dispose();
        _rootSignature.Dispose();
        _fovBuffer.Dispose();
        _sceneBuffer.Dispose();
    }

    private ComPtr<ID3D12Resource> CreateDepthResource(int width, int height)
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
            Format = Format.FormatD32Float,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowDepthStencil,
        };

        ClearValue clear = new()
        {
            Format = Format.FormatD32Float,
            Anonymous = new ClearValueUnion
            {
                DepthStencil = new DepthStencilValue { Depth = 1f, Stencil = 0 },
            },
        };

        void* ptr = null;
        Guid guid = typeof(ID3D12Resource).GUID;
        SilkMarshal.ThrowHResult(
            _ctx.Device->CreateCommittedResource(
                ref heap,
                HeapFlags.None,
                ref desc,
                ResourceStates.DepthWrite,
                &clear,
                ref guid,
                ref ptr));

        return new ComPtr<ID3D12Resource>((ID3D12Resource*)ptr);
    }

    private void CreateDepthView()
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

        _ctx.Device->CreateDepthStencilView(_depthResource.Handle, &dsvDesc, _viewmodelDsv);
    }

    private static ID3D12RootSignature* CreateRootSignature(ID3D12Device* device)
    {
        DescriptorRange albedoRange = new()
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1,
            BaseShaderRegister = 0,
            RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 0,
        };

        RootParameter* parameters = stackalloc RootParameter[3];
        parameters[0] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv,
            ShaderVisibility = ShaderVisibility.All,
        };
        parameters[0].Anonymous.Descriptor.ShaderRegister = 0;
        parameters[0].Anonymous.Descriptor.RegisterSpace = 0;

        parameters[1] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv,
            ShaderVisibility = ShaderVisibility.All,
        };
        parameters[1].Anonymous.Descriptor.ShaderRegister = 1;
        parameters[1].Anonymous.Descriptor.RegisterSpace = 0;

        parameters[2] = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            ShaderVisibility = ShaderVisibility.Pixel,
            Anonymous = new RootParameterUnion
            {
                DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1,
                    PDescriptorRanges = &albedoRange,
                },
            },
        };

        StaticSamplerDesc sampler = new()
        {
            Filter = Filter.Anisotropic,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MipLODBias = 0f,
            MaxAnisotropy = 8,
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
        IDxcBlob* psBlob,
        InputElementDesc[] inputLayout)
    {
        GCHandle pin = GCHandle.Alloc(inputLayout, GCHandleType.Pinned);
        try
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
                },
                DepthStencilState = BuildDepthWriteDesc(),
                InputLayout = new InputLayoutDesc
                {
                    PInputElementDescs = (InputElementDesc*)pin.AddrOfPinnedObject(),
                    NumElements = (uint)inputLayout.Length,
                },
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
        finally
        {
            pin.Free();
        }
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

    private static DepthStencilDesc BuildDepthWriteDesc()
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

    private static ID3D12RootSignature* SerializeAndCreateRootSignature(
        ID3D12Device* device,
        RootSignatureDesc desc)
    {
        void* sigBlob = null;
        void* errBlob = null;
        int hr = D3D12SerializeRootSignature(&desc, D3DRootSignatureVersion.Version10, &sigBlob, &errBlob);
        if (hr < 0)
        {
            ComRelease(sigBlob);
            ComRelease(errBlob);
            throw new InvalidOperationException($"D3D12SerializeRootSignature failed (hr=0x{hr:X8}).");
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
}

// Назначение:   Рендер viewmodel в отдельный depth buffer D32_FLOAT, с очисткой depth каждый кадр и отдельным CBV для FOV=55°.
// Зависит от:   RenderContext, ShaderCompiler, MeshRegistry, TextureRegistry, DescriptorHeap, IRenderer.DrawCall/CameraData, Silk.NET.Direct3D12
// Используется: Dx12Renderer как отдельный проход оружия/рук после world-pass
