# Patterns & Examples

Complete, idiomatic s&box code. Each example is runnable — no placeholder fragments. Use these as the structural template for your own work.

These examples assume you've read the topical references (`core-concepts.md`, `components-builtin.md`, `networking.md`, `input-and-physics.md`, `ui-razor.md`). They are intentionally verbose so you can copy and adapt.

---

## Conventions Used Here

- Components are `sealed` unless inheritance is needed.
- `[Property]` exposes state to the inspector and to prefab serialization.
- `OnFixedUpdate` for movement and physics. `OnUpdate` for camera, input polling, visuals.
- `Scene.Get<T>()` / `Components.Get<T>()` for scene queries — not `FindObjectOfType`.
- `if ( IsProxy ) return;` at the top of any networked input/movement update.
- `Log.Info(...)` / `Log.Warning(...)` — never `Console.WriteLine` or `System.IO`.

---

## Example 1 — Health Component with Damage Events

A reusable `Health` component implementing `IDamageable`. Broadcasts a scene-wide `IHealthEvents` event when entities die so other systems (score, spawn, HUD) can react without holding references.

```csharp
using Sandbox;

public interface IHealthEvents : ISceneEvent<IHealthEvents>
{
    void OnDamaged( Health health, in DamageInfo damage ) { }
    void OnKilled( Health health, in DamageInfo damage ) { }
}

public sealed class Health : Component, Component.IDamageable
{
    [Property, Range( 1f, 1000f )] public float MaxHealth { get; set; } = 100f;
    [Property] public bool Invincible { get; set; }

    [Sync, Change( nameof( OnHealthChanged ) )]
    public float Current { get; set; }

    public bool IsAlive => Current > 0f;

    TimeSince _timeSinceLastDamage;

    protected override void OnStart()
    {
        if ( !IsProxy )
            Current = MaxHealth;
    }

    public void OnDamage( in DamageInfo damage )
    {
        if ( IsProxy || !IsAlive || Invincible ) return;

        Current = MathX.Clamp( Current - damage.Damage, 0f, MaxHealth );
        _timeSinceLastDamage = 0;

        IHealthEvents.Post( x => x.OnDamaged( this, damage ) );

        if ( Current <= 0f )
            IHealthEvents.Post( x => x.OnKilled( this, damage ) );
    }

    void OnHealthChanged( float oldValue, float newValue )
    {
        if ( newValue < oldValue )
            Sound.Play( "player.hurt", WorldPosition );
    }
}
```

Key points:
- `[Sync, Change( nameof( OnHealthChanged ) )]` fires on every client when the owner changes health, even on proxies.
- `IDamageable.OnDamage` takes `in DamageInfo` — a `ref readonly` struct, so no allocation.
- Only the authoritative side (non-proxy) mutates `Current`. Change-events replicate to everyone.
- Scene events (`IHealthEvents.Post`) are local-only. If you need every client to see death effects, drive them from a `[Rpc.Broadcast]` instead.

---

## Example 2 — First-Person Character Controller

A standalone FPS controller using the lower-level `CharacterController` (no `PlayerController` helper). Good when you want explicit control over acceleration, friction, air control, and camera placement.

```csharp
using Sandbox;

public sealed class FirstPersonController : Component
{
    [Property] public CharacterController Controller { get; set; }
    [Property] public GameObject CameraPivot { get; set; }

    [Property, Range( 50f, 500f )] public float WalkSpeed { get; set; } = 180f;
    [Property, Range( 50f, 700f )] public float RunSpeed { get; set; } = 320f;
    [Property, Range( 100f, 600f )] public float JumpPower { get; set; } = 320f;
    [Property, Range( 1f, 20f )] public float GroundFriction { get; set; } = 6f;
    [Property, Range( 0f, 1f )] public float AirControl { get; set; } = 0.15f;

    [Sync] public Angles EyeAngles { get; set; }

    protected override void OnStart()
    {
        if ( !Controller.IsValid() )
            Controller = GetOrAddComponent<CharacterController>();
    }

    protected override void OnUpdate()
    {
        if ( IsProxy ) return;

        var look = EyeAngles;
        look += Input.AnalogLook;
        look.pitch = look.pitch.Clamp( -89f, 89f );
        look.roll = 0f;
        EyeAngles = look;

        if ( CameraPivot.IsValid() )
        {
            CameraPivot.WorldRotation = EyeAngles.ToRotation();
            CameraPivot.WorldPosition = WorldPosition + Vector3.Up * 64f;
        }
    }

    protected override void OnFixedUpdate()
    {
        if ( IsProxy ) return;

        var yaw = Rotation.FromYaw( EyeAngles.yaw );
        var wishDir = Input.AnalogMove * yaw;
        if ( !wishDir.IsNearZeroLength )
            wishDir = wishDir.Normal;

        var wishSpeed = Input.Down( "run" ) ? RunSpeed : WalkSpeed;

        if ( Controller.IsOnGround )
        {
            Controller.ApplyFriction( GroundFriction );
            Controller.Accelerate( wishDir * wishSpeed );

            if ( Input.Pressed( "jump" ) )
                Controller.Punch( Vector3.Up * JumpPower );
        }
        else
        {
            Controller.Velocity += Scene.PhysicsWorld.Gravity * Time.Delta;
            Controller.Accelerate( wishDir * wishSpeed * AirControl );
        }

        Controller.Move();

        WorldRotation = Rotation.FromYaw( EyeAngles.yaw );
    }
}
```

Setup in the scene:
1. GameObject with `FirstPersonController` + `CharacterController` (auto-added).
2. A child `GameObject` named `CameraPivot` with a `CameraComponent`. Drag it into the `CameraPivot` property.
3. Make sure the `CharacterController`'s `Height` / `Radius` match your player size (e.g., 72 / 16).

Why `OnFixedUpdate` for movement: the character controller does solver iterations and ground-sticking; running it at a variable rate causes jitter and inconsistent jumps.

---

## Example 3 — Hitscan Weapon with Networked Effects

A rifle that traces, applies damage, and plays impact effects on all clients. Uses `[Rpc.Broadcast]` to replicate effects without syncing a particle GameObject.

```csharp
using Sandbox;

public sealed class HitscanWeapon : Component
{
    [Property] public float Damage { get; set; } = 25f;
    [Property] public float Range { get; set; } = 5000f;
    [Property] public float RPM { get; set; } = 600f;
    [Property] public GameObject MuzzleFlashPrefab { get; set; }
    [Property] public GameObject ImpactPrefab { get; set; }
    [Property] public string ShootSound { get; set; } = "weapon.rifle.shoot";

    TimeSince _timeSinceShot;
    float FireDelay => 60f / RPM;

    protected override void OnUpdate()
    {
        if ( IsProxy ) return;
        if ( !Input.Down( "attack1" ) ) return;
        if ( _timeSinceShot < FireDelay ) return;

        _timeSinceShot = 0;
        Fire();
    }

    void Fire()
    {
        var cam = Scene.Camera;
        if ( !cam.IsValid() ) return;

        var ray = cam.ScreenNormalToRay( new Vector3( 0.5f, 0.5f, 0f ) );

        var tr = Scene.Trace.Ray( ray, Range )
            .UseHitboxes( true )
            .IgnoreGameObjectHierarchy( GameObject.Root )
            .WithoutTags( new[] { "trigger" } )
            .Run();

        if ( tr.Hit && tr.GameObject.IsValid() )
        {
            var damageable = tr.GameObject.Components.GetInAncestorsOrSelf<Component.IDamageable>();
            if ( damageable is not null )
            {
                damageable.OnDamage( new DamageInfo( Damage, GameObject.Root, GameObject )
                {
                    Position = tr.HitPosition,
                    Origin = ray.Position
                } );
            }
        }

        PlayShotEffects( tr.StartPosition, tr.EndPosition, tr.Normal, tr.Hit );
    }

    [Rpc.Broadcast( NetFlags.Unreliable )]
    void PlayShotEffects( Vector3 start, Vector3 end, Vector3 normal, bool hit )
    {
        Sound.Play( ShootSound, start );

        if ( MuzzleFlashPrefab.IsValid() )
        {
            var muzzle = MuzzleFlashPrefab.Clone( start );
            muzzle.BreakFromPrefab();
        }

        if ( hit && ImpactPrefab.IsValid() )
        {
            var impact = ImpactPrefab.Clone( end, Rotation.LookAt( normal ) );
            impact.BreakFromPrefab();
        }
    }
}
```

Key points:
- Tracing runs only on the shooter (`if ( IsProxy ) return;`) — damage application follows locally.
- The `[Rpc.Broadcast]` runs on **every** client including the shooter, so effects are visible everywhere without duplication.
- `NetFlags.Unreliable` is correct for cosmetic effects — missing a muzzle flash is fine, the alternative is network spam.
- Effects use `BreakFromPrefab()` so they become standalone GameObjects that self-destruct (assuming the prefab has a `TemporaryEffect` component).
- `IgnoreGameObjectHierarchy( GameObject.Root )` prevents self-hits even when the weapon is a child of a player rig.

---

## Example 4 — Networked Game Manager & Player Spawning

A `GameManager` implemented as a `GameObjectSystem` that sets up the lobby on host, then spawns player prefabs when each connection becomes active. Put this somewhere your scene always runs — as a system, you get automatic per-scene lifetime.

```csharp
using Sandbox;
using Sandbox.Network;
using System.Linq;

public sealed class GameManager : GameObjectSystem<GameManager>,
                                  ISceneStartup,
                                  Component.INetworkListener
{
    public GameManager( Scene scene ) : base( scene ) { }

    const string PlayerPrefabPath = "prefabs/player.prefab";

    void ISceneStartup.OnHostInitialize()
    {
        if ( !Networking.IsActive )
        {
            Networking.CreateLobby( new LobbyConfig
            {
                MaxPlayers = 8,
                Privacy = LobbyPrivacy.Public,
                Name = $"{Game.Ident} Game"
            } );
        }
    }

    public void OnActive( Connection connection )
    {
        var prefab = GameObject.GetPrefab( PlayerPrefabPath );
        if ( !prefab.IsValid() )
        {
            Log.Warning( $"Player prefab not found at {PlayerPrefabPath}" );
            return;
        }

        var spawn = PickSpawnPoint();
        var player = prefab.Clone( spawn.Position, spawn.Rotation );
        player.Name = $"Player - {connection.DisplayName}";
        player.NetworkSpawn( connection );
    }

    public void OnDisconnected( Connection connection )
    {
        // Clean up any objects left owned by this connection
        foreach ( var go in Scene.GetAllObjects( true ) )
        {
            if ( go.Network.Owner == connection )
                go.Destroy();
        }
    }

    Transform PickSpawnPoint()
    {
        var points = Scene.GetAllComponents<SpawnPoint>().ToList();
        if ( points.Count == 0 )
            return global::Transform.Zero;

        var chosen = points[Game.Random.Next( points.Count )];
        return chosen.WorldTransform;
    }
}
```

Key points:
- `GameObjectSystem<GameManager>` is auto-instantiated per scene. No need to put a component in the scene.
- `ISceneStartup.OnHostInitialize()` runs once, on host, after the scene has loaded — ideal for lobby creation.
- `INetworkListener.OnActive` fires after the connecting client has fully loaded the scene and is ready to receive gameplay.
- `NetworkSpawn( connection )` assigns ownership to the specified client so they control their own player.
- The `OnDisconnected` sweep matters because `NetworkOrphaned.Destroy` is only the default for **newly** spawned objects — some might have been transferred.

---

## Example 5 — Networked Player with Sync, RPCs, and Proxy Safety

A minimal player component that pairs with `FirstPersonController` from Example 2. Demonstrates the full networking surface: `INetworkSpawn`, `[Sync]`, proxy checks, owner-only RPCs, and `IGameObjectNetworkEvents`.

```csharp
using Sandbox;

public sealed class Player : Component,
                             Component.INetworkSpawn,
                             IGameObjectNetworkEvents
{
    [Property] public FirstPersonController Movement { get; set; }
    [Property] public Health Health { get; set; }
    [Property] public SkinnedModelRenderer Body { get; set; }

    [Sync] public string DisplayName { get; set; }
    [Sync] public int Kills { get; set; }
    [Sync] public int Deaths { get; set; }

    public void OnNetworkSpawn( Connection owner )
    {
        DisplayName = owner.DisplayName;

        // Hide the body for the owner (first-person view)
        if ( owner == Connection.Local && Body.IsValid() )
            Body.RenderType = ModelRenderer.ShadowRenderType.ShadowsOnly;
    }

    void IGameObjectNetworkEvents.NetworkOwnerChanged( Connection newOwner, Connection previousOwner )
    {
        Log.Info( $"{DisplayName} ownership: {previousOwner?.DisplayName} → {newOwner?.DisplayName}" );
    }

    [Rpc.Owner]
    public void TakeDamage( float amount, Guid attackerId )
    {
        // Runs on whoever owns this player
        Health?.OnDamage( new DamageInfo( amount, Scene.Directory.FindByGuid( attackerId ), null ) );
    }

    [Rpc.Broadcast]
    public void PlayEmote( string name )
    {
        if ( Body.IsValid() )
            Body.Set( $"emote_{name}", true );
    }

    [Rpc.Host]
    public void AddKill()
    {
        // Authoritative score increment — only the host mutates
        Kills++;
    }
}
```

Key points:
- `[Sync]` is **owner → everyone** by default. Only the owner can assign (unless you pass `SyncFlags.FromHost`).
- `[Rpc.Owner]` delivers to whichever client owns this `GameObject` — useful for "tell the victim they got hit."
- `[Rpc.Broadcast]` runs on all clients + host. For cosmetic/shared effects.
- `[Rpc.Host]` runs only on the host — for authoritative score, economy, or validated state changes.
- `Scene.Directory.FindByGuid(...)` is how you pass a `GameObject` reference through an RPC without including the full reference (cheaper and safer for late-joiners).
- `IGameObjectNetworkEvents.NetworkOwnerChanged` fires on every client — listen here to switch first-person / third-person visuals when ownership moves.

---

## Example 6 — Razor HUD Panel with Data Binding

A HUD showing health, ammo, and a rolling kill feed. Rebuilds only when `BuildHash` changes, so it's cheap to leave on screen.

**PlayerHud.razor** — drop on a `GameObject` with a `ScreenPanel` sibling:

```razor
@using Sandbox
@using Sandbox.UI
@inherits PanelComponent

<root class="hud">
    <div class="vitals">
        <div class="bar">
            <div class="fill" style="width: @(HealthPercent)%"></div>
            <label>@((int)HealthValue) / @((int)MaxHealthValue)</label>
        </div>
        <label class="ammo">@AmmoValue</label>
    </div>

    <div class="killfeed">
        @foreach ( var entry in KillFeed )
        {
            <label class="kill">@entry.Killer killed @entry.Victim</label>
        }
    </div>
</root>

@code
{
    Player LocalPlayer => Game.ActiveScene?
        .GetAllComponents<Player>()
        .FirstOrDefault( p => !p.IsProxy );

    float HealthValue => LocalPlayer?.Health?.Current ?? 0f;
    float MaxHealthValue => LocalPlayer?.Health?.MaxHealth ?? 1f;
    float HealthPercent => MathX.Clamp( HealthValue / MaxHealthValue * 100f, 0f, 100f );
    int AmmoValue => GetComponent<HitscanWeapon>() is { } w ? 30 : 0;

    List<KillEntry> KillFeed { get; } = new();

    public record KillEntry( string Killer, string Victim, TimeSince Time );

    protected override void OnUpdate()
    {
        KillFeed.RemoveAll( x => x.Time > 5f );
    }

    protected override int BuildHash() =>
        HashCode.Combine( HealthValue, AmmoValue, KillFeed.Count );
}
```

**PlayerHud.razor.scss** — auto-loaded by filename convention:

```scss
PlayerHud {
    position: absolute;
    left: 0; top: 0; right: 0; bottom: 0;
    pointer-events: none;
    flex-direction: column;
    justify-content: space-between;
    padding: 24px;

    .vitals {
        flex-direction: row;
        align-items: flex-end;
        justify-content: space-between;

        .bar {
            width: 320px;
            height: 28px;
            background-color: rgba( 0, 0, 0, 0.55 );
            border-radius: 4px;
            overflow: hidden;
            position: relative;

            .fill {
                height: 100%;
                background-color: #e74c3c;
                transition: width 0.25s ease-out;
            }

            label {
                position: absolute;
                left: 0; right: 0; top: 0; bottom: 0;
                justify-content: center;
                align-items: center;
                color: white;
                font-size: 16px;
                text-shadow: 1px 1px 2px black;
            }
        }

        .ammo {
            color: white;
            font-size: 48px;
            font-weight: bold;
            text-shadow: 2px 2px 4px black;
        }
    }

    .killfeed {
        flex-direction: column;
        align-items: flex-end;

        .kill {
            color: #ddd;
            font-size: 14px;
            padding: 4px 8px;
            background-color: rgba( 0, 0, 0, 0.4 );
            margin-bottom: 2px;
            transition: all 0.3s ease;
            &:intro { opacity: 0; transform: translateX( 40px ); }
            &:outro { opacity: 0; transform: translateX( -40px ); }
        }
    }
}
```

Key points:
- `BuildHash` is the **only** way to make the Razor tree rebuild cheaply. Include every value your template reads.
- `pointer-events: none` on the root lets game input pass through. Set `all` on interactive sub-panels if needed.
- `:intro` / `:outro` are s&box-specific transitions that fire when a panel is created / deleted. Combined with `Delete()` this gives animated kill-feed entries for free.
- `LocalPlayer` is queried every `BuildHash` — fine because it's a scene-wide `GetAllComponents` call, which is O(n) but small. For large scenes cache the reference in `OnStart`.

---

## Example 7 — Physics Grenade with Trigger Proximity & Explosion

A thrown grenade that bounces off world geometry, detonates on contact with a player, or times out. Shows `Rigidbody.ApplyImpulse`, `ICollisionListener`, `SceneTrace.Sphere` for radius damage, and async sequencing with `Task.DelaySeconds`.

```csharp
using Sandbox;
using System.Threading.Tasks;

public sealed class Grenade : Component, Component.ICollisionListener
{
    [Property] public float FuseSeconds { get; set; } = 3f;
    [Property] public float ExplosionRadius { get; set; } = 250f;
    [Property] public float MaxDamage { get; set; } = 150f;
    [Property] public float ThrowForce { get; set; } = 800f;
    [Property] public GameObject ExplosionPrefab { get; set; }

    Rigidbody _rigidbody;
    bool _exploded;

    protected override void OnStart()
    {
        _rigidbody = GetOrAddComponent<Rigidbody>();
        _rigidbody.Velocity = WorldRotation.Forward * ThrowForce;
        _rigidbody.AngularVelocity = Vector3.Random * 5f;

        _ = FuseTimer();
    }

    async Task FuseTimer()
    {
        await Task.DelaySeconds( FuseSeconds );
        Explode();
    }

    public void OnCollisionStart( Collision collision )
    {
        // Bounce off world, but detonate on contact with a player
        if ( collision.Other.GameObject.Tags.Has( "player" ) )
            Explode();
    }

    public void OnCollisionUpdate( Collision collision ) { }
    public void OnCollisionStop( CollisionStop collision ) { }

    void Explode()
    {
        if ( _exploded ) return;
        _exploded = true;

        var origin = WorldPosition;

        if ( ExplosionPrefab.IsValid() )
        {
            var fx = ExplosionPrefab.Clone( origin );
            fx.BreakFromPrefab();
        }

        // Radius damage — sphere overlap via sphere sweep of zero length
        foreach ( var hit in Scene.Trace
                     .Sphere( ExplosionRadius, origin, origin )
                     .WithAnyTags( "player", "prop" )
                     .RunAll() )
        {
            var target = hit.GameObject.Components.GetInAncestorsOrSelf<Component.IDamageable>();
            if ( target is null ) continue;

            var dist = Vector3.DistanceBetween( origin, hit.EndPosition );
            var falloff = 1f - MathX.Clamp( dist / ExplosionRadius, 0f, 1f );

            target.OnDamage( new DamageInfo( MaxDamage * falloff, GameObject, null )
            {
                Position = hit.EndPosition,
                Origin = origin,
                IsExplosion = true
            } );

            // Knockback — find a Rigidbody on the victim and shove it
            var rb = hit.GameObject.Components.GetInAncestorsOrSelf<Rigidbody>();
            if ( rb.IsValid() )
            {
                var dir = (hit.EndPosition - origin).Normal;
                rb.ApplyImpulse( dir * MaxDamage * falloff * 10f );
            }
        }

        GameObject.Destroy();
    }
}
```

Key points:
- `Components.GetOrCreate<Rigidbody>()` is safe to call in `OnStart` — idempotent, and hot-reload friendly.
- `_ = FuseTimer();` kicks off an async countdown without awaiting it. Because `Component.Task` is scoped to the GameObject, if the grenade is destroyed early the await is cancelled.
- `Scene.Trace.Sphere( r, origin, origin ).RunAll()` gives you every overlapping physics shape for radius damage — cheaper than iterating all components + distance checks.
- `WithAnyTags` does the broad-phase filter — use tags, not type checks, to avoid coupling the grenade to player code.
- `falloff = 1 - distance / radius` is a linear falloff; you can also use `MathX.LerpInverse` for other curves.

---

## Example 8 — NavMeshAgent AI with State Machine

A simple enemy that patrols between waypoints, chases the player when close, and attacks when in range. Uses `NavMeshAgent` for movement and `CitizenAnimationHelper` to drive anim parameters.

```csharp
using Sandbox;
using System.Linq;

public sealed class EnemyAi : Component
{
    public enum State { Idle, Patrol, Chase, Attack, Dead }

    [Property] public NavMeshAgent Agent { get; set; }
    [Property] public SkinnedModelRenderer Body { get; set; }
    [Property] public Health Health { get; set; }

    [Property] public float SightRange { get; set; } = 800f;
    [Property] public float AttackRange { get; set; } = 120f;
    [Property] public float AttackDamage { get; set; } = 15f;
    [Property] public float AttackCooldown { get; set; } = 1.25f;
    [Property] public float PatrolRadius { get; set; } = 600f;

    State _state;
    Vector3 _patrolTarget;
    GameObject _target;
    TimeSince _timeSinceAttack;
    TimeSince _timeSinceDecision;

    protected override void OnStart()
    {
        Agent ??= Components.Get<NavMeshAgent>();
        PickNewPatrolTarget();
    }

    protected override void OnFixedUpdate()
    {
        if ( IsProxy ) return;
        if ( Health is { IsAlive: false } )
        {
            EnterState( State.Dead );
            return;
        }

        if ( _timeSinceDecision > 0.25f )
        {
            _timeSinceDecision = 0;
            UpdatePerception();
        }

        switch ( _state )
        {
            case State.Idle:
            case State.Patrol: TickPatrol(); break;
            case State.Chase: TickChase(); break;
            case State.Attack: TickAttack(); break;
        }

        DriveAnimation();
    }

    void UpdatePerception()
    {
        var nearest = Scene.GetAllComponents<Player>()
            .Where( p => p.Health is { IsAlive: true } )
            .OrderBy( p => Vector3.DistanceBetween( p.WorldPosition, WorldPosition ) )
            .FirstOrDefault();

        if ( nearest is null )
        {
            _target = null;
            if ( _state is State.Chase or State.Attack )
                EnterState( State.Patrol );
            return;
        }

        var dist = Vector3.DistanceBetween( nearest.WorldPosition, WorldPosition );
        if ( dist > SightRange )
        {
            _target = null;
            if ( _state is State.Chase or State.Attack )
                EnterState( State.Patrol );
            return;
        }

        _target = nearest.GameObject;
        EnterState( dist <= AttackRange ? State.Attack : State.Chase );
    }

    void TickPatrol()
    {
        if ( !Agent.IsNavigating || Vector3.DistanceBetween( WorldPosition, _patrolTarget ) < 40f )
            PickNewPatrolTarget();
    }

    void TickChase()
    {
        if ( !_target.IsValid() ) return;
        Agent.MoveTo( _target.WorldPosition );
    }

    void TickAttack()
    {
        if ( !_target.IsValid() ) return;

        Agent.Stop();

        var lookDir = (_target.WorldPosition - WorldPosition).WithZ( 0 ).Normal;
        if ( !lookDir.IsNearZeroLength )
            WorldRotation = Rotation.LerpTo( WorldRotation, Rotation.LookAt( lookDir ), Time.Delta * 8f );

        if ( _timeSinceAttack >= AttackCooldown )
        {
            _timeSinceAttack = 0;
            DoAttack();
        }
    }

    void DoAttack()
    {
        if ( !_target.IsValid() ) return;

        var dmg = _target.Components.GetInAncestorsOrSelf<Component.IDamageable>();
        dmg?.OnDamage( new DamageInfo( AttackDamage, GameObject, null )
        {
            Position = _target.WorldPosition,
            Origin = WorldPosition
        } );
    }

    void PickNewPatrolTarget()
    {
        _patrolTarget = Scene.NavMesh.GetRandomPoint( WorldPosition, PatrolRadius ) ?? WorldPosition;
        Agent.MoveTo( _patrolTarget );
        EnterState( State.Patrol );
    }

    void EnterState( State newState )
    {
        if ( _state == newState ) return;
        _state = newState;

        if ( newState == State.Dead )
        {
            Agent.Stop();
            Agent.Enabled = false;
        }
    }

    void DriveAnimation()
    {
        if ( !Body.IsValid() ) return;
        Body.Set( "move_speed", Agent.Velocity.Length );
        Body.Set( "b_attack", _state == State.Attack && _timeSinceAttack < 0.1f );
    }
}
```

Key points:
- Perception runs at 4 Hz (`_timeSinceDecision > 0.25f`), not every fixed tick. AI doesn't need per-frame decisions, and it keeps the cost flat as enemy count grows.
- `Agent.MoveTo` is idempotent — calling it every tick with the same target is cheap. But `Agent.Stop()` before a melee swing prevents overshoot.
- `Scene.NavMesh.GetRandomPoint(origin, radius)` returns `Vector3?` — `null` when no NavMesh exists or the point can't be sampled. Fall back to current position.
- `Agent.Velocity.Length` drives the anim graph's move-speed parameter — the same pattern works with `CitizenAnimationHelper.WithVelocity`.

---

## Example 9 — Prefab Spawner with Pool-Friendly Lifecycle

A spawner that uses `GameObject.GetPrefab` + `Clone` to create enemies on a timer. Parks new instances under a pool root to keep the scene outliner tidy, and respects host authority.

```csharp
using Sandbox;

public sealed class PrefabSpawner : Component
{
    [Property] public GameObject Prefab { get; set; }
    [Property, Range( 0.5f, 30f )] public float Interval { get; set; } = 5f;
    [Property] public int MaxAlive { get; set; } = 12;
    [Property] public bool NetworkSpawned { get; set; } = true;

    TimeUntil _nextSpawn;
    readonly List<GameObject> _alive = new();

    protected override void OnStart()
    {
        _nextSpawn = Interval;
    }

    protected override void OnUpdate()
    {
        // Only the host spawns authoritative objects
        if ( NetworkSpawned && !Networking.IsHost ) return;

        _alive.RemoveAll( go => !go.IsValid() );

        if ( _alive.Count >= MaxAlive ) return;
        if ( !_nextSpawn ) return;

        _nextSpawn = Interval;
        Spawn();
    }

    void Spawn()
    {
        if ( !Prefab.IsValid() ) return;

        var pos = WorldPosition + Vector3.Random.WithZ( 0 ) * 100f;
        var rot = Rotation.FromYaw( Game.Random.NextSingle() * 360f );

        var go = Prefab.Clone( pos, rot );
        go.Name = $"{Prefab.Name} (spawn)";
        go.SetParent( GameObject );

        if ( NetworkSpawned )
            go.NetworkSpawn();

        _alive.Add( go );
    }

    protected override void OnDestroy()
    {
        foreach ( var go in _alive )
        {
            if ( go.IsValid() )
                go.Destroy();
        }
    }
}
```

Key points:
- `Prefab.Clone(pos, rot)` creates an instance with a live link to the source prefab — future edits to the prefab asset propagate. Call `BreakFromPrefab()` only if you need to break that link.
- `NetworkSpawn()` must only be called on the authority. The `Networking.IsHost` gate prevents double-spawns when clients run this component.
- `Vector3.Random` returns a unit-length random direction; multiply to taste.
- `_alive.RemoveAll( go => !go.IsValid() )` is the idiomatic way to prune destroyed references — `GameObject.IsValid()` is `false` after `Destroy()`.
- Parenting spawned objects under the spawner's `GameObject` gives you free cleanup via `OnDestroy` and a single place to disable them all with `GameObject.Enabled = false`.

---

## Example 10 — Trigger Zone (Pickup / Checkpoint)

A `Collider` configured as a trigger that grants a pickup when a player overlaps. Uses `ITriggerListener` and a broadcast RPC to tell every client to play the pickup effect.

```csharp
using Sandbox;

public sealed class HealthPickup : Component, Component.ITriggerListener
{
    [Property] public float HealAmount { get; set; } = 25f;
    [Property] public float RespawnTime { get; set; } = 15f;
    [Property] public GameObject PickupEffect { get; set; }
    [Property] public ModelRenderer Visual { get; set; }

    [Sync] bool _available { get; set; } = true;

    public void OnTriggerEnter( Collider other )
    {
        if ( !Networking.IsHost ) return;
        if ( !_available ) return;
        if ( !other.GameObject.Tags.Has( "player" ) ) return;

        var health = other.GameObject.Components.GetInAncestorsOrSelf<Health>();
        if ( health is null || !health.IsAlive ) return;
        if ( health.Current >= health.MaxHealth ) return;

        health.Current = MathX.Clamp( health.Current + HealAmount, 0f, health.MaxHealth );

        _available = false;
        PlayPickupEffect();
        _ = RespawnAsync();
    }

    public void OnTriggerExit( Collider other ) { }

    [Rpc.Broadcast]
    void PlayPickupEffect()
    {
        if ( Visual.IsValid() )
            Visual.Enabled = false;

        if ( PickupEffect.IsValid() )
        {
            var fx = PickupEffect.Clone( WorldPosition );
            fx.BreakFromPrefab();
        }

        Sound.Play( "pickup.health", WorldPosition );
    }

    [Rpc.Broadcast]
    void RespawnVisual()
    {
        if ( Visual.IsValid() )
            Visual.Enabled = true;
    }

    async Task RespawnAsync()
    {
        await Task.DelaySeconds( RespawnTime );
        if ( !this.IsValid() ) return;

        _available = true;
        RespawnVisual();
    }
}
```

Setup:
1. GameObject with `BoxCollider` (or `SphereCollider`) — set `IsTrigger = true`.
2. Add the `HealthPickup` component.
3. Drag the child `ModelRenderer` into the `Visual` property.
4. Mark players with the `"player"` tag (on the root player GameObject).

Key points:
- Only the host handles the pickup — other clients just see the `_available` sync and the RPCs.
- `[Sync]` on a backing field exposes the state to clients so a late-joining player sees pickups that are currently consumed.
- `Task.DelaySeconds` on a `Component` auto-cancels when the pickup is destroyed (via the implicit `Component.Task` scope).
- Don't forget to set `IsTrigger = true` on the collider — otherwise you get physics collisions, not trigger events.

---

## Quick-Reference Patterns

These are idioms that show up everywhere. Keep them in muscle memory.

### Local-player query

```csharp
var localPlayer = Scene.GetAllComponents<Player>().FirstOrDefault( p => !p.IsProxy );
```

Cache in `OnStart` if you query it every frame.

### "Do this on the host only"

```csharp
if ( !Networking.IsHost ) return;
```

### "Do this on the owner only"

```csharp
if ( IsProxy ) return;
```

### "Do this on everyone with authoritative data"

```csharp
[Rpc.Broadcast]
void DoThing( Vector3 pos ) { /* runs everywhere */ }
```

### Schedule work after a delay

```csharp
_ = DelayedWork();

async Task DelayedWork()
{
    await Task.DelaySeconds( 2f );
    if ( !this.IsValid() ) return;   // cancellation guard
    DoTheThing();
}
```

### Trace from crosshair

```csharp
var ray = Scene.Camera.ScreenNormalToRay( new Vector3( 0.5f, 0.5f, 0f ) );
var tr = Scene.Trace.Ray( ray, 5000f )
    .IgnoreGameObjectHierarchy( GameObject.Root )
    .UseHitboxes( true )
    .Run();
```

### Find all players and iterate

```csharp
foreach ( var player in Scene.GetAllComponents<Player>() )
{
    if ( player.IsProxy ) continue;
    // ...
}
```

### Broadcast an effect without syncing a GameObject

```csharp
[Rpc.Broadcast( NetFlags.Unreliable )]
void PlayEffect( Vector3 position )
{
    EffectPrefab.Clone( position ).BreakFromPrefab();
}
```

### Guard against destroyed references

```csharp
if ( !target.IsValid() ) return;           // works on GameObject, Component, and any IValid
```

### Tag-based collision filter (cheaper than type checks)

```csharp
if ( !other.GameObject.Tags.Has( "player" ) ) return;
```

### One-shot sound

```csharp
Sound.Play( "ui.click" );                                // 2D
Sound.Play( "impact.metal", hitPosition );               // 3D
GameObject.PlaySound( soundEventAsset, Vector3.Zero );   // attached to GO
```

---

## Anti-Patterns

If you find yourself writing one of these, stop.

| Wrong | Right | Why |
|---|---|---|
| `Update()` | `protected override void OnUpdate()` | s&box isn't Unity; the virtual method is `OnUpdate`. |
| `GetComponent<T>()` in a hot loop | Cache the reference in `OnStart` | Component lookup is cheap but not free; 60× per second on 100 objects is waste. |
| Reading `Input.*` in `OnFixedUpdate` | Read in `OnUpdate`, store, consume in `OnFixedUpdate` | Input polling is tied to frame rate, not physics tick. `Pressed`/`Released` may be missed. |
| Mutating `[Sync]` fields on a proxy | Guard with `if ( IsProxy ) return;` | Clients overwrite each other; the value snaps back on the next sync. |
| `Scene.GetAllComponents<T>()` in `OnUpdate` on every object | Cache or use scene events | O(scene) × O(components) quickly becomes the frame budget. |
| `Instantiate(prefab)` / `gameObject.SetActive(false)` | `prefab.Clone(pos)` / `go.Enabled = false` | These are Unity APIs that don't exist. |
| `Debug.Log(...)` | `Log.Info(...)` | Unity name, doesn't exist. |
| `new Thread(...)` or raw `System.IO.File` | s&box `FileSystem.Data` and async/await | Most of `System.IO` is blocked by the sandbox whitelist. |
| `transform.position = ...` | `WorldPosition = ...` | `transform` isn't a field — Transform access is via `WorldPosition` / `LocalPosition` shortcuts. |
| Calling `[Rpc.Broadcast]` methods on proxies without owner check | Guard with `IsProxy` / `Networking.IsHost` as appropriate | Every proxy re-firing an RPC multiplies the message count. |
| Building UI in C# imperatively every frame | Razor with `BuildHash` | Razor diffing is cheap; rebuilding the DOM from scratch is not. |

---

*See the topical references (`core-concepts.md`, `components-builtin.md`, `networking.md`, `input-and-physics.md`, `ui-razor.md`) for exhaustive API details. This file is for patterns and shape; those are for signatures and specifics.*
