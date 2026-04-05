#nullable enable

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using System.Numerics;

namespace MyEngine.Core.Rendering.Dx12;

/// <summary>
/// Владеет всей низкоуровневой DX12-инфраструктурой:
/// устройством, очередью команд, swap chain и синхронизацией кадров.
/// </summary>
internal sealed unsafe class RenderContext : IDisposable
{
    // ── Константы ────────────────────────────────────────────────────────────

    private const uint FrameCount                = 2;
    private const uint DxgiUsageRenderTargetOut  = 0x00000020u;   // DXGI_USAGE_RENDER_TARGET_OUTPUT
    private const uint AllSubresources           = 0xFFFFFFFFu;   // D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES

    // ── API ──────────────────────────────────────────────────────────────────

    private readonly DXGI  _dxgiApi;
    private readonly D3D12 _d3d12Api;

    // ── Окно ─────────────────────────────────────────────────────────────────

    private readonly IWindow _window;
    private readonly bool    _vsync;

    // ── COM-объекты ──────────────────────────────────────────────────────────

    private ComPtr<IDXGIFactory7>             _factory;
    private ComPtr<ID3D12Device>              _device;
    private ComPtr<ID3D12CommandQueue>        _commandQueue;
    private ComPtr<IDXGISwapChain3>           _swapChain;
    private ComPtr<ID3D12DescriptorHeap>      _rtvHeap;
    private ComPtr<ID3D12GraphicsCommandList> _commandList;
    private ComPtr<ID3D12Fence>               _fence;

    private readonly ComPtr<ID3D12Resource>[]         _backBuffers = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly ComPtr<ID3D12CommandAllocator>[] _allocators  = new ComPtr<ID3D12CommandAllocator>[FrameCount];

    // ── Состояние кадра ──────────────────────────────────────────────────────

    private uint  _rtvDescriptorSize;
    private ulong _fenceValue;
    private int   _frameIndex;

    private readonly ManualResetEventSlim _fenceEvent = new(initialState: false);
    private bool _disposed;

    // ── Свойства ─────────────────────────────────────────────────────────────

    public ID3D12Device*              Device       => _device.Handle;
    public ID3D12CommandQueue*        CommandQueue => _commandQueue.Handle;
    public ID3D12GraphicsCommandList* CommandList  => _commandList.Handle;
    public int                        FrameIndex   => _frameIndex;
    public int                        Width        { get; private set; }
    public int                        Height       { get; private set; }

    // ── Конструктор ──────────────────────────────────────────────────────────

    public RenderContext(string title, int width, int height, bool vsync)
    {
        Width    = width;
        Height   = height;
        _vsync   = vsync;
        _dxgiApi = DXGI.GetApi();
        _d3d12Api = D3D12.GetApi();

        _window = Window.Create(WindowOptions.Default with
        {
            Title                   = title,
            Size                    = new Vector2D<int>(width, height),
            API                     = GraphicsAPI.None,
            ShouldSwapAutomatically = false,
            VSync                   = false,         // синхронизируем через Present
        });
        _window.Initialize();

        InitFactory();
        InitDevice();
        InitCommandQueue();
        InitSwapChain();
        InitRtvHeap();
        InitBackBuffers();
        InitCommandAllocators();
        InitCommandList();
        InitFence();
    }

    // ── Публичный API ────────────────────────────────────────────────────────

    /// <summary>
    /// Сбрасывает аллокатор текущего кадра, открывает CommandList,
    /// переводит back-buffer из PRESENT в RENDER_TARGET.
    /// </summary>
    public void BeginFrame()
    {
        ID3D12CommandAllocator* allocator = _allocators[_frameIndex].Handle;
        SilkMarshal.ThrowHResult(allocator->Reset());
        SilkMarshal.ThrowHResult(_commandList.Handle->Reset(allocator, null));

        ResourceBarrier barrier = MakeTransitionBarrier(
            _backBuffers[_frameIndex].Handle,
            ResourceStates.Present,
            ResourceStates.RenderTarget);

        _commandList.Handle->ResourceBarrier(1, ref barrier);
    }

    /// <summary>
    /// Переводит back-buffer в PRESENT, закрывает и исполняет CommandList,
    /// вызывает Present, выставляет fence и ждёт GPU.
    /// </summary>
    public void EndFrame()
    {
        ResourceBarrier barrier = MakeTransitionBarrier(
            _backBuffers[_frameIndex].Handle,
            ResourceStates.RenderTarget,
            ResourceStates.Present);

        _commandList.Handle->ResourceBarrier(1, ref barrier);
        SilkMarshal.ThrowHResult(_commandList.Handle->Close());

        ID3D12CommandList* list = (ID3D12CommandList*)_commandList.Handle;
        _commandQueue.Handle->ExecuteCommandLists(1, &list);

        uint syncInterval = _vsync ? 1u : 0u;
        SilkMarshal.ThrowHResult(_swapChain.Handle->Present(syncInterval, 0));

        ++_fenceValue;
        SilkMarshal.ThrowHResult(_commandQueue.Handle->Signal(_fence.Handle, _fenceValue));
        WaitForGpu();

        _frameIndex ^= 1;
    }

    /// <summary>
    /// Очищает RTV текущего back-buffer указанным цветом.
    /// Вызывать после BeginFrame().
    /// </summary>
    public void ClearBackBuffer(Vector4 color)
    {
        CpuDescriptorHandle rtvStart = _rtvHeap.Handle->GetCPUDescriptorHandleForHeapStart();
        CpuDescriptorHandle rtv = new()
        {
            Ptr = rtvStart.Ptr + (nuint)((uint)_frameIndex * _rtvDescriptorSize),
        };

        float* c = stackalloc float[4] { color.X, color.Y, color.Z, color.W };
        _commandList.Handle->ClearRenderTargetView(rtv, c, 0, null);
    }

    /// <summary>
    /// Блокирует CPU до тех пор, пока GPU не обработает текущее значение fence.
    /// </summary>
    public void WaitForGpu()
    {
        if (_fence.Handle->GetCompletedValue() < _fenceValue)
        {
            _fenceEvent.Reset();
            nint hEvent = _fenceEvent.WaitHandle.SafeWaitHandle.DangerousGetHandle();
            SilkMarshal.ThrowHResult(_fence.Handle->SetEventOnCompletion(_fenceValue, (void*)hEvent));
            _fenceEvent.Wait();
        }
    }

    /// <summary>
    /// Ждёт GPU, освобождает back-buffer ресурсы, изменяет размер swap chain,
    /// пересоздаёт RTV-ы.
    /// </summary>
    public void Resize(int w, int h)
    {
        WaitForGpu();

        for (int i = 0; i < FrameCount; i++)
        {
            _backBuffers[i].Dispose();
            _backBuffers[i] = default;
        }

        Width  = w;
        Height = h;

        SilkMarshal.ThrowHResult(
            _swapChain.Handle->ResizeBuffers(
                FrameCount,
                (uint)w,
                (uint)h,
                Format.FormatR8G8B8A8Unorm,
                0));

        InitBackBuffers();
        _frameIndex = (int)_swapChain.Handle->GetCurrentBackBufferIndex();
    }

    // ── Инициализация ────────────────────────────────────────────────────────

    private void InitFactory()
    {
        void* ptr  = null;
        Guid  guid = typeof(IDXGIFactory7).GUID;
        SilkMarshal.ThrowHResult(_dxgiApi.CreateDXGIFactory2(0, ref guid, ref ptr));
        _factory = new ComPtr<IDXGIFactory7>((IDXGIFactory7*)ptr);
    }

    private void InitDevice()
    {
        IDXGIAdapter1* adapter  = null;
        IDXGIAdapter1* selected = null;

        for (uint i = 0; _factory.Handle->EnumAdapters1(i, ref adapter) == 0; i++)
        {
            AdapterDesc1 desc = default;
            adapter->GetDesc1(ref desc);

            if ((desc.Flags & (uint)AdapterFlag.Software) == 0)
            {
                selected = adapter;
                break;
            }

            adapter->Release();
            adapter = null;
        }

        if (selected == null)
            throw new InvalidOperationException("Direct3D 12 hardware adapter not found.");

        void* devPtr  = null;
        Guid  devGuid = typeof(ID3D12Device).GUID;
        SilkMarshal.ThrowHResult(
            _d3d12Api.CreateDevice(
                (IUnknown*)selected,
                D3DFeatureLevel.Level110,
                ref devGuid,
                ref devPtr));

        _device = new ComPtr<ID3D12Device>((ID3D12Device*)devPtr);
        selected->Release();
    }

    private void InitCommandQueue()
    {
        CommandQueueDesc desc = new()
        {
            Type     = CommandListType.Direct,
            Flags    = CommandQueueFlags.None,
            Priority = 0,
            NodeMask = 0,
        };

        void* ptr  = null;
        Guid  guid = typeof(ID3D12CommandQueue).GUID;
        SilkMarshal.ThrowHResult(_device.Handle->CreateCommandQueue(ref desc, ref guid, ref ptr));
        _commandQueue = new ComPtr<ID3D12CommandQueue>((ID3D12CommandQueue*)ptr);
    }

    private void InitSwapChain()
    {
        nint hwnd = _window.Native!.Win32!.Value.Hwnd;

        SwapChainDesc1 scDesc = new()
        {
            Width       = (uint)Width,
            Height      = (uint)Height,
            Format      = Format.FormatR8G8B8A8Unorm,
            Stereo      = 0,
            SampleDesc  = new SampleDesc { Count = 1, Quality = 0 },
            BufferUsage = DxgiUsageRenderTargetOut,
            BufferCount = FrameCount,
            Scaling     = Scaling.Stretch,
            SwapEffect  = SwapEffect.FlipDiscard,
            AlphaMode   = AlphaMode.Unspecified,
            Flags       = 0,
        };

        IDXGISwapChain1* sc1 = null;
        SilkMarshal.ThrowHResult(
            _factory.Handle->CreateSwapChainForHwnd(
                (IUnknown*)_commandQueue.Handle,
                hwnd,
                ref scDesc,
                (SwapChainFullscreenDesc*)null,
                (IDXGIOutput*)null,
                ref sc1));

        // Запрещаем Alt+Enter — управление полноэкранным режимом на стороне движка
        SilkMarshal.ThrowHResult(
            _factory.Handle->MakeWindowAssociation(hwnd, 1u /* DXGI_MWA_NO_ALT_ENTER */));

        void* sc3Ptr  = null;
        Guid  sc3Guid = typeof(IDXGISwapChain3).GUID;
        SilkMarshal.ThrowHResult(sc1->QueryInterface(ref sc3Guid, ref sc3Ptr));
        _swapChain = new ComPtr<IDXGISwapChain3>((IDXGISwapChain3*)sc3Ptr);
        sc1->Release();

        _frameIndex = (int)_swapChain.Handle->GetCurrentBackBufferIndex();
    }

    private void InitRtvHeap()
    {
        DescriptorHeapDesc desc = new()
        {
            NumDescriptors = FrameCount,
            Type           = DescriptorHeapType.Rtv,
            Flags          = DescriptorHeapFlags.None,
            NodeMask       = 0,
        };

        void* ptr  = null;
        Guid  guid = typeof(ID3D12DescriptorHeap).GUID;
        SilkMarshal.ThrowHResult(_device.Handle->CreateDescriptorHeap(ref desc, ref guid, ref ptr));
        _rtvHeap = new ComPtr<ID3D12DescriptorHeap>((ID3D12DescriptorHeap*)ptr);

        _rtvDescriptorSize = _device.Handle->GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
    }

    private void InitBackBuffers()
    {
        CpuDescriptorHandle rtvStart = _rtvHeap.Handle->GetCPUDescriptorHandleForHeapStart();

        for (uint i = 0; i < FrameCount; i++)
        {
            void* bbPtr  = null;
            Guid  bbGuid = typeof(ID3D12Resource).GUID;
            SilkMarshal.ThrowHResult(_swapChain.Handle->GetBuffer(i, ref bbGuid, ref bbPtr));
            _backBuffers[i] = new ComPtr<ID3D12Resource>((ID3D12Resource*)bbPtr);

            CpuDescriptorHandle handle = new() { Ptr = rtvStart.Ptr + i * _rtvDescriptorSize };
            _device.Handle->CreateRenderTargetView(_backBuffers[i].Handle, null, handle);
        }
    }

    private void InitCommandAllocators()
    {
        for (uint i = 0; i < FrameCount; i++)
        {
            void* ptr  = null;
            Guid  guid = typeof(ID3D12CommandAllocator).GUID;
            SilkMarshal.ThrowHResult(
                _device.Handle->CreateCommandAllocator(CommandListType.Direct, ref guid, ref ptr));
            _allocators[i] = new ComPtr<ID3D12CommandAllocator>((ID3D12CommandAllocator*)ptr);
        }
    }

    private void InitCommandList()
    {
        void* ptr  = null;
        Guid  guid = typeof(ID3D12GraphicsCommandList).GUID;
        SilkMarshal.ThrowHResult(
            _device.Handle->CreateCommandList(
                nodeMask: 0,
                type:     CommandListType.Direct,
                pCommandAllocator: _allocators[0].Handle,
                pInitialState:     null,
                riid:              ref guid,
                ppCommandList:     ref ptr));

        _commandList = new ComPtr<ID3D12GraphicsCommandList>((ID3D12GraphicsCommandList*)ptr);
        // Spec: закрыт сразу после создания
        SilkMarshal.ThrowHResult(_commandList.Handle->Close());
    }

    private void InitFence()
    {
        void* ptr  = null;
        Guid  guid = typeof(ID3D12Fence).GUID;
        SilkMarshal.ThrowHResult(
            _device.Handle->CreateFence(0, FenceFlags.None, ref guid, ref ptr));
        _fence      = new ComPtr<ID3D12Fence>((ID3D12Fence*)ptr);
        _fenceValue = 0;
    }

    // ── Вспомогательные методы ────────────────────────────────────────────────

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

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        WaitForGpu();

        _fenceEvent.Dispose();
        _fence.Dispose();
        _commandList.Dispose();

        for (int i = 0; i < FrameCount; i++)
        {
            _allocators[i].Dispose();
            _backBuffers[i].Dispose();
        }

        _rtvHeap.Dispose();
        _swapChain.Dispose();
        _commandQueue.Dispose();
        _device.Dispose();
        _factory.Dispose();

        _d3d12Api.Dispose();
        _dxgiApi.Dispose();

        _window.Dispose();
    }
}

// Назначение:   Вся низкоуровневая DX12-инфраструктура: device, swap chain, command list, fence
// Зависит от:   Silk.NET.Direct3D12, Silk.NET.DXGI, Silk.NET.Windowing, Silk.NET.Core.Native
// Используется: Dx12Renderer (единственный потребитель внутри Core)
