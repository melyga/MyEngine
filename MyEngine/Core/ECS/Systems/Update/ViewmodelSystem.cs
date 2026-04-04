#nullable enable
using System;
using System.Numerics;
using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.ECS.Components;
namespace MyEngine.Core.ECS.Systems.Update;
public sealed class ViewmodelSystem : AEntitySetSystem<float> {
    private readonly CameraSystem _camera;
    private readonly EntitySet    _playerSet;
    private float _walkTime;
    private const float BobFreq=8f,BobAmp=0.008f,BobDecay=10f;
    public ViewmodelSystem(World world,CameraSystem camera)
        :base(world.GetEntities().With<ViewmodelTag>().With<TransformComponent>().AsSet()){
        _camera=camera;
        // fix: CharacterComponent вместо несуществующего MovementComponent
        _playerSet=world.GetEntities().With<PlayerTag>().With<CharacterComponent>().AsSet();}
    protected override void Update(float dt,in Entity entity){
        // fix: CharacterComponent.MoveDirection вместо MovementComponent.MoveDirection
        Vector3 moveDir=Vector3.Zero;
        var players=_playerSet.GetEntities();
        if(players.Length>0)moveDir=players[0].Get<CharacterComponent>().MoveDirection;
        Vector3 pos=_camera.Position+_camera.Right*.18f+_camera.Up*-.12f+_camera.Forward*.35f;
        if(moveDir.LengthSquared()>0.01f){_walkTime+=dt;pos.Y+=MathF.Sin(_walkTime*BobFreq)*BobAmp;}
        else{_walkTime=Lerp(_walkTime,0f,dt*BobDecay);pos.Y+=MathF.Sin(_walkTime*BobFreq)*BobAmp;}
        Quaternion rot=BuildRot();
        ref TransformComponent tr=ref entity.Get<TransformComponent>();
        Vector3 scale=tr.Scale==Vector3.Zero?Vector3.One:tr.Scale;
        tr.Position=pos;tr.Rotation=rot;tr.Scale=scale;
        tr.WorldMatrix=Matrix4x4.CreateScale(scale)*Matrix4x4.CreateFromQuaternion(rot)*Matrix4x4.CreateTranslation(pos);}
    public override void Dispose(){_playerSet.Dispose();base.Dispose();}
    private Quaternion BuildRot(){
        Vector3 fwd=_camera.Forward,up=_camera.Up;
        if(MathF.Abs(Vector3.Dot(Vector3.Normalize(fwd),Vector3.Normalize(up)))>0.999f)up=Vector3.UnitX;
        return Matrix4x4.Invert(Matrix4x4.CreateLookAt(Vector3.Zero,fwd,up),out Matrix4x4 inv)
            ?Quaternion.CreateFromRotationMatrix(inv):Quaternion.Identity;}
    private static float Lerp(float a,float b,float t)=>a+(b-a)*Math.Clamp(t,0f,1f);
}
// Назначение:   Позиционирует вьюмодель относительно камеры с bob-эффектом при движении
// Зависит от:   DefaultEcs, CameraSystem, CharacterComponent, TransformComponent, ViewmodelTag
// Используется: SystemGroup Update-группа (после CameraSystem)
