#nullable enable

using MyEngine.Core.Abstractions;

namespace MyEngine.Core.ECS.Components;

// fix: public вместо internal — Sample.Game должен видеть тип для AssetHandle<AudioClip>
public sealed class AudioClip
{
    public float[] Samples    { get; init; } = [];
    public int     SampleRate { get; init; }
    public int     Channels   { get; init; }
    public float   Duration   => Samples.Length / (float)(SampleRate * Channels);
}

public struct AudioSourceComponent
{
    public AssetHandle<AudioClip> Clip;
    public float Volume;
    public float Pitch;
    public float MinDistance;
    public float MaxDistance;
    public bool  Loop;
    public bool  PlayOnStart;
    public bool  Is3D;

    public static AudioSourceComponent Default => new()
    {
        Clip        = AssetHandle<AudioClip>.Invalid,
        Volume      = 1.0f,
        Pitch       = 1.0f,
        MinDistance = 1.0f,
        MaxDistance = 50.0f,
        Loop        = false,
        PlayOnStart = false,
        Is3D        = false,
    };
}

public struct AudioListenerComponent { }

// Назначение:   Компоненты ECS для воспроизведения звука (источник и слушатель)
// Зависит от:   AssetHandle, MyEngine.Core.Abstractions
// Используется: AudioSystem (XAudio2 + X3DAudio), Sample.Game
