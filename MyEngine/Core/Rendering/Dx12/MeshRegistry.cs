#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using MyEngine.Core.Abstractions;
using Silk.NET.Direct3D12;

namespace MyEngine.Core.Rendering.Dx12;

internal sealed class MeshRegistry : IDisposable
{
    private readonly RenderContext _ctx;
    private readonly Dictionary<int, MeshEntry> _meshes = new();
    private readonly Dictionary<int, GpuBuffer<Matrix4x4>> _boneBuffers = new();
    private int _nextId;
    private bool _disposed;

    private readonly struct MeshEntry
    {
        public readonly GpuBuffer<Vertex> VB;
        public readonly GpuBuffer<uint> IB;
        public readonly int IndexCount;

        public MeshEntry(GpuBuffer<Vertex> vb, GpuBuffer<uint> ib, int indexCount)
        {
            VB = vb;
            IB = ib;
            IndexCount = indexCount;
        }
    }

    public MeshRegistry(RenderContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
    }

    public GpuMeshHandle Register(
        RenderContext ctx,
        ReadOnlySpan<Vertex> verts,
        ReadOnlySpan<uint> indices)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (verts.IsEmpty) throw new ArgumentException("Vertex data must not be empty.", nameof(verts));
        if (indices.IsEmpty) throw new ArgumentException("Index data must not be empty.", nameof(indices));

        GpuBuffer<Vertex>? vb = null;
        GpuBuffer<uint>? ib = null;

        try
        {
            vb = GpuBuffer<Vertex>.CreateVertex(ctx, verts);
            ib = GpuBuffer<uint>.CreateIndex(ctx, indices);

            int id = _nextId;
            checked { _nextId++; }

            _meshes.Add(id, new MeshEntry(vb, ib, indices.Length));
            return new GpuMeshHandle(id);
        }
        catch
        {
            vb?.Dispose();
            ib?.Dispose();
            throw;
        }
    }

    public void Unregister(GpuMeshHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_meshes.Remove(handle.Id, out MeshEntry entry))
            return;

        entry.VB.Dispose();
        entry.IB.Dispose();

        if (_boneBuffers.Remove(handle.Id, out GpuBuffer<Matrix4x4>? bones))
            bones.Dispose();
    }

    public (VertexBufferView vbv, IndexBufferView ibv, int count) GetBuffers(GpuMeshHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_meshes.TryGetValue(handle.Id, out MeshEntry entry))
            throw new KeyNotFoundException($"Mesh handle is not registered: {handle}.");

        return (entry.VB.VertexView, entry.IB.IndexView, entry.IndexCount);
    }

    public void SetBoneMatrices(GpuMeshHandle handle, ReadOnlySpan<Matrix4x4> bones)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_meshes.ContainsKey(handle.Id))
            throw new KeyNotFoundException($"Mesh handle is not registered: {handle}.");

        if (bones.IsEmpty)
            return;

        if (_boneBuffers.TryGetValue(handle.Id, out GpuBuffer<Matrix4x4>? existing))
        {
            if (existing.ElementCount < bones.Length)
            {
                existing.Dispose();

                GpuBuffer<Matrix4x4> resized = GpuBuffer<Matrix4x4>.CreateStructured(_ctx, bones.Length);
                resized.Update(bones);
                _boneBuffers[handle.Id] = resized;
                return;
            }

            existing.Update(bones);
            return;
        }

        GpuBuffer<Matrix4x4> buffer = GpuBuffer<Matrix4x4>.CreateStructured(_ctx, bones.Length);
        buffer.Update(bones);
        _boneBuffers.Add(handle.Id, buffer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach ((_, MeshEntry entry) in _meshes)
        {
            entry.VB.Dispose();
            entry.IB.Dispose();
        }

        _meshes.Clear();

        foreach ((_, GpuBuffer<Matrix4x4> boneBuffer) in _boneBuffers)
            boneBuffer.Dispose();

        _boneBuffers.Clear();
    }
}

// Назначение:   Реестр GPU-мешей DX12: создаёт/хранит VB+IB, обновляет bone-матрицы и освобождает ресурсы.
// Зависит от:   RenderContext, GpuBuffer<T>, GpuMeshHandle, Vertex, Matrix4x4, Silk.NET.Direct3D12
// Используется: Dx12Renderer для LoadMesh/UnloadMesh/GetBuffers/SetBoneMatrices
