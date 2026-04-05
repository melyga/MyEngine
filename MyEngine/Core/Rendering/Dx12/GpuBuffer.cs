#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Dx12Range = Silk.NET.Direct3D12.Range;

namespace MyEngine.Core.Rendering.Dx12;

/// <summary>
/// Типизированный буфер GPU. Управляет ресурсом DX12, upload-выгрузкой
/// и постоянным маппингом для Constant/Structured-буферов.
/// </summary>
internal sealed unsafe class GpuBuffer<T> : IDisposable where T : unmanaged
{
    // ── Внутренний вид буфера ─────────────────────────────────────────────────

    private enum BufferKind { Vertex, Index, Constant, Structured }

    private readonly struct PersistentMappedAllocation
    {
        public PersistentMappedAllocation(ComPtr<ID3D12Resource> resource, void* mappedPtr)
        {
            Resource = resource;
            MappedPtr = mappedPtr;
        }

        public ComPtr<ID3D12Resource> Resource { get; }
        public void* MappedPtr { get; }
    }

    // ── Поля ─────────────────────────────────────────────────────────────────

    private ComPtr<ID3D12Resource> _resource;
    private void*                  _mappedPtr; // ненулевой только для Constant/Structured
    private readonly BufferKind    _kind;
    private bool                   _disposed;

    private const uint AllSubresources = 0xFFFF_FFFFu; // D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES

    // ── Свойства ─────────────────────────────────────────────────────────────

    /// <summary>Дескриптор вершинного буфера. Только для CreateVertex.</summary>
    public VertexBufferView VertexView   { get; }

    /// <summary>Дескриптор индексного буфера. Только для CreateIndex.</summary>
    public IndexBufferView  IndexView    { get; }

    /// <summary>GPU-адрес ресурса.</summary>
    public ulong GpuAddress   { get; }

    /// <summary>Размер буфера в байтах (с учётом выравнивания для Constant).</summary>
    public int   SizeInBytes  { get; }

    /// <summary>Количество элементов типа T, с которым был создан буфер.</summary>
    public int   ElementCount { get; }

    // ── Приватный конструктор ─────────────────────────────────────────────────

    private GpuBuffer(
        ComPtr<ID3D12Resource> resource,
        void*            mappedPtr,
        BufferKind       kind,
        int              sizeInBytes,
        int              elementCount,
        VertexBufferView vertexView,
        IndexBufferView  indexView)
    {
        _resource    = resource;
        _mappedPtr   = mappedPtr;
        _kind        = kind;
        SizeInBytes  = sizeInBytes;
        ElementCount = elementCount;
        GpuAddress   = resource.Handle->GetGPUVirtualAddress();
        VertexView   = vertexView;
        IndexView    = indexView;
    }

    // ── Статические фабрики ───────────────────────────────────────────────────

    /// <summary>
    /// Создаёт вершинный буфер на DEFAULT heap.
    /// Данные копируются через upload heap + CopyBufferRegion.
    /// </summary>
    public static GpuBuffer<T> CreateVertex(RenderContext ctx, ReadOnlySpan<T> data)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Vertex data must not be empty.", nameof(data));

        int stride      = sizeof(T);
        int sizeInBytes = stride * data.Length;

        ComPtr<ID3D12Resource> res = UploadToDefaultHeap(
            ctx, data, sizeInBytes, ResourceStates.VertexAndConstantBuffer);

        var vbv = new VertexBufferView
        {
            BufferLocation = res.Handle->GetGPUVirtualAddress(),
            SizeInBytes    = (uint)sizeInBytes,
            StrideInBytes  = (uint)stride,
        };

        return new GpuBuffer<T>(
            res, null, BufferKind.Vertex,
            sizeInBytes, data.Length,
            vbv, default);
    }

    /// <summary>
    /// Создаёт индексный буфер на DEFAULT heap.
    /// T должен быть ushort (R16_UINT) или uint (R32_UINT).
    /// </summary>
    public static GpuBuffer<T> CreateIndex(RenderContext ctx, ReadOnlySpan<T> data)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Index data must not be empty.", nameof(data));

        int elementSize = sizeof(T);
        if (elementSize is not 2 and not 4)
            throw new ArgumentException(
                $"Index element must be ushort (2 bytes) or uint (4 bytes); " +
                $"sizeof({typeof(T).Name}) = {elementSize}.");

        int sizeInBytes = elementSize * data.Length;

        ComPtr<ID3D12Resource> res = UploadToDefaultHeap(
            ctx, data, sizeInBytes, ResourceStates.IndexBuffer);

        var ibv = new IndexBufferView
        {
            BufferLocation = res.Handle->GetGPUVirtualAddress(),
            SizeInBytes    = (uint)sizeInBytes,
            Format         = elementSize == 2
                ? Format.FormatR16Uint
                : Format.FormatR32Uint,
        };

        return new GpuBuffer<T>(
            res, null, BufferKind.Index,
            sizeInBytes, data.Length,
            default, ibv);
    }

    /// <summary>
    /// Создаёт постоянно-маппированный константный буфер на UPLOAD heap.
    /// Размер автоматически выравнивается до 256 байт (требование DX12 CBV).
    /// Обновляется через Update().
    /// </summary>
    public static GpuBuffer<T> CreateConstant(RenderContext ctx, int elementCount)
    {
        if (elementCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(elementCount), "Must be > 0.");

        int rawBytes    = sizeof(T) * elementCount;
        int alignedSize = AlignTo256(rawBytes);

        PersistentMappedAllocation allocation = AllocatePersistentMapped(ctx, alignedSize);

        return new GpuBuffer<T>(
            allocation.Resource, allocation.MappedPtr, BufferKind.Constant,
            alignedSize, elementCount,
            default, default);
    }

    /// <summary>
    /// Создаёт постоянно-маппированный structured-буфер на UPLOAD heap.
    /// Обновляется через Update().
    /// </summary>
    public static GpuBuffer<T> CreateStructured(RenderContext ctx, int elementCount)
    {
        if (elementCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(elementCount), "Must be > 0.");

        int sizeInBytes = sizeof(T) * elementCount;

        PersistentMappedAllocation allocation = AllocatePersistentMapped(ctx, sizeInBytes);

        return new GpuBuffer<T>(
            allocation.Resource, allocation.MappedPtr, BufferKind.Structured,
            sizeInBytes, elementCount,
            default, default);
    }

    // ── Публичные методы ──────────────────────────────────────────────────────

    /// <summary>
    /// Синхронно копирует данные в постоянно маппированный буфер.
    /// Допустимо только для Constant и Structured буферов.
    /// </summary>
    public void Update(ReadOnlySpan<T> data)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GpuBuffer<T>));

        if (_kind is not BufferKind.Constant and not BufferKind.Structured)
            throw new InvalidOperationException(
                $"Update() is valid only for Constant and Structured buffers; " +
                $"this buffer is {_kind}.");

        if (data.Length > ElementCount)
            throw new ArgumentOutOfRangeException(
                nameof(data),
                $"Span length {data.Length} exceeds ElementCount {ElementCount}.");

        uint byteCount = (uint)(sizeof(T) * data.Length);

        fixed (T* src = data)
            Unsafe.CopyBlockUnaligned(_mappedPtr, src, byteCount);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_mappedPtr != null)
        {
            _resource.Handle->Unmap(0, (Dx12Range*)null);
            _mappedPtr = null;
        }

        _resource.Dispose();
    }

    // ── Приватные вспомогательные методы ─────────────────────────────────────

    /// <summary>
    /// Создаёт ресурс на DEFAULT heap, загружая данные через временный upload buffer
    /// с последующим CopyBufferRegion и переводом ресурса в <paramref name="finalState"/>.
    /// Вызов блокирует CPU до завершения GPU-копии.
    /// </summary>
    private static ComPtr<ID3D12Resource> UploadToDefaultHeap(
        RenderContext  ctx,
        ReadOnlySpan<T> data,
        int            sizeInBytes,
        ResourceStates finalState)
    {
        // ── 1. Upload heap: выделить и заполнить ─────────────────────────────
        ComPtr<ID3D12Resource> upload = CreateCommittedBuffer(
            ctx, sizeInBytes,
            HeapType.Upload, ResourceStates.GenericRead);

        void* uploadPtr;
        SilkMarshal.ThrowHResult(
            upload.Handle->Map(0, (Dx12Range*)null, &uploadPtr));

        fixed (T* src = data)
            Unsafe.CopyBlockUnaligned(uploadPtr, src, (uint)sizeInBytes);

        upload.Handle->Unmap(0, (Dx12Range*)null);

        // ── 2. Default heap: целевой буфер ───────────────────────────────────
        ComPtr<ID3D12Resource> resource = CreateCommittedBuffer(
            ctx, sizeInBytes,
            HeapType.Default, ResourceStates.CopyDest);

        // ── 3. Команды: Copy + Barrier ───────────────────────────────────────
        (ComPtr<ID3D12CommandAllocator> cmdAlloc,
         ComPtr<ID3D12GraphicsCommandList> cmdList) = OpenOneTimeCommandList(ctx);

        cmdList.Handle->CopyBufferRegion(
            resource.Handle, 0,
            upload.Handle,   0,
            (ulong)sizeInBytes);

        ResourceBarrier barrier = MakeTransitionBarrier(
            resource.Handle,
            ResourceStates.CopyDest,
            finalState);

        cmdList.Handle->ResourceBarrier(1, ref barrier);
        SilkMarshal.ThrowHResult(cmdList.Handle->Close());

        // ── 4. Отправка и синхронизация ──────────────────────────────────────
        ID3D12CommandList* rawList = (ID3D12CommandList*)cmdList.Handle;
        ctx.CommandQueue->ExecuteCommandLists(1, &rawList);
        WaitForQueueIdle(ctx);

        cmdList.Dispose();
        cmdAlloc.Dispose();
        upload.Dispose();

        return resource;
    }

    /// <summary>
    /// Создаёт ресурс на UPLOAD heap и сразу оставляет его постоянно маппированным.
    /// Подходит для данных, обновляемых каждый кадр (CBV, SRV structured).
    /// </summary>
    private static PersistentMappedAllocation AllocatePersistentMapped(RenderContext ctx, int sizeInBytes)
    {
        ComPtr<ID3D12Resource> resource = CreateCommittedBuffer(
            ctx, sizeInBytes,
            HeapType.Upload, ResourceStates.GenericRead);

        void* mapped;
        SilkMarshal.ThrowHResult(
            resource.Handle->Map(0, (Dx12Range*)null, &mapped));

        // Persistent map: Unmap вызывается только в Dispose()
        return new PersistentMappedAllocation(resource, mapped);
    }

    /// <summary>
    /// Создаёт committed resource типа Buffer на указанном heap.
    /// </summary>
    private static ComPtr<ID3D12Resource> CreateCommittedBuffer(
        RenderContext  ctx,
        int            sizeInBytes,
        HeapType       heapType,
        ResourceStates initialState)
    {
        var heapProps = new HeapProperties
        {
            Type                 = heapType,
            CPUPageProperty      = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown,
            CreationNodeMask     = 0,
            VisibleNodeMask      = 0,
        };

        var desc = new ResourceDesc
        {
            Dimension        = ResourceDimension.Buffer,
            Alignment        = 0,
            Width            = (ulong)sizeInBytes,
            Height           = 1,
            DepthOrArraySize = 1,
            MipLevels        = 1,
            Format           = Format.FormatUnknown,
            SampleDesc       = new SampleDesc { Count = 1, Quality = 0 },
            Layout           = TextureLayout.LayoutRowMajor,
            Flags            = ResourceFlags.None,
        };

        void* resPtr  = null;
        Guid  resGuid = typeof(ID3D12Resource).GUID;

        SilkMarshal.ThrowHResult(
            ctx.Device->CreateCommittedResource(
                ref heapProps,
                HeapFlags.None,
                ref desc,
                initialState,
                (ClearValue*)null,
                ref resGuid,
                ref resPtr));

        return new ComPtr<ID3D12Resource>((ID3D12Resource*)resPtr);
    }

    /// <summary>
    /// Открывает временный командный аллокатор и список для одноразовой операции.
    /// Список уже открыт (не нужно Reset перед первым вызовом).
    /// </summary>
    private static (ComPtr<ID3D12CommandAllocator>, ComPtr<ID3D12GraphicsCommandList>)
        OpenOneTimeCommandList(RenderContext ctx)
    {
        void* allocPtr  = null;
        Guid  allocGuid = typeof(ID3D12CommandAllocator).GUID;
        SilkMarshal.ThrowHResult(
            ctx.Device->CreateCommandAllocator(
                CommandListType.Direct, ref allocGuid, ref allocPtr));

        var cmdAlloc = new ComPtr<ID3D12CommandAllocator>((ID3D12CommandAllocator*)allocPtr);

        void* listPtr  = null;
        Guid  listGuid = typeof(ID3D12GraphicsCommandList).GUID;
        SilkMarshal.ThrowHResult(
            ctx.Device->CreateCommandList(
                nodeMask:          0,
                type:              CommandListType.Direct,
                pCommandAllocator: cmdAlloc.Handle,
                pInitialState:     null,
                riid:              ref listGuid,
                ppCommandList:     ref listPtr));

        var cmdList = new ComPtr<ID3D12GraphicsCommandList>((ID3D12GraphicsCommandList*)listPtr);

        return (cmdAlloc, cmdList);
    }

    /// <summary>
    /// Блокирует вызывающий поток до тех пор, пока GPU не завершит все ранее
    /// отправленные в CommandQueue команды. Вызывается только из фабрик (не в game loop).
    /// </summary>
    private static void WaitForQueueIdle(RenderContext ctx)
    {
        void* fencePtr  = null;
        Guid  fenceGuid = typeof(ID3D12Fence).GUID;
        SilkMarshal.ThrowHResult(
            ctx.Device->CreateFence(0, FenceFlags.None, ref fenceGuid, ref fencePtr));

        var fence = new ComPtr<ID3D12Fence>((ID3D12Fence*)fencePtr);

        const ulong signalValue = 1UL;
        SilkMarshal.ThrowHResult(ctx.CommandQueue->Signal(fence.Handle, signalValue));

        using var waitEvent = new ManualResetEventSlim(initialState: false);
        nint hEvent = waitEvent.WaitHandle.SafeWaitHandle.DangerousGetHandle();
        SilkMarshal.ThrowHResult(
            fence.Handle->SetEventOnCompletion(signalValue, (void*)hEvent));

        waitEvent.Wait();
        fence.Dispose();
    }

    /// <summary>
    /// Создаёт ResourceBarrier для перехода состояния ресурса.
    /// </summary>
    private static ResourceBarrier MakeTransitionBarrier(
        ID3D12Resource* resource,
        ResourceStates  before,
        ResourceStates  after)
    {
        ResourceBarrier barrier = default;
        barrier.Type  = ResourceBarrierType.Transition;
        barrier.Flags = ResourceBarrierFlags.None;
        barrier.Anonymous.Transition = new ResourceTransitionBarrier
        {
            PResource   = resource,
            Subresource = AllSubresources,
            StateBefore = before,
            StateAfter  = after,
        };
        return barrier;
    }

    /// <summary>
    /// Выравнивает размер в байтах до кратного 256 — требование DX12 для CBV.
    /// </summary>
    private static int AlignTo256(int size) => (size + 255) & ~255;
}

// Назначение:   Типизированный GPU-буфер (vertex/index/constant/structured) с загрузкой upload heap → CopyBufferRegion → default heap
// Зависит от:   RenderContext, Silk.NET.Direct3D12, Silk.NET.DXGI, Silk.NET.Core.Native
// Используется: MeshRenderer, ConstantBufferPool, RenderPipeline
