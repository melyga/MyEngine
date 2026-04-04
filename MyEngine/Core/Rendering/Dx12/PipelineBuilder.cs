#nullable enable

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using System.Runtime.InteropServices;
using System.Text;

namespace MyEngine.Core.Rendering.Dx12;

/// <summary>
/// Fluent builder для <see cref="ID3D12PipelineState"/> и
/// стандартной корневой подписи движка (6 слотов + 3 статических сэмплера).
///
/// Использование:
/// <code>
///   var (pso, root) = new PipelineBuilder()
///       .SetVertexShader(vsBlob)
///       .SetPixelShader(psBlob)
///       .SetInputLayout(elems)
///       .EnableDepthTest()
///       .SetCullMode(CullMode.Back)
///       .Build(ctx);
/// </code>
///
/// Вызывающий код владеет возвращёнными COM-объектами и обязан вызвать Release().
/// </summary>
internal sealed unsafe class PipelineBuilder
{
    // D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND = 0xFFFFFFFF
    private const uint OffsetAppend = 0xFFFF_FFFFu;

    // ── Накапливаемое состояние ───────────────────────────────────────────────

    private IDxcBlob*          _vs          = null;
    private IDxcBlob*          _ps          = null;
    private Format[]           _rtFormats   = [Format.FormatR8G8B8A8Unorm];
    private Format             _dsFormat    = Format.FormatD32Float;
    private InputElementDesc[] _inputElems  = [];
    private bool               _depthEnable = false;
    private bool               _depthWrite  = true;
    private bool               _blendAlpha  = false;
    private CullMode           _cullMode    = CullMode.Back;
    private FillMode           _fillMode    = FillMode.Solid;

    // ── Fluent API ────────────────────────────────────────────────────────────

    /// <summary>Устанавливает DXIL-блоб вершинного шейдера.</summary>
    public PipelineBuilder SetVertexShader(IDxcBlob* blob)
    {
        _vs = blob;
        return this;
    }

    /// <summary>Устанавливает DXIL-блоб пиксельного шейдера.</summary>
    public PipelineBuilder SetPixelShader(IDxcBlob* blob)
    {
        _ps = blob;
        return this;
    }

    /// <summary>Задаёт форматы RTV (1–8 слотов).</summary>
    public PipelineBuilder SetRenderTargetFormats(params Format[] fmts)
    {
        if (fmts.Length is 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(fmts), "D3D12 поддерживает 1–8 render-target'ов.");
        _rtFormats = fmts;
        return this;
    }

    /// <summary>Задаёт формат буфера глубины (DSV).</summary>
    public PipelineBuilder SetDepthFormat(Format fmt)
    {
        _dsFormat = fmt;
        return this;
    }

    /// <summary>
    /// Задаёт input layout.
    /// SemanticName в каждом <see cref="InputElementDesc"/> — нативная UTF-8 строка;
    /// вызывающий код отвечает за её жизненный цикл.
    /// </summary>
    public PipelineBuilder SetInputLayout(params InputElementDesc[] elems)
    {
        _inputElems = elems;
        return this;
    }

    /// <summary>Включает тест глубины. <paramref name="writeDepth"/> = false → read-only.</summary>
    public PipelineBuilder EnableDepthTest(bool writeDepth = true)
    {
        _depthEnable = true;
        _depthWrite  = writeDepth;
        return this;
    }

    /// <summary>Включает тест глубины без записи (эквивалент EnableDepthTest(false)).</summary>
    public PipelineBuilder EnableDepthTestReadOnly()
    {
        _depthEnable = true;
        _depthWrite  = false;
        return this;
    }

    /// <summary>Включает альфа-блендинг (SrcAlpha / InvSrcAlpha).</summary>
    public PipelineBuilder SetBlendAlpha()
    {
        _blendAlpha = true;
        return this;
    }

    /// <summary>Задаёт режим отсечения граней.</summary>
    public PipelineBuilder SetCullMode(CullMode mode)
    {
        _cullMode = mode;
        return this;
    }

    /// <summary>Включает режим заливки Wireframe.</summary>
    public PipelineBuilder SetFillWireframe()
    {
        _fillMode = FillMode.Wireframe;
        return this;
    }

    // ── Построение ────────────────────────────────────────────────────────────

    /// <summary>
    /// Создаёт <see cref="ID3D12RootSignature"/> и <see cref="ID3D12PipelineState"/>.
    /// При сбое создания PSO корневая подпись автоматически освобождается.
    /// </summary>
    public (ID3D12PipelineState* pso, ID3D12RootSignature* root) Build(RenderContext ctx)
    {
        if (_vs == null) throw new InvalidOperationException("PipelineBuilder: вершинный шейдер не задан.");
        if (_ps == null) throw new InvalidOperationException("PipelineBuilder: пиксельный шейдер не задан.");

        ID3D12RootSignature* root = CreateRootSignature(ctx.Device);
        ID3D12PipelineState* pso;
        try
        {
            pso = CreatePso(ctx.Device, root);
        }
        catch
        {
            root->Release();
            throw;
        }
        return (pso, root);
    }

    // ── Корневая подпись ──────────────────────────────────────────────────────

    /// <summary>
    /// Стандартная корневая подпись движка:
    /// [0] Inline CBV b0 — MVP (Matrix4x4×3) + материал (float4 + float2), All
    /// [1] Inline CBV b1 — массив источников света, Pixel
    /// [2] Table: SRV t0–t3 — albedo / normal / metallic-roughness / emissive, Pixel
    /// [3] Table: SRV t4   — shadow map, Pixel
    /// [4] Table: SRV t5   — SSAO map, Pixel
    /// [5] Table: UAV u0   — буфер частиц (particle compute), All
    /// Статические сэмплеры:
    ///   s0 — Anisotropic×8 Wrap          (материальные текстуры)
    ///   s1 — Comparison LinearClamp PCF  (shadow map, LessEqual, border=white)
    ///   s2 — Bilinear Clamp              (SSAO буфер)
    /// </summary>
    private static ID3D12RootSignature* CreateRootSignature(ID3D12Device* device)
    {
        // ── Descriptor ranges для table-параметров ────────────────────────────

        const int RangeCount = 4;
        DescriptorRange* ranges = stackalloc DescriptorRange[RangeCount];

        // t0–t3: albedo / normal / metallic-roughness / emissive
        ranges[0] = MakeSrvRange(baseReg: 0, count: 4);
        // t4: shadow map
        ranges[1] = MakeSrvRange(baseReg: 4, count: 1);
        // t5: SSAO map
        ranges[2] = MakeSrvRange(baseReg: 5, count: 1);
        // u0: particle compute UAV
        ranges[3] = new DescriptorRange
        {
            RangeType                         = DescriptorRangeType.Uav,
            NumDescriptors                    = 1,
            BaseShaderRegister                = 0,
            RegisterSpace                     = 0,
            OffsetInDescriptorsFromTableStart = OffsetAppend,
        };

        // ── Root parameters ───────────────────────────────────────────────────

        const int ParamCount = 6;
        RootParameter* p = stackalloc RootParameter[ParamCount];

        // [0] Inline CBV b0 — MVP + материал (вершинный + пиксельный шейдеры)
        p[0].ParameterType                       = RootParameterType.Cbv;
        p[0].ShaderVisibility                    = ShaderVisibility.All;
        p[0].Anonymous.Descriptor.ShaderRegister = 0;
        p[0].Anonymous.Descriptor.RegisterSpace  = 0;

        // [1] Inline CBV b1 — освещение (только пиксельный шейдер)
        p[1].ParameterType                       = RootParameterType.Cbv;
        p[1].ShaderVisibility                    = ShaderVisibility.Pixel;
        p[1].Anonymous.Descriptor.ShaderRegister = 1;
        p[1].Anonymous.Descriptor.RegisterSpace  = 0;

        // [2] Descriptor table: SRV t0–t3 (материальные текстуры)
        SetTableParam(ref p[2], ShaderVisibility.Pixel, &ranges[0], rangeCount: 1);
        // [3] Descriptor table: SRV t4 (shadow map)
        SetTableParam(ref p[3], ShaderVisibility.Pixel, &ranges[1], rangeCount: 1);
        // [4] Descriptor table: SRV t5 (SSAO)
        SetTableParam(ref p[4], ShaderVisibility.Pixel, &ranges[2], rangeCount: 1);
        // [5] Descriptor table: UAV u0 (particle — VS для instancing / CS для simulate)
        SetTableParam(ref p[5], ShaderVisibility.All,   &ranges[3], rangeCount: 1);

        // ── Статические сэмплеры ──────────────────────────────────────────────

        const int SamplerCount = 3;
        StaticSamplerDesc* samplers = stackalloc StaticSamplerDesc[SamplerCount];
        FillStaticSamplers(samplers);

        // ── Сборка и сериализация ─────────────────────────────────────────────

        RootSignatureDesc rsDesc = new()
        {
            NumParameters     = (uint)ParamCount,
            PParameters       = p,
            NumStaticSamplers = (uint)SamplerCount,
            PStaticSamplers   = samplers,
            Flags             = RootSignatureFlags.AllowInputAssemblerInputLayout,
        };

        void* sigBlob = null;
        void* errBlob = null;

        int serHr = D3D12SerializeRootSignature(
            &rsDesc, D3DRootSignatureVersion.Version10, &sigBlob, &errBlob);

        if (serHr < 0)
        {
            string msg = ReadBlobText(errBlob);
            ComRelease(errBlob);
            throw new InvalidOperationException(
                $"D3D12SerializeRootSignature провалился (hr=0x{serHr:X8}):\n{msg}");
        }
        ComRelease(errBlob); // warnings не фатальны, но blob может быть ненулевым

        GetBlobBuffer(sigBlob, out void* bufPtr, out nuint bufSize);

        void* rootPtr  = null;
        Guid  rootGuid = typeof(ID3D12RootSignature).GUID;
        int   createHr = device->CreateRootSignature(0, bufPtr, bufSize, ref rootGuid, ref rootPtr);
        ComRelease(sigBlob);
        SilkMarshal.ThrowHResult(createHr);

        return (ID3D12RootSignature*)rootPtr;
    }

    // ── PSO ───────────────────────────────────────────────────────────────────

    private ID3D12PipelineState* CreatePso(ID3D12Device* device, ID3D12RootSignature* rootSig)
    {
        // Пинируем InputElementDesc[] через GCHandle, чтобы не держать fixed-блок
        // при обращении к нескольким вспомогательным методам.
        GCHandle pin = _inputElems.Length > 0
            ? GCHandle.Alloc(_inputElems, GCHandleType.Pinned)
            : default;
        try
        {
            InputElementDesc* pElems = pin.IsAllocated
                ? (InputElementDesc*)pin.AddrOfPinnedObject()
                : null;

            GraphicsPipelineStateDesc desc = new()
            {
                PRootSignature        = rootSig,
                VS                    = MakeShaderBytecode(_vs),
                PS                    = MakeShaderBytecode(_ps),
                RasterizerState       = BuildRasterizerDesc(),
                BlendState            = BuildBlendDesc(),
                DepthStencilState     = BuildDepthStencilDesc(),
                InputLayout           = new InputLayoutDesc
                {
                    PInputElementDescs = pElems,
                    NumElements        = (uint)_inputElems.Length,
                },
                SampleMask            = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                NumRenderTargets      = (uint)_rtFormats.Length,
                DSVFormat             = _dsFormat,
                SampleDesc            = new SampleDesc { Count = 1, Quality = 0 },
            };

            // RTVFormats — [InlineArray(8)] в Silk.NET (.NET 8+): индексируем напрямую.
            for (int i = 0; i < _rtFormats.Length; i++)
                desc.RTVFormats[i] = _rtFormats[i];

            void* psoPtr  = null;
            Guid  psoGuid = typeof(ID3D12PipelineState).GUID;
            SilkMarshal.ThrowHResult(
                device->CreateGraphicsPipelineState(ref desc, ref psoGuid, ref psoPtr));

            return (ID3D12PipelineState*)psoPtr;
        }
        finally
        {
            if (pin.IsAllocated) pin.Free();
        }
    }

    // ── Вспомогательные методы построения состояний ───────────────────────────

    private static DescriptorRange MakeSrvRange(uint baseReg, uint count) => new()
    {
        RangeType                         = DescriptorRangeType.Srv,
        NumDescriptors                    = count,
        BaseShaderRegister                = baseReg,
        RegisterSpace                     = 0,
        OffsetInDescriptorsFromTableStart = OffsetAppend,
    };

    private static void SetTableParam(
        ref RootParameter   param,
        ShaderVisibility    visibility,
        DescriptorRange*    range,
        uint                rangeCount)
    {
        param.ParameterType                                  = RootParameterType.DescriptorTable;
        param.ShaderVisibility                               = visibility;
        param.Anonymous.DescriptorTable.NumDescriptorRanges = rangeCount;
        param.Anonymous.DescriptorTable.PDescriptorRanges   = range;
    }

    private static void FillStaticSamplers(StaticSamplerDesc* s)
    {
        // s0: Anisotropic×8, Wrap — материальные текстуры
        s[0] = new StaticSamplerDesc
        {
            Filter           = Filter.Anisotropic,
            AddressU         = TextureAddressMode.Wrap,
            AddressV         = TextureAddressMode.Wrap,
            AddressW         = TextureAddressMode.Wrap,
            MipLODBias       = 0f,
            MaxAnisotropy    = 8,
            ComparisonFunc   = ComparisonFunc.Never,
            BorderColor      = StaticBorderColor.TransparentBlack,
            MinLOD           = 0f,
            MaxLOD           = float.MaxValue,
            ShaderRegister   = 0,
            RegisterSpace    = 0,
            ShaderVisibility = ShaderVisibility.Pixel,
        };

        // s1: Comparison LinearClamp, LessEqual, border=white — shadow map PCF
        s[1] = new StaticSamplerDesc
        {
            Filter           = Filter.ComparisonMinMagLinearMipPoint,
            AddressU         = TextureAddressMode.Border,
            AddressV         = TextureAddressMode.Border,
            AddressW         = TextureAddressMode.Border,
            MipLODBias       = 0f,
            MaxAnisotropy    = 1,
            ComparisonFunc   = ComparisonFunc.LessEqual,
            BorderColor      = StaticBorderColor.OpaqueWhite,
            MinLOD           = 0f,
            MaxLOD           = 0f,               // shadow map — один mip
            ShaderRegister   = 1,
            RegisterSpace    = 0,
            ShaderVisibility = ShaderVisibility.Pixel,
        };

        // s2: Bilinear Clamp — SSAO (нет mip, границы не нужны)
        s[2] = new StaticSamplerDesc
        {
            Filter           = Filter.MinMagLinearMipPoint,
            AddressU         = TextureAddressMode.Clamp,
            AddressV         = TextureAddressMode.Clamp,
            AddressW         = TextureAddressMode.Clamp,
            MipLODBias       = 0f,
            MaxAnisotropy    = 1,
            ComparisonFunc   = ComparisonFunc.Never,
            BorderColor      = StaticBorderColor.TransparentBlack,
            MinLOD           = 0f,
            MaxLOD           = float.MaxValue,
            ShaderRegister   = 2,
            RegisterSpace    = 0,
            ShaderVisibility = ShaderVisibility.Pixel,
        };
    }

    private static ShaderBytecode MakeShaderBytecode(IDxcBlob* blob) => new()
    {
        PShaderBytecode = blob->GetBufferPointer(),
        BytecodeLength  = blob->GetBufferSize(),
    };

    private RasterizerDesc BuildRasterizerDesc() => new()
    {
        FillMode              = _fillMode,
        CullMode              = _cullMode,
        FrontCounterClockwise = 0,              // CW = front (DirectX default)
        DepthBias             = 0,
        DepthBiasClamp        = 0f,
        SlopeScaledDepthBias  = 0f,
        DepthClipEnable       = 1,
        MultisampleEnable     = 0,
        AntialiasedLineEnable = 0,
        ForcedSampleCount     = 0,
        ConservativeRaster    = ConservativeRasterizationMode.Off,
    };

    private BlendDesc BuildBlendDesc()
    {
        RenderTargetBlendDesc rt = _blendAlpha
            ? new RenderTargetBlendDesc
            {
                BlendEnable           = 1,
                LogicOpEnable         = 0,
                SrcBlend              = Blend.SrcAlpha,
                DestBlend             = Blend.InvSrcAlpha,
                BlendOp               = BlendOp.Add,
                SrcBlendAlpha         = Blend.One,
                DestBlendAlpha        = Blend.InvSrcAlpha,
                BlendOpAlpha          = BlendOp.Add,
                LogicOp               = LogicOp.Noop,
                RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            }
            : new RenderTargetBlendDesc
            {
                BlendEnable           = 0,
                LogicOpEnable         = 0,
                SrcBlend              = Blend.One,
                DestBlend             = Blend.Zero,
                BlendOp               = BlendOp.Add,
                SrcBlendAlpha         = Blend.One,
                DestBlendAlpha        = Blend.Zero,
                BlendOpAlpha          = BlendOp.Add,
                LogicOp               = LogicOp.Noop,
                RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            };

        BlendDesc desc = new() { AlphaToCoverageEnable = 0, IndependentBlendEnable = 0 };

        // RenderTarget — [InlineArray(8)] в Silk.NET (.NET 8+)
        for (int i = 0; i < 8; i++)
            desc.RenderTarget[i] = rt;

        return desc;
    }

    private DepthStencilDesc BuildDepthStencilDesc()
    {
        DepthStencilopDesc passThrough = new()
        {
            StencilFailOp      = StencilOp.Keep,
            StencilDepthFailOp = StencilOp.Keep,
            StencilPassOp      = StencilOp.Keep,
            StencilFunc        = ComparisonFunc.Always,
        };
        return new DepthStencilDesc
        {
            DepthEnable      = _depthEnable ? 1 : 0,
            DepthWriteMask   = _depthWrite ? DepthWriteMask.All : DepthWriteMask.Zero,
            DepthFunc        = ComparisonFunc.LessEqual,
            StencilEnable    = 0,
            StencilReadMask  = 0xFF,
            StencilWriteMask = 0xFF,
            FrontFace        = passThrough,
            BackFace         = passThrough,
        };
    }

    // ── COM / blob утилиты ────────────────────────────────────────────────────

    // Прямой P/Invoke в d3d12.dll избегает зависимости от
    // Silk.NET.Direct3D.Compiler (содержит ID3DBlob).
    // Вместо ID3DBlob** используем void** — бинарно совместимо.
    [DllImport("d3d12", EntryPoint = "D3D12SerializeRootSignature")]
    private static extern int D3D12SerializeRootSignature(
        RootSignatureDesc*      pDesc,
        D3DRootSignatureVersion version,
        void**                  ppBlob,
        void**                  ppErrorBlob);

    // IUnknown vtable: [0]=QueryInterface  [1]=AddRef  [2]=Release
    // ID3DBlob vtable: [3]=GetBufferPointer  [4]=GetBufferSize
    // IDxcBlob имеет идентичный layout (оба наследуют IUnknown и добавляют те же два метода).
    private static void GetBlobBuffer(void* blob, out void* ptr, out nuint size)
    {
        void** vtbl = *(void***)blob;
        ptr  = ((delegate* unmanaged[Stdcall]<void*, void*>)  vtbl[3])(blob);
        size = ((delegate* unmanaged[Stdcall]<void*, nuint>)   vtbl[4])(blob);
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
        if (ptr == null || size == 0) return "(пустой error blob)";
        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>((byte*)ptr, (int)size)).TrimEnd('\0');
    }
}

// Назначение:   Fluent builder для D3D12 PSO + стандартная корневая подпись движка (6 слотов, 3 сэмплера)
// Зависит от:   RenderContext, Silk.NET.Direct3D12, Silk.NET.Direct3D.Dxc, Silk.NET.DXGI, Silk.NET.Core.Native
// Используется: Dx12Renderer (создаёт PSO для каждого прохода: opaque, shadow, particles, postprocess)
