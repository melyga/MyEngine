#nullable enable

using System;
using System.Collections.Generic;
using System.Numerics;
using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.ECS.Components;

namespace MyEngine.Core.ECS.Systems.Update;

public sealed class AudioStore
{
    private readonly Dictionary<int, AudioClip> _clips = new();

    public void Register(AssetHandle<AudioClip> handle, AudioClip clip)
    {
        if (handle.IsValid)
            _clips[handle.Id] = clip;
    }

    public AudioClip? Resolve(AssetHandle<AudioClip> handle) =>
        handle.IsValid && _clips.TryGetValue(handle.Id, out AudioClip? clip) ? clip : null;
}

public sealed class AudioSystem : ISystem<float>, IDisposable
{
    private readonly AudioStore _store;
    private readonly HashSet<int> _startedEntities = new();

    public bool IsEnabled { get; set; } = true;

    public AudioSystem()
        : this(new AudioStore())
    {
    }

    public AudioSystem(AudioStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public AudioSystem(World world, AudioStore store)
        : this(store)
    {
        _ = world;
    }

    public void Update(float deltaTime)
    {
        _ = deltaTime;
        if (!IsEnabled)
            return;
    }

    public void Play(
        AssetHandle<AudioClip> clipHandle,
        Vector3 pos,
        float volume = 1f,
        float pitch = 1f)
    {
        _ = pos;
        _ = volume;
        _ = pitch;
        _ = _store.Resolve(clipHandle);
    }

    public void MarkStarted(Entity entity) => _startedEntities.Add(entity.GetHashCode());

    public bool HasStarted(Entity entity) => _startedEntities.Contains(entity.GetHashCode());

    public void Dispose() => _startedEntities.Clear();
}
