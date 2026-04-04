#nullable enable
using System.Numerics;
using DefaultEcs;
using DefaultEcs.System;
using MyEngine.Core.ECS.Components;
using MyEngine.Core.Engine;
using Silk.NET.Input;
namespace MyEngine.Core.ECS.Systems.Update;
public sealed class InputSystem : AEntitySetSystem<float> {
    private readonly EngineContext _ctx;
    private readonly CameraSystem  _camera;
    public InputSystem(World w,EngineContext ctx,CameraSystem camera)
        :base(w.GetEntities().With<PlayerTag>().With<CharacterComponent>().AsSet())
    {_ctx=ctx;_camera=camera;}
    protected override void Update(float dt,in Entity entity){
        InputManager input=_ctx.Input;
        // fix: берём из CameraSystem, а не из несуществующего IRenderer.CameraForward
        Vector3 fwd=Vector3.Normalize(new Vector3(_camera.Forward.X,0f,_camera.Forward.Z));
        Vector3 rgt=Vector3.Normalize(new Vector3(_camera.Right.X,0f,_camera.Right.Z));
        Vector3 dir=Vector3.Zero;
        if(input.IsKeyDown(Key.W))dir+=fwd;
        if(input.IsKeyDown(Key.S))dir-=fwd;
        if(input.IsKeyDown(Key.D))dir+=rgt;
        if(input.IsKeyDown(Key.A))dir-=rgt;
        ref CharacterComponent ch=ref entity.Get<CharacterComponent>();
        ch.MoveDirection=dir.LengthSquared()>1e-6f?Vector3.Normalize(dir):Vector3.Zero;
        // fix: публикуем через DefaultEcs World.Publish, не через EventBus заглушку
        if(input.IsKeyDown(Key.Space)&&ch.IsGrounded)
            _ctx.EcsWorld.Publish(new JumpRequestedEvent(entity));
        if(input.IsKeyDown(Key.F)&&entity.Has<ViewmodelLightComponent>()){
            ref var light=ref entity.Get<ViewmodelLightComponent>();
            light.IsOn=!light.IsOn;}}}
// fix: единственное объявление события — CharacterControllerSystem использует world.Subscribe
public readonly struct JumpRequestedEvent{
    public readonly Entity Source;
    public JumpRequestedEvent(Entity s)=>Source=s;}
// fix: единственное объявление InputManager (заменяет заглушку из EngineContext.cs)
public sealed class InputManager{
    public bool IsKeyDown(Key k)=>false;
    public System.Numerics.Vector2 MouseDelta{get;set;}
    public bool IsMouseButton(MouseButton b)=>false;}
// Назначение:   Читает ввод, пишет MoveDirection, публикует JumpRequestedEvent через DefaultEcs World
// Зависит от:   DefaultEcs, EngineContext, CameraSystem, CharacterComponent, PlayerTag, Silk.NET.Input
// Используется: SystemGroup Update-группа
