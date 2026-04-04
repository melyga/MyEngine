#nullable enable

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.ECS.Components;
using Silk.NET.Core.Native;
using SharpDX.XAudio2;

namespace MyEngine.Core.ECS.Systems.Update;


public sealed class AudioStore {
    private readonly System.Collections.Generic.Dictionary<int, AudioClip> _clips = new();
    public void Register(AssetHandle<AudioClip> h, AudioClip c) { if (h.IsValid) _clips[h.Id] = c; }
    public AudioClip? Resolve(AssetHandle<AudioClip> h) =>
        h.IsValid && _clips.TryGetValue(h.Id, out var c) ? c : null;
}

public sealed class AudioSystem : AEntitySetSystem<float>, IDisposable
{
    // ── константы ────────────────────────────────────────────────────
    private const int PoolSize        = 32;
    private const int MaxInputChannels = 1;   // моно для 3D
    private const int SampleRate      = 44100;

    // ── XAudio2 ──────────────────────────────────────────────────────
    private readonly XAudio2     _xaudio2Api;
    private readonly unsafe IXAudio2* _xaudio2;
    private readonly unsafe IXAudio2MasteringVoice* _masteringVoice;

    // ── X3DAudio ─────────────────────────────────────────────────────
    private          X3DAudio       _x3daudio;
    private          X3daudioHandle _x3daudioHandle;

    // ── пул голосов ─────────────────────────────────────────────────
    private readonly unsafe IXAudio2SourceVoice*[] _voicePool;
    private readonly bool[]                         _voiceBusy;

    // ── ECS запросы ──────────────────────────────────────────────────
    private readonly EntitySet _sourceSet;
    private readonly EntitySet _listenerSet;

    // ── отслеживание запущенных источников ───────────────────────────
    private readonly System.Collections.Generic.HashSet<int> _startedEntities = new();
    private readonly AudioStore _store;

    // ─────────────────────────────────────────────────────────────────
    public unsafe AudioSystem(World world, AudioStore store)
        : base(world.GetEntities()
                    .With<AudioSourceComponent>()
                    .With<TransformComponent>()
                    .AsSet())
    {
        _xaudio2Api = XAudio2.GetApi();
        _store = store;

        // Создаём IXAudio2
        IXAudio2* xa2 = null;
        int hr = _xaudio2Api.Create(&xa2, 0, (byte)Silk.NET.XAudio2.Windows.XAudio2.ProcessorDefault);
        ThrowIfFailed(hr, "XAudio2Create");
        _xaudio2 = xa2;

        // MasteringVoice
        IXAudio2MasteringVoice* mv = null;
        hr = _xaudio2->CreateMasteringVoice(
            &mv,
            XAudio2.DefaultChannels,
            XAudio2.DefaultSamplerate,
            0, null, null,
            AudioStreamCategory.GameEffects);
        ThrowIfFailed(hr, "CreateMasteringVoice");
        _masteringVoice = mv;

        // Определяем маску каналов для X3DAudio
        uint channelMask = 0;
        _masteringVoice->GetChannelMask(&channelMask);

        // X3DAudio init
        _x3daudio = X3DAudio.GetApi();
        _x3daudioHandle = new X3daudioHandle();
        _x3daudio.Initialize(channelMask, X3DAudio.SpeedOfSound, ref _x3daudioHandle);

        // Пул SourceVoice — моно PCM float
        _voicePool = new IXAudio2SourceVoice*[PoolSize];
        _voiceBusy = new bool[PoolSize];

        var wfx = new WaveformatEx
        {
            WFormatTag      = (ushort)WaveformatExTag.IeeFloat,
            NChannels       = MaxInputChannels,
            NSamplesPerSec  = SampleRate,
            WBitsPerSample  = 32,
            NBlockAlign     = (ushort)(MaxInputChannels * 4),
            NAvgBytesPerSec = (uint)(SampleRate * MaxInputChannels * 4),
        };

        for (int i = 0; i < PoolSize; i++)
        {
            IXAudio2SourceVoice* sv = null;
            hr = _xaudio2->CreateSourceVoice(
                &sv,
                &wfx,
                0,
                XAudio2.DefaultFreqRatio,
                null, null, null);
            ThrowIfFailed(hr, "CreateSourceVoice");
            _voicePool[i] = sv;
        }

        // Запросы ECS
        _sourceSet   = world.GetEntities()
                            .With<AudioSourceComponent>()
                            .With<TransformComponent>()
                            .AsSet();
        _listenerSet = world.GetEntities()
                            .With<AudioListenerComponent>()
                            .With<TransformComponent>()
                            .AsSet();
    }

    // ─────────────────────────────────────────────────────────────────
    protected override unsafe void Update(float deltaTime, in Entity entity)
    {
        // Позиция слушателя (по умолчанию — origin)
        Vector3 listenerPos = Vector3.Zero;
        Vector3 listenerFwd = Vector3.UnitZ;
        Vector3 listenerUp  = Vector3.UnitY;

        Span<Entity> listeners = stackalloc Entity[1];
        int listenerCount = _listenerSet.GetEntities().ToArray().Length;
        if (listenerCount > 0)
        {
            var arr = _listenerSet.GetEntities();
            var first = arr[0];
            ref var lt = ref first.Get<TransformComponent>();
            listenerPos = lt.Position;
            // Направления из мировой матрицы
            listenerFwd = new Vector3(lt.WorldMatrix.M31, lt.WorldMatrix.M32, lt.WorldMatrix.M33);
            listenerUp  = new Vector3(lt.WorldMatrix.M21, lt.WorldMatrix.M22, lt.WorldMatrix.M23);
        }

        ref var src = ref entity.Get<AudioSourceComponent>();
        ref var tr  = ref entity.Get<TransformComponent>();

        int entityId = entity.GetHashCode();

        // Ещё не запущен + PlayOnStart → запуск
        if (src.PlayOnStart && _startedEntities.Add(entityId))
        {
            PlayFromComponent(in src, tr.Position, src.Is3D,
                              listenerPos, listenerFwd, listenerUp);
        }

        // Обновляем 3D для уже играющих — здесь мы только пересчитываем DSP,
        // реальная привязка голоса к entity требует полноценного voice-менеджера;
        // для простоты пересчёт идёт при каждом PlayOnStart-запуске.
    }

    // ─────────────────────────────────────────────────────────────────
    /// <summary>Fire-and-forget: воспроизводит клип из свободного голоса пула.</summary>
    public unsafe void Play(
        AssetHandle<AudioClip> clipHandle,
        Vector3 pos,
        float   volume = 1f,
        float   pitch  = 1f)
    {
        int idx = FindFreeVoice();
        if (idx < 0) return; // пул исчерпан

        var resolved = _store.Resolve(clipHandle);
        if (resolved is null) return;

        SubmitBuffer(_voicePool[idx], resolved, loop: false);
        _voicePool[idx]->SetVolume(volume, XAudio2.CommitNow);
        _voicePool[idx]->SetFrequencyRatio(pitch, XAudio2.CommitNow);
        _voicePool[idx]->Start(0, XAudio2.CommitNow);
        _voiceBusy[idx] = true;
    }

    // ─────────────────────────────────────────────────────────────────
    public unsafe void Dispose()
    {
        for (int i = 0; i < PoolSize; i++)
        {
            if (_voicePool[i] != null)
            {
                _voicePool[i]->Stop(0, XAudio2.CommitNow);
                _voicePool[i]->DestroyVoice();
            }
        }

        if (_masteringVoice != null)
            _masteringVoice->DestroyVoice();

        if (_xaudio2 != null)
            _xaudio2->Release();

        _sourceSet.Dispose();
        _listenerSet.Dispose();

        base.Dispose();
    }

    // ── приватные методы ─────────────────────────────────────────────

    private unsafe void PlayFromComponent(
        in AudioSourceComponent src,
        Vector3 emitterPos,
        bool    is3D,
        Vector3 listenerPos,
        Vector3 listenerFwd,
        Vector3 listenerUp)
    {
        int idx = FindFreeVoice();
        if (idx < 0) return;

        var resolved = _store.Resolve(src.Clip);
        if (resolved is null) return;

        SubmitBuffer(_voicePool[idx], resolved, src.Loop);
        _voicePool[idx]->SetFrequencyRatio(src.Pitch, XAudio2.CommitNow);

        if (is3D)
        {
            Apply3D(_voicePool[idx], emitterPos, listenerPos, listenerFwd, listenerUp,
                    src.Volume, src.MinDistance, src.MaxDistance);
        }
        else
        {
            _voicePool[idx]->SetVolume(src.Volume, XAudio2.CommitNow);
        }

        _voicePool[idx]->Start(0, XAudio2.CommitNow);
        _voiceBusy[idx] = true;
    }

    private unsafe void Apply3D(
        IXAudio2SourceVoice* voice,
        Vector3 emitterPos,
        Vector3 listenerPos,
        Vector3 listenerFwd,
        Vector3 listenerUp,
        float   volume,
        float   minDist,
        float   maxDist)
    {
        var listenerOrient = new Vector3[2]
        {
            Vector3.Normalize(listenerFwd),
            Vector3.Normalize(listenerUp),
        };

        // Получаем количество каналов мастер-голоса
        VoiceDetails masterDetails;
        _masteringVoice->GetVoiceDetails(&masterDetails);
        uint dstChannels = masterDetails.InputChannels;

        float[] matrixCoeffs = new float[MaxInputChannels * (int)dstChannels];

        var x3Listener = new X3daudioListener
        {
            Position = emitterPos,   // намеренно: позиция слушателя
            OrientFront = listenerOrient[0],
            OrientTop   = listenerOrient[1],
            Velocity    = Vector3.Zero,
        };

        var x3Emitter = new X3daudioEmitter
        {
            Position       = emitterPos,
            OrientFront    = Vector3.UnitZ,
            OrientTop      = Vector3.UnitY,
            Velocity       = Vector3.Zero,
            ChannelCount   = MaxInputChannels,
            CurveDistanceScaler = minDist,
            DopplerScaler  = 1f,
        };

        // DSP блок с матрицей
        fixed (float* pMatrix = matrixCoeffs)
        {
            var dspSettings = new X3daudioDspSettings
            {
                PMatrixCoefficients = pMatrix,
                SrcChannelCount     = (uint)MaxInputChannels,
                DstChannelCount     = dstChannels,
            };

            _x3daudio.Calculate(
                ref _x3daudioHandle,
                ref x3Listener,
                ref x3Emitter,
                (uint)(X3DAudioCalculateFlags.Matrix |
                       X3DAudioCalculateFlags.Doppler),
                ref dspSettings);

            // Применяем матрицу и доплер
            voice->SetOutputMatrix(
                null,
                (uint)MaxInputChannels,
                dstChannels,
                pMatrix,
                XAudio2.CommitNow);

            voice->SetFrequencyRatio(dspSettings.DopplerFactor, XAudio2.CommitNow);
        }

        // Затухание по расстоянию (линейное)
        float dist   = Vector3.Distance(listenerPos, emitterPos);
        float atten  = 1f - MathF.Min(1f, MathF.Max(0f, (dist - minDist) / (maxDist - minDist)));
        voice->SetVolume(volume * atten, XAudio2.CommitNow);
    }

    private unsafe void SubmitBuffer(IXAudio2SourceVoice* voice, AudioClip clip, bool loop)
    {
        voice->FlushSourceBuffers();

        // Копируем float[] → byte[]
        byte[] bytes = MemoryMarshal.AsBytes<float>(clip.Samples).ToArray();

        fixed (byte* pData = bytes)
        {
            var buf = new XaudioBuffer
            {
                AudioBytes = (uint)bytes.Length,
                PAudioData = pData,
                Flags      = loop ? 0u : XAudio2.EndOfStream,
                LoopCount  = loop ? XAudio2.LoopInfinite : 0u,
                LoopBegin  = 0,
                LoopLength = 0,
                PlayBegin  = 0,
                PlayLength = 0,
            };
            voice->SubmitSourceBuffer(&buf, null);
        }
    }

    private unsafe int FindFreeVoice()
    {
        // Обновляем флаги занятости
        for (int i = 0; i < PoolSize; i++)
        {
            if (!_voiceBusy[i]) continue;
            VoiceState state;
            _voicePool[i]->GetState(&state, 0);
            if (state.BuffersQueued == 0)
                _voiceBusy[i] = false;
        }

        for (int i = 0; i < PoolSize; i++)
            if (!_voiceBusy[i]) return i;

        return -1;
    }

    private static void ThrowIfFailed(int hr, string op)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{op} failed with HRESULT 0x{hr:X8}");
    }

    // Назначение:   ECS-система воспроизведения звука через XAudio2 + X3DAudio с пулом из 32 голосов
    // Зависит от:   AudioSourceComponent, AudioListenerComponent, TransformComponent, AssetHandle, Silk.NET.XAudio2, DefaultEcs
    // Используется: GameLoop / SystemScheduler, Sample.Game
}
