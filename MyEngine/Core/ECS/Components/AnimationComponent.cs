#nullable enable

using System.Numerics;
using MyEngine.Core.Abstractions;

namespace MyEngine.Core.ECS.Components;

// fix: public вместо internal — Sample.Game должен видеть тип для AssetHandle<AnimationClip>
public sealed class AnimationClip
{
    public string       Name     { get; init; } = "";
    public float        Duration { get; init; }
    public JointTrack[] Tracks   { get; init; } = [];
}

// fix: public вместо internal — нужен публично для GltfModelLoader и AnimationSystem
public sealed class JointTrack
{
    public int          JointIndex { get; init; }
    public float[]      Timestamps { get; init; } = [];
    public Vector3[]    Positions  { get; init; } = [];
    public Quaternion[] Rotations  { get; init; } = [];
    public Vector3[]    Scales     { get; init; } = [];
}

public struct AnimationComponent
{
    public AssetHandle<AnimationClip> CurrentClip;
    public AssetHandle<AnimationClip> BlendClip;
    public float Speed;
    public float Time;
    public float BlendWeight;
    public bool  Loop;
    public bool  Playing;

    public static AnimationComponent Default => new()
    {
        CurrentClip = AssetHandle<AnimationClip>.Invalid,
        BlendClip   = AssetHandle<AnimationClip>.Invalid,
        Speed       = 1.0f,
        Time        = 0f,
        BlendWeight = 0f,
        Loop        = false,
        Playing     = false,
    };
}

// Назначение:   Компонент анимации скелета: текущий и blending клип, время, скорость, вес перехода
// Зависит от:   AssetHandle, System.Numerics
// Используется: AnimationSystem, AssetManager, GltfModelLoader
