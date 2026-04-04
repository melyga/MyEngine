#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.ECS.Components;
namespace MyEngine.Core.ECS.Systems.Update;
public sealed class AnimationStore {
    private readonly Dictionary<int,AnimationClip> _clips=new();
    public void Register(AssetHandle<AnimationClip> h,AnimationClip c){if(h.IsValid)_clips[h.Id]=c;}
    public bool TryGet(AssetHandle<AnimationClip> h,out AnimationClip? c){
        if(h.IsValid&&_clips.TryGetValue(h.Id,out var f)){c=f;return true;}c=null;return false;}
}
public sealed class AnimationSystem : AEntitySetSystem<float> {
    private readonly IRenderer _renderer;
    private readonly AnimationStore _store;
    public AnimationSystem(World w,IRenderer r,AnimationStore s):base(w.GetEntities().With<AnimationComponent>().With<MeshComponent>().AsSet()){_renderer=r;_store=s;}
    protected override void Update(float dt,in Entity e){
        ref var anim=ref e.Get<AnimationComponent>();
        ref var mesh=ref e.Get<MeshComponent>();
        if(!anim.Playing)return;
        if(!_store.TryGet(anim.CurrentClip,out AnimationClip? clip)||clip is null)return;
        anim.Time+=dt*anim.Speed;
        if(anim.Time>clip.Duration){if(anim.Loop)anim.Time%=clip.Duration;else{anim.Time=clip.Duration;anim.Playing=false;}}
        var bones=SampleClip(clip,anim.Time);
        if(anim.BlendWeight>0f&&anim.BlendClip!=AssetHandle<AnimationClip>.Invalid&&_store.TryGet(anim.BlendClip,out AnimationClip? bc)&&bc is not null){
            var bb=SampleClip(bc,Math.Min(anim.Time,bc.Duration));
            int cnt=Math.Min(bones.Length,bb.Length);
            for(int i=0;i<cnt;i++)bones[i]=LerpMat(bones[i],bb[i],anim.BlendWeight);}
        _renderer.SetBoneMatrices(mesh.Mesh,bones);}
    private static Matrix4x4[] SampleClip(AnimationClip clip,float time){
        int n=clip.Tracks.Length;var r=new Matrix4x4[n];
        for(int t=0;t<n;t++){var tr=clip.Tracks[t];
            if(tr.Timestamps.Length==0){r[t]=Matrix4x4.Identity;continue;}
            if(tr.Timestamps.Length==1){r[t]=Bld(tr.Positions.Length>0?tr.Positions[0]:Vector3.Zero,tr.Rotations.Length>0?tr.Rotations[0]:Quaternion.Identity,tr.Scales.Length>0?tr.Scales[0]:Vector3.One);continue;}
            int lo=Lb(tr.Timestamps,time),hi=Math.Min(lo+1,tr.Timestamps.Length-1);
            float t0=tr.Timestamps[lo],t1=tr.Timestamps[hi],a=(t1>t0)?Math.Clamp((time-t0)/(t1-t0),0f,1f):0f;
            int idx=tr.JointIndex<n?tr.JointIndex:t;
            r[idx]=Bld(Vector3.Lerp(tr.Positions.Length>lo?tr.Positions[lo]:Vector3.Zero,tr.Positions.Length>hi?tr.Positions[hi]:Vector3.Zero,a),
                Quaternion.Slerp(tr.Rotations.Length>lo?tr.Rotations[lo]:Quaternion.Identity,tr.Rotations.Length>hi?tr.Rotations[hi]:Quaternion.Identity,a),
                Vector3.Lerp(tr.Scales.Length>lo?tr.Scales[lo]:Vector3.One,tr.Scales.Length>hi?tr.Scales[hi]:Vector3.One,a));}
        return r;}
    private static int Lb(float[] ts,float t){int lo=0,hi=ts.Length-1;if(t<=ts[lo])return 0;if(t>=ts[hi])return hi-1;while(lo<hi-1){int m=(lo+hi)>>1;if(ts[m]<=t)lo=m;else hi=m;}return lo;}
    private static Matrix4x4 Bld(Vector3 p,Quaternion r,Vector3 s)=>Matrix4x4.CreateScale(s)*Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(r))*Matrix4x4.CreateTranslation(p);
    private static Matrix4x4 LerpMat(Matrix4x4 a,Matrix4x4 b,float w){
        if(!Matrix4x4.Decompose(a,out var sA,out var rA,out var tA)){sA=Vector3.One;rA=Quaternion.Identity;tA=Vector3.Zero;}
        if(!Matrix4x4.Decompose(b,out var sB,out var rB,out var tB)){sB=Vector3.One;rB=Quaternion.Identity;tB=Vector3.Zero;}
        return Bld(Vector3.Lerp(tA,tB,w),Quaternion.Slerp(rA,rB,w),Vector3.Lerp(sA,sB,w));}
}
// Назначение:   Система анимации скелета: сэмплирует клипы, интерполирует кости, отправляет матрицы в IRenderer
// Зависит от:   AnimationComponent, MeshComponent, IRenderer, AnimationStore, DefaultEcs
// Используется: SystemGroup Update-группа
