#nullable enable

#if DEBUG

using System;
using System.Collections.Generic;
using System.Numerics;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.Engine;    // fix: DebugSystem живёт в Engine namespace

namespace MyEngine.Core.ECS.Systems.Render;

// ── Color helper ─────────────────────────────────────────────────────────────
public readonly struct Color
{
    public float R { get; init; }
    public float G { get; init; }
    public float B { get; init; }
    public float A { get; init; }

    public Color(float r, float g, float b, float a = 1f) { R=r; G=g; B=b; A=a; }

    public static readonly Color White  = new(1f, 1f, 1f);
    public static readonly Color Red    = new(1f, 0f, 0f);
    public static readonly Color Green  = new(0f, 1f, 0f);
    public static readonly Color Blue   = new(0f, 0f, 1f);
    public static readonly Color Yellow = new(1f, 1f, 0f);
    public static readonly Color Cyan   = new(0f, 1f, 1f);

    public Vector4 ToVector4() => new(R, G, B, A);
}

// ── Pending shapes ────────────────────────────────────────────────────────────
file readonly struct PendingLine   { public Vector3 From, To; public Color Color; }
file readonly struct PendingSphere { public Vector3 Center; public float Radius; public Color Color; }
file readonly struct PendingBox    { public Vector3 Center, Half; public Color Color; }

// ── DebugDraw ─────────────────────────────────────────────────────────────────
public static class DebugDraw
{
    private static readonly List<PendingLine>   _lines   = new();
    private static readonly List<PendingSphere> _spheres = new();
    private static readonly List<PendingBox>    _boxes   = new();

    public static IReadOnlyList<PendingLine>   Lines   => _lines;
    public static IReadOnlyList<PendingSphere> Spheres => _spheres;
    public static IReadOnlyList<PendingBox>    Boxes   => _boxes;

    public static void Line(Vector3 from, Vector3 to, Color color)
        => _lines.Add(new PendingLine { From=from, To=to, Color=color });
    public static void Sphere(Vector3 center, float radius, Color color)
        => _spheres.Add(new PendingSphere { Center=center, Radius=radius, Color=color });
    public static void Box(Vector3 center, Vector3 half, Color color)
        => _boxes.Add(new PendingBox { Center=center, Half=half, Color=color });
    public static void Ray(Vector3 origin, Vector3 dir, Color color)
        => Line(origin, origin + dir, color);

    internal static void Flush()
    {
        _lines.Clear(); _spheres.Clear(); _boxes.Clear();
    }
}

// ── DebugRenderSystem ─────────────────────────────────────────────────────────
public sealed class DebugRenderSystem : ISystem<float>
{
    private readonly IRenderer   _renderer;
    private readonly DebugSystem _debug;

    public bool IsEnabled { get; set; } = true;

    public DebugRenderSystem(IRenderer renderer, DebugSystem debug)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _debug    = debug    ?? throw new ArgumentNullException(nameof(debug));
    }

    public void Update(float deltaTime)
    {
        if (!IsEnabled) { DebugDraw.Flush(); return; }

        // fix: Vertex не имеет поля Color — используем BaseColor в DrawCall
        foreach (PendingLine line in DebugDraw.Lines)
        {
            // fix: два вертекса без Color — только Position и Normal
            Vertex[] verts =
            [
                new Vertex { Position = line.From, Normal = Vector3.UnitY },
                new Vertex { Position = line.To,   Normal = Vector3.UnitY },
            ];
            uint[] idx = [0, 1];

            GpuMeshHandle mesh = _renderer.LoadMesh(verts, idx);
            _renderer.Submit(new DrawCall
            {
                Mesh      = mesh,
                WorldMatrix = Matrix4x4.Identity,
                BaseColor = line.Color.ToVector4(),  // цвет через BaseColor, не Vertex.Color
                Metallic  = 0f,
                Roughness = 1f,
            });
            _renderer.UnloadMesh(mesh);
        }

        foreach (PendingSphere sphere in DebugDraw.Spheres)
        {
            Matrix4x4 world = Matrix4x4.CreateScale(sphere.Radius)
                            * Matrix4x4.CreateTranslation(sphere.Center);
            Vertex[] verts = BuildUnitSphereVerts();
            uint[]   idx   = BuildUnitSphereIndices();
            GpuMeshHandle mesh = _renderer.LoadMesh(verts, idx);
            _renderer.Submit(new DrawCall
            {
                Mesh=mesh, WorldMatrix=world,
                BaseColor=sphere.Color.ToVector4(), Metallic=0f, Roughness=1f,
            });
            _renderer.UnloadMesh(mesh);
        }

        foreach (PendingBox box in DebugDraw.Boxes)
        {
            Matrix4x4 world = Matrix4x4.CreateScale(box.Half * 2f)
                            * Matrix4x4.CreateTranslation(box.Center);
            Vertex[] verts = BuildUnitBoxVerts();
            uint[]   idx   = BuildUnitBoxIndices();
            GpuMeshHandle mesh = _renderer.LoadMesh(verts, idx);
            _renderer.Submit(new DrawCall
            {
                Mesh=mesh, WorldMatrix=world,
                BaseColor=box.Color.ToVector4(), Metallic=0f, Roughness=1f,
            });
            _renderer.UnloadMesh(mesh);
        }

        DebugDraw.Flush();
    }

    // fix: вертексы без Color — только Position/Normal
    private static Vertex[] BuildUnitSphereVerts()
    {
        const int Segments = 16, Rings = 3;
        Vertex[] verts = new Vertex[Segments * Rings];
        int idx = 0;
        for (int ring = 0; ring < Rings; ring++)
        {
            for (int i = 0; i < Segments; i++)
            {
                float angle = i * 2f * MathF.PI / Segments;
                Vector3 pos = ring switch
                {
                    0 => new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)),
                    1 => new Vector3(0f, MathF.Cos(angle), MathF.Sin(angle)),
                    _ => new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f),
                };
                verts[idx++] = new Vertex { Position = pos, Normal = pos };
            }
        }
        return verts;
    }

    private static uint[] BuildUnitSphereIndices()
    {
        const int Segments = 16, Rings = 3;
        uint[] idx = new uint[Segments * Rings * 2];
        int ptr = 0;
        for (int ring = 0; ring < Rings; ring++)
        {
            uint offset = (uint)(ring * Segments);
            for (int i = 0; i < Segments; i++)
            {
                idx[ptr++] = offset + (uint)i;
                idx[ptr++] = offset + (uint)((i + 1) % Segments);
            }
        }
        return idx;
    }

    private static Vertex[] BuildUnitBoxVerts()
    {
        float h = 0.5f;
        return
        [
            new Vertex { Position = new Vector3(-h,-h,-h) },
            new Vertex { Position = new Vector3( h,-h,-h) },
            new Vertex { Position = new Vector3( h, h,-h) },
            new Vertex { Position = new Vector3(-h, h,-h) },
            new Vertex { Position = new Vector3(-h,-h, h) },
            new Vertex { Position = new Vector3( h,-h, h) },
            new Vertex { Position = new Vector3( h, h, h) },
            new Vertex { Position = new Vector3(-h, h, h) },
        ];
    }

    private static uint[] BuildUnitBoxIndices() =>
    [
        0,1, 1,2, 2,3, 3,0,
        4,5, 5,6, 6,7, 7,4,
        0,4, 1,5, 2,6, 3,7,
    ];

    public void Dispose() { }
}

#endif

// Назначение:   Рендерит debug-гизмо (линии, сферы, боксы) и сбрасывает DebugDraw каждый кадр
// Зависит от:   IRenderer, DebugSystem, Vertex, GpuMeshHandle, DrawCall
// Используется: SystemGroup (Render-группа, только #if DEBUG)
