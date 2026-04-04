#nullable enable
using System;
using System.Numerics;
using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.Abstractions;
using MyEngine.Core.ECS.Components;
// fix: убран несуществующий using MyEngine.Core.ECS.Events
// JumpRequestedEvent объявлен в InputSystem.cs (тот же namespace Update)
namespace MyEngine.Core.ECS.Systems.Update;
public sealed class CharacterControllerSystem : AEntitySetSystem<float> {
    private readonly IPhysicsBackend _physics;
    private readonly IDisposable     _jumpSub;
    private bool _jumpRequested;
    public CharacterControllerSystem(World world,IPhysicsBackend physics)
        :base(world.GetEntities().With<PlayerTag>().With<CharacterComponent>().With<RigidBodyComponent>().With<TransformComponent>().AsSet()){
        _physics=physics;
        // fix: подписка через DefaultEcs World.Subscribe — единственная шина событий
        _jumpSub=world.Subscribe<JumpRequestedEvent>(OnJump);}
    private void OnJump(in JumpRequestedEvent _)=>_jumpRequested=true;
    protected override void Update(float dt,in Entity entity){
        ref CharacterComponent ch=ref entity.Get<CharacterComponent>();
        ref readonly RigidBodyComponent rb=ref entity.Get<RigidBodyComponent>();
        ref TransformComponent tr=ref entity.Get<TransformComponent>();
        PhysicsBodyHandle body=rb.Body;
        Vector3 pos=_physics.GetPosition(body);
        Vector3 vel=_physics.GetLinearVelocity(body);
        Vector3 horiz=ch.MoveDirection*ch.MoveSpeed;
        _physics.SetLinearVelocity(body,new Vector3(horiz.X,vel.Y,horiz.Z));
        if(_jumpRequested&&ch.IsGrounded){
            _physics.ApplyImpulse(body,Vector3.UnitY*ch.JumpImpulse);
            _jumpRequested=false;}
        if(_physics.Raycast(pos,-Vector3.UnitY,1f,out RaycastHit slope)){
            float angle=MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(slope.Normal),Vector3.UnitY),-1f,1f))*(180f/MathF.PI);
            if(angle>ch.SlopeLimit){var v=_physics.GetLinearVelocity(body);_physics.SetLinearVelocity(body,new Vector3(0f,v.Y,0f));}}
        Vector3 mxz=new Vector3(ch.MoveDirection.X,0f,ch.MoveDirection.Z);
        if(mxz.LengthSquared()>1e-6f){
            Vector3 f=Vector3.Normalize(mxz);
            if(_physics.Raycast(pos,f,.5f,out _)&&!_physics.Raycast(pos+f*.3f+Vector3.UnitY*ch.StepHeight,-Vector3.UnitY,ch.StepHeight,out _))
                _physics.SetPosition(body,pos+Vector3.UnitY*ch.StepHeight);}
        ch.IsGrounded=_physics.Raycast(pos,-Vector3.UnitY,.15f,out _);
        tr.Position=_physics.GetPosition(body);
        tr.Rotation=_physics.GetRotation(body);}
    public override void Dispose(){_jumpSub.Dispose();base.Dispose();}
}
// Назначение:   ECS-система движения персонажа — горизонталь, прыжок, slope-limit, step-up, IsGrounded
// Зависит от:   IPhysicsBackend, CharacterComponent, RigidBodyComponent, TransformComponent, JumpRequestedEvent
// Используется: SystemGroup Update-группа
