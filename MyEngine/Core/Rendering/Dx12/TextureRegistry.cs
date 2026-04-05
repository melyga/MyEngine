#nullable enable

using System;
using System.Collections.Generic;
using MyEngine.Core.Abstractions;
using Silk.NET.Direct3D12;

namespace MyEngine.Core.Rendering.Dx12;

internal sealed class TextureRegistry : IDisposable
{
    private readonly Dictionary<int, GpuTexture2D> _textures = new();
    private int _nextId;
    private readonly GpuTexture2D _white1x1;
    private bool _disposed;

    public TextureRegistry(RenderContext ctx, DescriptorHeap srvHeap)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (srvHeap is null) throw new ArgumentNullException(nameof(srvHeap));

        _white1x1 = GpuTexture2D.CreateWhite1x1(ctx, srvHeap);
    }

    public GpuTextureHandle Register(
        RenderContext ctx,
        DescriptorHeap srvHeap,
        ReadOnlySpan<byte> rgba,
        int w,
        int h)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (srvHeap is null) throw new ArgumentNullException(nameof(srvHeap));

        GpuTexture2D texture = GpuTexture2D.FromBytes(ctx, srvHeap, rgba, w, h);

        int id = _nextId;
        checked { _nextId++; }

        _textures.Add(id, texture);
        return new GpuTextureHandle(id);
    }

    public void Unregister(GpuTextureHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_textures.Remove(handle.Id, out GpuTexture2D? texture))
            return;

        texture.Dispose();
    }

    public GpuDescriptorHandle GetSRV(GpuTextureHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!handle.IsValid)
            return _white1x1.SRVGpu;

        if (!_textures.TryGetValue(handle.Id, out GpuTexture2D? texture))
            return _white1x1.SRVGpu;

        return texture.SRVGpu;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach ((_, GpuTexture2D texture) in _textures)
            texture.Dispose();

        _textures.Clear();
        _white1x1.Dispose();
    }
}

// Назначение:   Реестр GPU-текстур DX12: регистрирует RGBA-данные, выдаёт SRV и даёт fallback на белую 1x1 текстуру.
// Зависит от:   RenderContext, DescriptorHeap, GpuTexture2D, GpuTextureHandle, Silk.NET.Direct3D12
// Используется: Dx12Renderer для LoadTexture/UnloadTexture и получения SRV по handle
