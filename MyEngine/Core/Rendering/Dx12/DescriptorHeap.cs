#nullable enable

using System;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace MyEngine.Core.Rendering.Dx12;

/// <summary>
/// Обёртка над <see cref="ID3D12DescriptorHeap"/> с линейным bump-аллокатором.
/// Поддерживает CPU- и GPU-описатели; GPU-описатели доступны только
/// для shader-visible хипов (CBV/SRV/UAV, Sampler).
/// </summary>
internal sealed unsafe class DescriptorHeap : IDisposable
{
    // ── COM-объект ────────────────────────────────────────────────────────────

    private ComPtr<ID3D12DescriptorHeap> _heap;

    // ── Кешированные базовые адреса ───────────────────────────────────────────

    private readonly CpuDescriptorHandle _cpuBase;
    private readonly GpuDescriptorHandle _gpuBase;   // валиден только при shaderVisible

    // ── Шаг между дескрипторами ───────────────────────────────────────────────

    private readonly uint _stride;

    // ── Bump-аллокатор ────────────────────────────────────────────────────────

    private int _current;

    // ── Мета ─────────────────────────────────────────────────────────────────

    private readonly bool _shaderVisible;
    private bool          _disposed;

    // ── Свойства ──────────────────────────────────────────────────────────────

    public int  Capacity      { get; }
    public int  Count         => _current;
    public bool IsFull        => _current >= Capacity;

    // ── Конструктор ───────────────────────────────────────────────────────────

    /// <param name="ctx">Контекст DX12; используется только для Device.</param>
    /// <param name="type">Тип хипа (RTV, DSV, CBV_SRV_UAV, Sampler).</param>
    /// <param name="capacity">Максимальное число дескрипторов.</param>
    /// <param name="shaderVisible">
    ///     <see langword="true"/> → <see cref="DescriptorHeapFlags.ShaderVisible"/>;
    ///     разрешает <see cref="AllocateGpu"/> и <see cref="GetGpuHandle"/>.
    /// </param>
    public DescriptorHeap(
        RenderContext             ctx,
        DescriptorHeapType        type,
        int                       capacity,
        bool                      shaderVisible)
    {
        if (ctx is null)      throw new ArgumentNullException(nameof(ctx));
        if (capacity <= 0)    throw new ArgumentOutOfRangeException(nameof(capacity));

        Capacity       = capacity;
        _shaderVisible = shaderVisible;

        DescriptorHeapDesc desc = new()
        {
            NumDescriptors = (uint)capacity,
            Type           = type,
            Flags          = shaderVisible
                                 ? DescriptorHeapFlags.ShaderVisible
                                 : DescriptorHeapFlags.None,
            NodeMask       = 0,
        };

        void* ptr  = null;
        Guid  guid = typeof(ID3D12DescriptorHeap).GUID;
        SilkMarshal.ThrowHResult(
            ctx.Device->CreateDescriptorHeap(ref desc, ref guid, ref ptr));

        _heap = new ComPtr<ID3D12DescriptorHeap>((ID3D12DescriptorHeap*)ptr);

        _stride  = ctx.Device->GetDescriptorHandleIncrementSize(type);
        _cpuBase = _heap.Handle->GetCPUDescriptorHandleForHeapStart();
        _gpuBase = shaderVisible
                       ? _heap.Handle->GetGPUDescriptorHandleForHeapStart()
                       : default;
    }

    // ── Аллокация ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Возвращает следующий свободный CPU-дескриптор и продвигает курсор.
    /// </summary>
    /// <exception cref="InvalidOperationException">Хип переполнен.</exception>
    public CpuDescriptorHandle Allocate()
    {
        ThrowIfFull();
        CpuDescriptorHandle handle = GetCpuHandle(_current);
        _current++;
        return handle;
    }

    /// <summary>
    /// Возвращает следующий свободный GPU-дескриптор и продвигает курсор.
    /// Доступен только для shader-visible хипов.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Хип не является shader-visible или переполнен.
    /// </exception>
    public GpuDescriptorHandle AllocateGpu()
    {
        ThrowIfNotShaderVisible();
        ThrowIfFull();
        GpuDescriptorHandle handle = GetGpuHandle(_current);
        _current++;
        return handle;
    }

    /// <summary>
    /// Сбрасывает курсор bump-аллокатора в начало.
    /// Физически ничего не обнуляет — старые данные остаются до перезаписи.
    /// </summary>
    public void Reset() => _current = 0;

    // ── Адресная арифметика ───────────────────────────────────────────────────

    /// <summary>
    /// Возвращает CPU-дескриптор по абсолютному индексу без изменения курсора.
    /// </summary>
    /// <param name="index">Индекс в диапазоне [0, Capacity).</param>
    public CpuDescriptorHandle GetCpuHandle(int index)
    {
        ThrowIfOutOfRange(index);
        return new CpuDescriptorHandle
        {
            Ptr = _cpuBase.Ptr + (nuint)((uint)index * _stride),
        };
    }

    /// <summary>
    /// Возвращает GPU-дескриптор по абсолютному индексу без изменения курсора.
    /// Доступен только для shader-visible хипов.
    /// </summary>
    /// <param name="index">Индекс в диапазоне [0, Capacity).</param>
    public GpuDescriptorHandle GetGpuHandle(int index)
    {
        ThrowIfNotShaderVisible();
        ThrowIfOutOfRange(index);
        return new GpuDescriptorHandle
        {
            Ptr = _gpuBase.Ptr + (ulong)((uint)index * _stride),
        };
    }

    // ── Guard-методы ─────────────────────────────────────────────────────────

    private void ThrowIfFull()
    {
        if (IsFull)
            throw new InvalidOperationException(
                $"DescriptorHeap is full (capacity = {Capacity}).");
    }

    private void ThrowIfNotShaderVisible()
    {
        if (!_shaderVisible)
            throw new InvalidOperationException(
                "GPU descriptor handles are only available for shader-visible heaps.");
    }

    private void ThrowIfOutOfRange(int index)
    {
        if ((uint)index >= (uint)Capacity)
            throw new ArgumentOutOfRangeException(
                nameof(index),
                $"Index {index} is out of range [0, {Capacity}).");
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heap.Dispose();
    }
}

// Назначение:   Линейный bump-аллокатор CPU/GPU дескрипторов над ID3D12DescriptorHeap
// Зависит от:   RenderContext, Silk.NET.Direct3D12, Silk.NET.Core.Native
// Используется: Dx12Renderer, TextureManager, любой код, выделяющий DX12-дескрипторы
