#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;

namespace MyEngine.Core.Abstractions;

public interface IModelLoader
{
    bool CanLoad(string fileExtension);
    ModelData Load(string path);
}

public record ModelData(
    MeshData[] Meshes,
    MaterialData[] Materials,
    AnimationClipData[] Animations,
    NodeData[] Hierarchy);

public record MeshData(
    Vertex[] Vertices,
    uint[] Indices,
    int MaterialIndex);

public record MaterialData(
    string Name,
    byte[]? AlbedoPixels,
    byte[]? NormalPixels,
    byte[]? MetallicRoughnessPixels,
    byte[]? EmissivePixels,
    int Width,
    int Height,
    float MetallicFactor,
    float RoughnessFactor,
    Vector4 BaseColorFactor,
    Vector3 EmissiveFactor);

public record NodeData(
    string Name,
    int MeshIndex,
    Matrix4x4 LocalTransform,
    int[] Children);

public record AnimationClipData(
    string Name,
    float DurationSeconds,
    JointTrack[] Tracks);

public record JointTrack(
    int JointIndex,
    float[] Timestamps,
    Vector3[] Positions,
    Quaternion[] Rotations,
    Vector3[] Scales);

[StructLayout(LayoutKind.Sequential)]
public struct Vector4I : IEquatable<Vector4I>
{
    public int X;
    public int Y;
    public int Z;
    public int W;

    public Vector4I(int x, int y, int z, int w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    public bool Equals(Vector4I other) =>
        X == other.X && Y == other.Y && Z == other.Z && W == other.W;

    public override bool Equals(object? obj) =>
        obj is Vector4I other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(X, Y, Z, W);

    public static bool operator ==(Vector4I left, Vector4I right) => left.Equals(right);
    public static bool operator !=(Vector4I left, Vector4I right) => !left.Equals(right);

    public override string ToString() => $"({X}, {Y}, {Z}, {W})";
}

[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoord;
    public Vector4 Tangent;
    public Vector4 BlendWeights;
    public Vector4I BlendIndices;
}

// Назначение:   Контракт загрузки 3D-моделей и все plain-data типы, передаваемые между загрузчиком и движком
// Зависит от:   System.Numerics
// Используется: MyEngine.Core.Assets, MyEngine.Core.Rendering, AssimpNet-загрузчик, SharpGLTF-загрузчик
