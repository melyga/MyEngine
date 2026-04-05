#nullable enable

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace MyEngine.Core.Rendering.Dx12;

/// <summary>
/// Иммутабельная GPU-текстура RGBA8 Unorm с готовым SRV-дескриптором.
/// Создаётся через статические фабрики; после создания CPU-копия не хранится.
/// </summary>
internal sealed unsafe class GpuTexture2D : IDisposable
{
    // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING = RGBA identity
    private const uint DefaultShader4ComponentMapping = 0x00001688u;

    // D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES
    private const uint AllSubresources = 0xFFFFFFFFu;

    // ── COM-ресурс ────────────────────────────────────────────────────────────

    private ComPtr<ID3D12Resource> _texture;
    private bool _disposed;

    // ── Свойства ─────────────────────────────────────────────────────────────

    public CpuDescriptorHandle SRV    { get; }
    public GpuDescriptorHandle SRVGpu { get; }
    public int                 Width  { get; }
    public int                 Height { get; }

    // ── Приватный конструктор ─────────────────────────────────────────────────

    private GpuTexture2D(
        ComPtr<ID3D12Resource> texture,
        CpuDescriptorHandle    srv,
        GpuDescriptorHandle    srvGpu,
        int                    width,
        int                    height)
    {
        _texture = texture;
        SRV      = srv;
        SRVGpu   = srvGpu;
        Width    = width;
        Height   = height;
    }

    // ── Статические фабрики ───────────────────────────────────────────────────

    /// <summary>
    /// Создаёт белую текстуру 1×1 RGBA (255,255,255,255).
    /// Служит дефолтным fallback-ом для отсутствующих текстур.
    /// </summary>
    public static GpuTexture2D CreateWhite1x1(RenderContext ctx, DescriptorHeap srvHeap)
    {
        if (ctx is null)     throw new ArgumentNullException(nameof(ctx));
        if (srvHeap is null) throw new ArgumentNullException(nameof(srvHeap));

        ReadOnlySpan<byte> white = new byte[] { 255, 255, 255, 255 };
        return FromBytes(ctx, srvHeap, white, 1, 1);
    }

    /// <summary>
    /// Загружает RGBA8-пиксели на GPU:
    /// upload heap → ID3D12Resource (default heap, DXGI_FORMAT_R8G8B8A8_UNORM)
    /// → CreateShaderResourceView в <paramref name="srvHeap"/>.
    /// Метод блокирует CPU до завершения копирования GPU.
    /// </summary>
    /// <param name="ctx">Контекст DX12.</param>
    /// <param name="srvHeap">Shader-visible CBV/SRV/UAV хип для регистрации SRV.</param>
    /// <param name="rgbaPixels">Плотно упакованные RGBA8-пиксели (row-major).</param>
    /// <param name="width">Ширина в пикселях (≥ 1).</param>
    /// <param name="height">Высота в пикселях (≥ 1).</param>
    public static GpuTexture2D FromBytes(
        RenderContext      ctx,
        DescriptorHeap     srvHeap,
        ReadOnlySpan<byte> rgbaPixels,
        int                width,
        int                height)
    {
        if (ctx is null)     throw new ArgumentNullException(nameof(ctx));
        if (srvHeap is null) throw new ArgumentNullException(nameof(srvHeap));
        if (width  <= 0)     throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)     throw new ArgumentOutOfRangeException(nameof(height));
        int required = width * height * 4;
        if (rgbaPixels.Length < required)
            throw new ArgumentException(
                $"Pixel buffer too small: need {required} bytes, got {rgbaPixels.Length}.",
                nameof(rgbaPixels));

        // ── 1. Описатель текстуры ─────────────────────────────────────────────

        ResourceDesc texDesc = new()
        {
            Dimension        = ResourceDimension.Texture2D,
            Alignment        = 0,
            Width            = (ulong)width,
            Height           = (uint)height,
            DepthOrArraySize = 1,
            MipLevels        = 1,
            Format           = Format.FormatR8G8B8A8Unorm,
            SampleDesc       = new SampleDesc { Count = 1, Quality = 0 },
            Layout           = TextureLayout.LayoutUnknown,
            Flags            = ResourceFlags.None,
        };

        // ── 2. Default-heap ресурс (получатель копии) ─────────────────────────

        HeapProperties defaultProps = new() { Type = HeapType.Default };
        void* texPtr  = null;
        Guid  texGuid = typeof(ID3D12Resource).GUID;

        SilkMarshal.ThrowHResult(
            ctx.Device->CreateCommittedResource(
                ref defaultProps,
                HeapFlags.None,
                ref texDesc,
                ResourceStates.CopyDest,
                null,
                ref texGuid,
                ref texPtr));

        var texture = new ComPtr<ID3D12Resource>((ID3D12Resource*)texPtr);

        try
        {
            // ── 3. Размер буфера через GetCopyableFootprints ──────────────────

            PlacedSubresourceFootprint footprint = default;
            uint  numRows      = 0;
            ulong rowSizeBytes = 0;
            ulong totalBytes   = 0;

            ctx.Device->GetCopyableFootprints(
                ref texDesc,
                0u,
                1u,
                0ul,
                ref footprint,
                ref numRows,
                ref rowSizeBytes,
                ref totalBytes);

            // ── 4. Upload-heap буфер ──────────────────────────────────────────

            ResourceDesc uploadDesc = new()
            {
                Dimension        = ResourceDimension.Buffer,
                Alignment        = 0,
                Width            = totalBytes,
                Height           = 1,
                DepthOrArraySize = 1,
                MipLevels        = 1,
                Format           = Format.FormatUnknown,
                SampleDesc       = new SampleDesc { Count = 1, Quality = 0 },
                Layout           = TextureLayout.LayoutRowMajor,
                Flags            = ResourceFlags.None,
            };

            HeapProperties uploadProps = new() { Type = HeapType.Upload };
            void* upPtr  = null;
            Guid  upGuid = typeof(ID3D12Resource).GUID;

            SilkMarshal.ThrowHResult(
                ctx.Device->CreateCommittedResource(
                    ref uploadProps,
                    HeapFlags.None,
                    ref uploadDesc,
                    ResourceStates.GenericRead,
                    null,
                    ref upGuid,
                    ref upPtr));

            var upload = new ComPtr<ID3D12Resource>((ID3D12Resource*)upPtr);

            try
            {
                // ── 5. Map → row-by-row копирование → Unmap ──────────────────
                //
                // Textures на GPU могут иметь RowPitch > width*4 (выравнивание
                // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT = 256 байт). Копируем
                // построчно, уважая целевой stride из footprint.

                void* mapped = null;
                Silk.NET.Direct3D12.Range noRead = new() { Begin = 0, End = 0 };
                SilkMarshal.ThrowHResult(upload.Handle->Map(0, ref noRead, ref mapped));

                uint srcStride = (uint)width * 4u;
                uint dstStride = footprint.Footprint.RowPitch;

                for (uint row = 0; row < (uint)height; row++)
                {
                    ReadOnlySpan<byte> src = rgbaPixels.Slice(
                        (int)(row * srcStride),
                        (int)srcStride);

                    Span<byte> dst = new((byte*)mapped + row * dstStride, (int)srcStride);
                    src.CopyTo(dst);
                }

                upload.Handle->Unmap(0, null);

                // ── 6. One-shot command list ──────────────────────────────────

                void* allocPtr  = null;
                Guid  allocGuid = typeof(ID3D12CommandAllocator).GUID;
                SilkMarshal.ThrowHResult(
                    ctx.Device->CreateCommandAllocator(
                        CommandListType.Direct,
                        ref allocGuid,
                        ref allocPtr));
                var alloc = new ComPtr<ID3D12CommandAllocator>((ID3D12CommandAllocator*)allocPtr);

                void* listPtr  = null;
                Guid  listGuid = typeof(ID3D12GraphicsCommandList).GUID;
                SilkMarshal.ThrowHResult(
                    ctx.Device->CreateCommandList(
                        0,
                        CommandListType.Direct,
                        alloc.Handle,
                        null,
                        ref listGuid,
                        ref listPtr));
                var cmd = new ComPtr<ID3D12GraphicsCommandList>((ID3D12GraphicsCommandList*)listPtr);

                // ── 7. CopyTextureRegion ──────────────────────────────────────

                TextureCopyLocation dstLoc = new()
                {
                    PResource = texture.Handle,
                    Type      = TextureCopyType.SubresourceIndex,
                };
                dstLoc.Anonymous.SubresourceIndex = 0u;

                TextureCopyLocation srcLoc = new()
                {
                    PResource = upload.Handle,
                    Type      = TextureCopyType.PlacedFootprint,
                };
                srcLoc.Anonymous.PlacedFootprint = footprint;

                cmd.Handle->CopyTextureRegion(ref dstLoc, 0, 0, 0, ref srcLoc, (Box*)null);

                // ── 8. Барьер CopyDest → PixelShaderResource ─────────────────

                ResourceBarrier barrier = default;
                barrier.Type  = ResourceBarrierType.Transition;
                barrier.Flags = ResourceBarrierFlags.None;
                barrier.Anonymous.Transition = new ResourceTransitionBarrier
                {
                    PResource   = texture.Handle,
                    Subresource = AllSubresources,
                    StateBefore = ResourceStates.CopyDest,
                    StateAfter  = ResourceStates.PixelShaderResource,
                };
                cmd.Handle->ResourceBarrier(1, ref barrier);

                SilkMarshal.ThrowHResult(cmd.Handle->Close());

                // ── 9. Execute ────────────────────────────────────────────────

                ID3D12CommandList* rawList = (ID3D12CommandList*)cmd.Handle;
                ctx.CommandQueue->ExecuteCommandLists(1, &rawList);

                // ── 10. Временный fence: ожидаем завершения GPU ───────────────

                void* fPtr  = null;
                Guid  fGuid = typeof(ID3D12Fence).GUID;
                SilkMarshal.ThrowHResult(
                    ctx.Device->CreateFence(0, FenceFlags.None, ref fGuid, ref fPtr));
                var fence = new ComPtr<ID3D12Fence>((ID3D12Fence*)fPtr);

                const ulong signalValue = 1ul;
                SilkMarshal.ThrowHResult(ctx.CommandQueue->Signal(fence.Handle, signalValue));

                if (fence.Handle->GetCompletedValue() < signalValue)
                {
                    using ManualResetEventSlim mre = new(false);
                    nint hEvent = mre.WaitHandle.SafeWaitHandle.DangerousGetHandle();
                    SilkMarshal.ThrowHResult(
                        fence.Handle->SetEventOnCompletion(signalValue, (void*)hEvent));
                    mre.Wait();
                }

                fence.Dispose();
                cmd.Dispose();
                alloc.Dispose();
            }
            finally
            {
                // upload буфер не нужен после синхронизации с GPU
                upload.Dispose();
            }

            // ── 11. SRV ───────────────────────────────────────────────────────
            //
            // Allocate() продвигает курсор; GetGpuHandle() берёт тот же индекс
            // без повторного продвижения курсора.

            int srvIndex = srvHeap.Count;
            CpuDescriptorHandle srvCpu = srvHeap.Allocate();
            GpuDescriptorHandle srvGpu = srvHeap.GetGpuHandle(srvIndex);

            ShaderResourceViewDesc srvDesc = new()
            {
                Format                  = Format.FormatR8G8B8A8Unorm,
                ViewDimension           = SrvDimension.Texture2D,
                Shader4ComponentMapping = DefaultShader4ComponentMapping,
            };
            srvDesc.Anonymous.Texture2D = new Tex2DSrv
            {
                MostDetailedMip     = 0,
                MipLevels           = 1,
                PlaneSlice          = 0,
                ResourceMinLODClamp = 0.0f,
            };

            ctx.Device->CreateShaderResourceView(texture.Handle, ref srvDesc, srvCpu);

            return new GpuTexture2D(texture, srvCpu, srvGpu, width, height);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _texture.Dispose();
    }
}

// Назначение:   Загрузка RGBA8-текстуры на GPU и хранение SRV-дескриптора
// Зависит от:   RenderContext, DescriptorHeap, Silk.NET.Direct3D12, Silk.NET.DXGI, Silk.NET.Core.Native
// Используется: TextureManager, Dx12Renderer (fallback белая текстура, обычные материалы)
