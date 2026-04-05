#nullable enable

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using MyEngine.Core.Abstractions;
using Silk.NET.Direct3D12;

namespace MyEngine.Core.Rendering.Dx12.Passes;

internal sealed unsafe class LightManager : IDisposable
{
    private const int LightsCapacity = 273; // 256 + 16 + 1

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuLightData
    {
        public Vector3 Position;
        public float _p0;
        public Vector3 Direction;
        public float _p1;
        public Vector3 Color;
        public float Intensity;
        public float Radius;
        public float InnerAngle;
        public float OuterAngle;
        public int Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuShadowData
    {
        public uint Enabled;
        public Vector3 _padding;
    }

    private readonly GpuBuffer<GpuLightData> _lightsBuffer;
    private readonly GpuBuffer<GpuShadowData> _shadowBuffer;
    private bool _disposed;

    public LightManager(RenderContext ctx)
    {
        _lightsBuffer = GpuBuffer<GpuLightData>.CreateStructured(ctx, LightsCapacity);
        _shadowBuffer = GpuBuffer<GpuShadowData>.CreateStructured(ctx, LightsCapacity);
    }

    public void Update(ReadOnlySpan<LightData> lights)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (lights.Length > LightsCapacity)
            throw new ArgumentOutOfRangeException(nameof(lights), lights.Length, $"Light count exceeds capacity {LightsCapacity}.");

        GpuLightData[] gpuLights = new GpuLightData[lights.Length];
        for (int i = 0; i < lights.Length; i++)
        {
            LightData src = lights[i];
            gpuLights[i] = new GpuLightData
            {
                Position = src.Position,
                _p0 = 0f,
                Direction = src.Direction,
                _p1 = 0f,
                Color = src.Color,
                Intensity = src.Intensity,
                Radius = src.Radius,
                InnerAngle = src.InnerAngle,
                OuterAngle = src.OuterAngle,
                Type = (int)src.Type,
            };
        }

        _lightsBuffer.Update(gpuLights);
    }

    public void Bind(ID3D12GraphicsCommandList* cmd, int rootParamIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cmd->SetGraphicsRootShaderResourceView((uint)rootParamIndex, _lightsBuffer.GpuAddress);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _shadowBuffer.Dispose();
        _lightsBuffer.Dispose();
    }
}

// Назначение:   Управляет structured-буферами света/теней для DX12 и привязкой буфера источников света к root-параметру.
// Зависит от:   RenderContext, GpuBuffer, IRenderer.LightData, Silk.NET.Direct3D12
// Используется: DX12 lighting pass (подготовка/биндинг данных света перед шейдингом)
