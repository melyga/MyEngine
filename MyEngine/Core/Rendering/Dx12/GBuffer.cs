#nullable enable

using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace MyEngine.Core.Rendering.Dx12;

internal sealed unsafe class GBuffer : IDisposable
{
    private const int RenderTargetCount = 4;
    private const uint AllSubresources = 0xFFFF_FFFFu;

    private readonly RenderContext _ctx;
    private readonly DescriptorHeap _rtvHeap;
    private readonly DescriptorHeap _dsvHeap;
    private readonly DescriptorHeap _srvHeap;

    private readonly int[] _srvIndices = new int[RenderTargetCount];

    private readonly ComPtr<ID3D12Resource>[] _rtResources = new ComPtr<ID3D12Resource>[RenderTargetCount];
    private ComPtr<ID3D12Resource> _depthResource;

    private bool _disposed;

    public CpuDescriptorHandle[] RTVHandles { get; }
    public CpuDescriptorHandle DSVHandle { get; }
    public GpuDescriptorHandle[] SRVHandles { get; }
    public ID3D12Resource* DepthResource => _depthResource.Handle;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public GBuffer(
        RenderContext ctx,
        DescriptorHeap rtvHeap,
        DescriptorHeap dsvHeap,
        DescriptorHeap srvHeap,
        int width,
        int height)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (rtvHeap is null) throw new ArgumentNullException(nameof(rtvHeap));
        if (dsvHeap is null) throw new ArgumentNullException(nameof(dsvHeap));
        if (srvHeap is null) throw new ArgumentNullException(nameof(srvHeap));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        _ctx = ctx;
        _rtvHeap = rtvHeap;
        _dsvHeap = dsvHeap;
        _srvHeap = srvHeap;

        RTVHandles = new CpuDescriptorHandle[RenderTargetCount];
        SRVHandles = new GpuDescriptorHandle[RenderTargetCount];

        for (int i = 0; i < RenderTargetCount; i++)
        {
            RTVHandles[i] = _rtvHeap.Allocate();

            _srvIndices[i] = _srvHeap.Count;
            _srvHeap.Allocate();
            SRVHandles[i] = _srvHeap.GetGpuHandle(_srvIndices[i]);
        }

        DSVHandle = _dsvHeap.Allocate();

        Width = width;
        Height = height;

        CreateResourcesAndViews();
    }

    public void BeginGeometryPass(ID3D12GraphicsCommandList* cmd)
    {
        if (cmd is null)
            throw new ArgumentNullException(nameof(cmd));

        ResourceBarrier* barriers = stackalloc ResourceBarrier[RenderTargetCount];
        for (int i = 0; i < RenderTargetCount; i++)
        {
            barriers[i] = MakeTransitionBarrier(
                _rtResources[i].Handle,
                ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource,
                ResourceStates.RenderTarget);
        }

        cmd->ResourceBarrier((uint)RenderTargetCount, barriers);

        fixed (CpuDescriptorHandle* rtvHandles = RTVHandles)
        {
            CpuDescriptorHandle dsvHandle = DSVHandle;
            cmd->OMSetRenderTargets((uint)RenderTargetCount, rtvHandles, false, &dsvHandle);
        }

        float* clearColor = stackalloc float[4] { 0f, 0f, 0f, 0f };
        for (int i = 0; i < RenderTargetCount; i++)
        {
            cmd->ClearRenderTargetView(RTVHandles[i], clearColor, 0, null);
        }

        cmd->ClearDepthStencilView(
            DSVHandle,
            ClearFlags.Depth,
            1.0f,
            0,
            0,
            null);
    }

    public void EndGeometryPass(ID3D12GraphicsCommandList* cmd)
    {
        if (cmd is null)
            throw new ArgumentNullException(nameof(cmd));

        ResourceBarrier* barriers = stackalloc ResourceBarrier[RenderTargetCount];
        for (int i = 0; i < RenderTargetCount; i++)
        {
            barriers[i] = MakeTransitionBarrier(
                _rtResources[i].Handle,
                ResourceStates.RenderTarget,
                ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
        }

        cmd->ResourceBarrier((uint)RenderTargetCount, barriers);
    }

    public void Resize(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (width == Width && height == Height) return;

        Width = width;
        Height = height;

        ReleaseResources();
        CreateResourcesAndViews();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseResources();
    }

    private void CreateResourcesAndViews()
    {
        _rtResources[0] = CreateRenderTarget(Format.FormatR8G8B8A8Unorm);
        _rtResources[1] = CreateRenderTarget(Format.FormatR16G16B16A16Float);
        _rtResources[2] = CreateRenderTarget(Format.FormatR8G8B8A8Unorm);
        _rtResources[3] = CreateRenderTarget(Format.FormatR16G16B16A16Float);

        _depthResource = CreateDepth(Format.FormatD32Float);

        for (int i = 0; i < RenderTargetCount; i++)
        {
            _ctx.Device->CreateRenderTargetView(_rtResources[i].Handle, null, RTVHandles[i]);

            CpuDescriptorHandle srvCpu = _srvHeap.GetCpuHandle(_srvIndices[i]);
            _ctx.Device->CreateShaderResourceView(_rtResources[i].Handle, null, srvCpu);
            SRVHandles[i] = _srvHeap.GetGpuHandle(_srvIndices[i]);
        }

        _ctx.Device->CreateDepthStencilView(_depthResource.Handle, null, DSVHandle);
    }

    private ComPtr<ID3D12Resource> CreateRenderTarget(Format format)
    {
        HeapProperties heapProps = new() { Type = HeapType.Default };
        ResourceDesc desc = new()
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (ulong)Width,
            Height = (uint)Height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = format,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowRenderTarget,
        };

        void* ptr = null;
        Guid guid = typeof(ID3D12Resource).GUID;

        SilkMarshal.ThrowHResult(
            _ctx.Device->CreateCommittedResource(
                ref heapProps,
                HeapFlags.None,
                ref desc,
                ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource,
                (ClearValue*)null,
                ref guid,
                ref ptr));

        return new ComPtr<ID3D12Resource>((ID3D12Resource*)ptr);
    }

    private ComPtr<ID3D12Resource> CreateDepth(Format format)
    {
        HeapProperties heapProps = new() { Type = HeapType.Default };
        ResourceDesc desc = new()
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (ulong)Width,
            Height = (uint)Height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = format,
            SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowDepthStencil,
        };

        void* ptr = null;
        Guid guid = typeof(ID3D12Resource).GUID;

        SilkMarshal.ThrowHResult(
            _ctx.Device->CreateCommittedResource(
                ref heapProps,
                HeapFlags.None,
                ref desc,
                ResourceStates.DepthWrite,
                (ClearValue*)null,
                ref guid,
                ref ptr));

        return new ComPtr<ID3D12Resource>((ID3D12Resource*)ptr);
    }

    private void ReleaseResources()
    {
        for (int i = 0; i < RenderTargetCount; i++)
        {
            _rtResources[i].Dispose();
            _rtResources[i] = default;
        }

        _depthResource.Dispose();
        _depthResource = default;
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
}

// Назначение:   Хранит G-buffer (4 RT + depth), переключает состояния ресурсов и готовит geometry pass в DX12.
// Зависит от:   RenderContext, DescriptorHeap, Silk.NET.Direct3D12, Silk.NET.DXGI, Silk.NET.Core.Native
// Используется: Dx12Renderer и системы deferred-рендеринга для geometry/light pass
