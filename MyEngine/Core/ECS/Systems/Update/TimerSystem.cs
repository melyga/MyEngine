#nullable enable
using System;
using System.Collections.Generic;
using DefaultEcs.System;
// fix: убран несуществующий using MyEngine.Core.ECS.Systems — ISystem<float> из DefaultEcs.System
namespace MyEngine.Core.ECS.Systems.Update;
public interface ITimer{void Cancel();void Reset();bool IsActive{get;}}
public sealed class TimerSystem : ISystem<float> {
    private readonly List<TimerEntry> _entries=new();
    public bool IsEnabled { get; set; } = true;
    private struct TimerEntry{public float Delay,Elapsed;public Action Callback;public bool Repeat,Active;}
    private sealed class TimerHandle:ITimer{
        private readonly TimerSystem _o;private readonly int _i;
        internal TimerHandle(TimerSystem o,int i){_o=o;_i=i;}
        public bool IsActive=>_i<_o._entries.Count&&_o._entries[_i].Active;
        public void Cancel(){if(_i>=_o._entries.Count)return;var e=_o._entries[_i];e.Active=false;_o._entries[_i]=e;}
        public void Reset(){if(_i>=_o._entries.Count)return;var e=_o._entries[_i];e.Elapsed=0f;e.Active=true;_o._entries[_i]=e;}}
    public void Update(float dt){
        if(!IsEnabled)return;
        for(int i=0;i<_entries.Count;i++){
            var e=_entries[i];if(!e.Active)continue;
            e.Elapsed+=dt;
            if(e.Elapsed>=e.Delay){e.Callback();if(e.Repeat)e.Elapsed=0f;else e.Active=false;}
            _entries[i]=e;}}
    public ITimer Delay(float s,Action cb){
        int i=_entries.Count;var h=new TimerHandle(this,i);
        _entries.Add(new TimerEntry{Delay=s,Callback=cb,Repeat=false,Active=true});return h;}
    public ITimer Repeat(float s,Action cb){
        int i=_entries.Count;var h=new TimerHandle(this,i);
        _entries.Add(new TimerEntry{Delay=s,Callback=cb,Repeat=true,Active=true});return h;}
    public void Dispose()=>_entries.Clear();
}
// Назначение:   Управление отложенными и повторяющимися таймерами через ISystem<float>
// Зависит от:   DefaultEcs.System.ISystem<float>
// Используется: SystemGroup, EngineContext.Timers
