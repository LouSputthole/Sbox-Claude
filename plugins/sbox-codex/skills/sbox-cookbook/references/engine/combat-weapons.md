# Combat & Weapons

Build weapons, projectiles, explosions, ballistic previews, and cancellable combat actions in s&box using the modern Component + `Scene.Trace` API.

## Mental model

A weapon is a `Component` that runs an **input pump** every frame and an authoritative **fire path** on the owner. Keep three jobs separate so the same weapon is drivable by a human OR an AI:

- `Can*Attack()` — am I *allowed* (ammo, not reloading, cooldown elapsed)?
- `Wants*Attack()` — did the controller *ask* (input down, or AI flag set)?
- `*Attack()` — actually *do* it (trace, damage, effects).

The pump just wires them: `if ( CanPrimaryAttack() && WantsPrimaryAttack() ) PrimaryAttack();` (sandbox: `Code/Game/Weapon/BaseWeapon/BaseWeapon.cs:129`). An AI never touches input — it overrides `Wants*` to return a decision, and the rest is identical.

Authority: the fire trace + damage run only on the owner (`if ( IsProxy ) return;`); cosmetic muzzle flash / tracers / decals run for everyone via `[Rpc.Broadcast]`. See the gotcha table — getting this wrong means "only I see my own bullets" or silent rollback.

## Pick an architecture (commit to ONE spine)

| Architecture | Shape | Best for | Source |
|---|---|---|---|
| **Deep base-class hierarchy** | `BaseCarryable → BaseWeapon → BaseBulletWeapon → GlockWeapon`; base owns pump/ammo/cooldown + a record-struct config, concrete overrides `PrimaryAttack`/`GetPrimaryFireRate` | curated, code-authored set sharing lots of logic | sandbox: `BaseWeapon.cs:116` |
| **Data-driven enum + delegates** | one `Weapon` component, `FiringType` switch (Instant/Charged/Cursor/Continuous) in `OnUpdate`, designer-set `[Property]` Cooldown/MaxUses + `OnFire`/`OnFireFinished` delegate properties | LARGE arsenal designers tune in-editor without new classes | data-driven pattern |
| **Strategy as a child Component** | `abstract class BulletInfo : Component { Shoot(...); }`; attach `HitScanBulletInfo` or `PhysicalBulletInfo` | polymorphic, designer-selectable behavior where each variant has real distinct logic | simple-weapon-base: `bullets/BulletInfo.Base.cs:5` |

The strategy-Component is the modern idiomatic choice when each variant is substantial; prefer it over an enum+switch. Default it safely: `Components.Create<HitScanBulletInfo>()` if none is attached. Don't mix all three.

## Recipes

### 1. Shot cooldown with `TimeUntil` + `AddShootDelay`

`TimeUntil` is a struct that counts down in real seconds; assign a float and it's "now + that". Put the check in `Can*Attack`, push it forward after firing (sandbox: `BaseWeapon.cs:21,171`).

```csharp
protected TimeUntil TimeUntilNextShotAllowed;
public void AddShootDelay( float seconds ) => TimeUntilNextShotAllowed = seconds;

public virtual bool CanPrimaryAttack()
{
    if ( HasOwner && !HasAmmo() ) return false;
    if ( IsReloading() ) return false;
    if ( TimeUntilNextShotAllowed > 0 ) return false;   // still cooling down
    return true;
}

public override void PrimaryAttack()
{
    AddShootDelay( GetPrimaryFireRate() );              // re-arm the gate
    // ... fire ...
}
```

Keep per-weapon tunables (fire rate, aim-cone, spread, recoil) in a `record struct` config so a concrete weapon overrides them trivially.

### 2. Fire a hitscan shot (modern fluent trace + broadcast effects)

Run the trace authoritatively, apply damage, THEN broadcast cosmetics (sandbox: `BaseBulletWeapon.cs:81`; simple-weapon-base: `BulletInfo.HitScan.cs:11`).

```csharp
var traceRay = AimRay with { Forward = AimRay.Forward.WithAimCone( spreadX, spreadY ) };

var tr = Scene.Trace.Ray( traceRay, config.Range )
    .IgnoreGameObjectHierarchy( AimIgnoreRoot )
    .WithCollisionRules( "bullet" )   // named ruleset → designers retune what bullets hit
    .WithoutTags( "playercontroller" )
    .Radius( config.BulletRadius )
    .UseHitboxes()                    // hits land on skinned hitboxes → headshots work
    .Run();

// authoritative: resolve the logical entity, not the raw collider GameObject
tr.GameObject?.Components.GetInAncestorsOrSelf<IDamageable>()
   ?.OnDamage( DamageInfo.FromBullet( /* attacker, weapon, hitbox, pos, force, dmg... */ ) );

ShootEffects( tr.EndPosition, tr.Hit, tr.Normal, tr.GameObject, tr.Surface );  // cosmetics
```

```csharp
[Rpc.Broadcast( NetFlags.Unreliable )]   // cosmetic, lossy-OK → Unreliable
void ShootEffects( Vector3 end, bool hit, Vector3 normal, GameObject hitObj, Surface surface )
{
    if ( Application.IsDedicatedServer ) return;   // headless host has nothing to draw
    // muzzle flash, tracer, impact decal/sound here
}
```

`WithCollisionRules`+`UseHitboxes` confirmed at sandbox `BaseBulletWeapon.cs:83-86`; the `[Rpc.Broadcast( NetFlags.Unreliable )]` + `Application.IsDedicatedServer` early-out at simple-weapon-base `BulletInfo.HitScan.cs:59-62`.

### 3. Physical (travel-time) projectile as a per-step integrating mover

Spawn a networked GameObject carrying a mover; integrate in **`OnFixedUpdate`** (deterministic, frame-rate-independent) and trace each segment from last position to new (simple-weapon-base: `bullets/PhysicalBullet.Mover.cs:34`). Wrap it behind the same `BulletInfo.Shoot` strategy interface as hitscan so weapon code is identical.

```csharp
protected override void OnFixedUpdate()
{
    if ( IsProxy || HasImpacted || Owner is null ) return;

    BulletVelocity *= 1 - BulletDrag;                          // drag first
    BulletVelocity += Vector3.Down * BulletGravity * Time.Delta;
    var step = BulletVelocity * Time.Delta;

    var tr = Weapon.TraceBullet( Owner.GameObject, WorldPosition, WorldPosition + step );
    if ( tr.Hit ) { HandleImpact( tr ); WorldPosition = tr.HitPosition; HasImpacted = true; return; }
    WorldPosition += step;                                     // miss → advance
}
```

### 4. Radial explosion: sphere query → falloff → per-body knockback → networked damage

One helper ties it together: query `Scene.FindInPhysics(new Sphere(...))`, inverse-square falloff, knockback down the **correct path per body type**, damage via `Health.TakeDamage`, world destruction host-only (sbox-grubs: `Code/Helpers/ExplosionHelper.cs:18`).

```csharp
foreach ( var go in Scene.FindInPhysics( new Sphere( position, radius ) ) )
{
    if ( !go.Components.TryGet( out Health health, FindMode.EverythingInSelfAndAncestors ) )
        continue;   // hit a child collider → resolve to the entity root, else null

    var dist = Vector3.DistanceBetween( position, go.WorldPosition );
    var distFactor = 1.0f - MathF.Pow( dist / radius, 2 ).Clamp( 0, 1 );   // inverse-square

    // knockback: kinematic character vs physics prop use DIFFERENT calls
    if ( go.Components.TryGet( out CharacterController cc, FindMode.EverythingInSelfAndAncestors ) )
    { cc.Punch( dir * force ); cc.ReleaseFromGround(); }
    else if ( go.Components.TryGet( out Rigidbody body, FindMode.EverythingInSelf ) )
        body.ApplyImpulseAt( body.WorldPosition, dir * force * body.PhysicsBody.Mass );

    health.TakeDamage( DamageInfo.FromExplosion( damage * distFactor, /* attacker, pos */ ) );
}

using ( Rpc.FilterInclude( c => c.IsHost ) )   // SDF terrain carve/scorch: host-only
{
    GrubsTerrain.Instance.SubtractCircle( center2d, radius / 2f, 1 );
}
```

`CharacterController.Punch` + `ReleaseFromGround` vs `Rigidbody.ApplyImpulseAt` and the `Rpc.FilterInclude(c => c.IsHost)` host-only carve are all at `ExplosionHelper.cs:41-66`.

### 5. Ballistic arc prediction (aim reticle / trajectory line / AI targeting)

Sample the flight path in N segments, `Scene.Trace.Ray` each, stop at first hit, return the segment list (sbox-grubs: `Code/Helpers/ArcSegment.cs:64`). Two modes:

- **`RunTo`** — cubic bezier toward a control point: cheap, smooth preview, not physically faithful.
- **`RunTowards`** — integrates the *real* motion so the preview matches the live projectile.

```csharp
var velocity = force * -direction;
var position = startPos;
for ( var i = 0; i < SegmentCount; i++ )
{
    var seg = new ArcSegment { StartPos = position };
    velocity -= new Vector3( windForceX / 2, 0, 0 );
    velocity -= scene.PhysicsWorld.Gravity * epsilon;   // SAME gravity the projectile uses
    position -= velocity;
    seg.EndPos = position;

    var tr = scene.Trace.Ray( seg.StartPos, seg.EndPos )
        .IgnoreGameObjectHierarchy( Grub.GameObject ).Radius( 4f ).Run();
    segments.Add( seg );
    if ( tr.Hit ) { seg.EndPos = tr.EndPosition; seg.HitNormal = tr.Normal; break; }
}
```

`RunTowardsWithBounces` reflects off surfaces and damps `activeForce *= 0.66f` per bounce, treating a near-vertical hit (`Vector3.GetAngle(hitNormal, Vector3.Up) < 45`) as a stop (`ArcSegment.cs:114`). **Use the exact same gravity/wind/drag constants as the live projectile or the preview lies.**

### 6. Cancellable reload/channel/build — capture-then-compare the token

Model long interruptible actions as `async`. A new action cancels the old via a stored `CancellationTokenSource`; the critical idiom is to **capture your token at entry and only fire the "finished" callback if you still own it** — otherwise a superseded reload emits a spurious "finished" on the wrong instance (sandbox-plus-plus: `Code/Game/Weapon/BaseWeapon/BaseWeapon.Reloading.cs:95`).

```csharp
public virtual async void OnReloadStart()
{
    if ( !CanReload() ) return;
    CancelReload();                              // cancel any in-flight reload
    var cts = new CancellationTokenSource();
    reloadToken = cts; isReloading = true;
    try { await ReloadAsync( cts.Token ); }
    finally { if ( reloadToken == cts ) { isReloading = false; reloadToken = null; } cts.Dispose(); }
}

protected virtual async Task ReloadAsync( CancellationToken ct )
{
    var mySource = reloadToken;                  // capture
    try
    {
        while ( ClipContents < ClipMaxSize && !ct.IsCancellationRequested )
            await Task.DelaySeconds( ReloadTime, ct );   // cancellation interrupts the wait
        // ... fill clip ...
    }
    finally
    {
        if ( reloadToken == mySource )           // compare: am I still the current reload?
            ViewModel?.RunEvent<ViewModel>( x => x.OnReloadFinish() );
    }
}
```

`CancelReload()` just guards-and-cancels: `if ( reloadToken?.IsCancellationRequested == false ) reloadToken.Cancel();` (`BaseWeapon.Reloading.cs:46`). Generalizes to any overlapping cancellable action, not just reloads.

## Gotcha table

| Gotcha | Fix |
|---|---|
| Overlapping async reloads fire a stale "finished" callback on the wrong instance | Capture `var mySource = reloadToken` at entry; in `finally` only finalize `if ( reloadToken == mySource )` (`BaseWeapon.Reloading.cs:98,146`) |
| Knockback silently does nothing | Kinematic `CharacterController` needs `.Punch` (+ often `.ReleaseFromGround`); physics bodies need `Rigidbody.ApplyImpulseAt` — different APIs per body type (`ExplosionHelper.cs:53,64`) |
| Headshots / limb damage don't register | Add `.UseHitboxes()` to the shot trace, else hits land on the coarse physics body |
| Aim-preview arc doesn't match where the shot lands | Reuse the *exact* gravity/wind/drag constants the live projectile integrates; keep them in one shared place (`ArcSegment.cs`) |
| Only the shooter (or only the host) sees muzzle flash/tracers/decals | Put cosmetics in a `[Rpc.Broadcast]` method, not the authoritative fire path |
| Terrain carve/scorch desyncs across clients | Run world destruction host-only inside `using ( Rpc.FilterInclude( c => c.IsHost ) )` (`ExplosionHelper.cs:41`) |
| `Components.Get<Health>()` on a trace hit returns null | A trace hits a child collider/hitbox, not the entity root → use `FindMode.EverythingInSelfAndAncestors` / `GetInAncestorsOrSelf<IDamageable>()` |
| Projectile ballistics jitter / differ across clients | Integrate in `OnFixedUpdate`, not `OnUpdate` (frame-rate dependent) |
| Designers can't retune what bullets hit without a code change | Use a named collision ruleset (`.WithCollisionRules("bullet")`) instead of hardcoded tag checks |
| Effect/RPC body wastes cycles or null-crashes on a dedicated server | Early-return on `Application.IsDedicatedServer` in broadcast bodies (`BulletInfo.HitScan.cs:62`) |
| `new GameObject`/`Clone()` projectile is invisible to other clients | Call `NetworkSpawn()` after configuring it; spawn on exactly one machine (`if ( IsProxy ) return;`) |
| Synced mutator on a proxy silently rolls back | Gate every authoritative mutator behind `if ( IsProxy ) return;` (owner) or `if ( !Networking.IsHost ) return;` (host) |
| `Network.IsOwner` is false in solo editor playtest (no lobby) → firing disabled | The bridge is single-client and can't synthesize keypresses; verify fire/cooldown/reload with `execute_csharp` or a human playtest, visuals with `screenshot_from` |

**Verify live:** API names drift between SDK builds — confirm against the installed SDK with `describe_type`/`search_types`/`get_method_signature` (reflection is authoritative) before writing, e.g. `describe_type SceneTrace`, `describe_type CharacterController`, `search_types BulletInfo`. Wrap genuinely volatile calls in try/catch with a safe fallback.

Cross-links: see the **sbox-api** skill for reflection-verified type/method signatures, and **sbox-build-feature** for the screenshot-driven iteration loop that proves a weapon actually fires in-scene.

## Corpus refresh (2026): more reference implementations

### A. Anim-event damage windows instead of timers (aethercore.versus)

Opening a hit trigger based on elapsed time is fragile — if a flinch or guard-break interrupts the swing before `hit_end` fires, the trigger stays active and grants free hits. The correct pattern is to open a **collider trigger on the `hit_start` anim event** and close it on `hit_end`, then re-validate attacker state inside `OnTriggerEnter`.

`versus/Code/WeaponDamage.cs` — `Component.ITriggerListener`; `OnAttackHitStart` enables the collider, `OnAttackHitEnd` disables it. Inside `OnTriggerEnter` it dedupes with a `HashSet<GameObject>` (one hit per target root per swing) and refuses damage if the attacker's state flags are wrong:

```csharp
// Wire in PlayerAnimator: Model.OnGenericEvent += OnAnimEvent
void OnAnimEvent( string name ) {
    if ( name == "hit_start" ) WeaponDamage.StartDamageWindow( damage, shieldDmg, knockbackMult );
    else if ( name == "hit_end" ) WeaponDamage.EndDamageWindow();
}

// WeaponDamage : Component, Component.ITriggerListener
HashSet<GameObject> _hitThisSwing = new();
void OnTriggerEnter( Collider other ) {
    var root = other.GameObject.Root;
    if ( _hitThisSwing.Contains( root ) ) return;           // dedupe per swing
    if ( !inAttack || IsGuardBroken || IsParrying ) return; // re-check — collider may be stale
    if ( targetFaction == myFaction ) return;               // friendly fire guard
    _hitThisSwing.Add( root );
    root.Components.GetInAncestorsOrSelf<IDamageable>()?.OnDamage( ... );
}
```

Anti-pattern: using `TimeUntil hitWindowEnd > 0` as the gate — timer doesn't know the swing was interrupted.

### B. `[Rpc.Owner]` damage routing preserves private timers (aethercore.versus)

When melee combat depends on non-synced private state (parry window timer, i-frame flag, guard meter) that is only correct on the victim's owning machine, routing damage through `[Rpc.Owner]` ensures the victim's own authoritative logic runs the outcome. On the attacker's proxy the victim's timer is always stale.

`versus/Code/HealthComponent.cs` + `PlayerController.cs`:

```csharp
public void TakeDamage( DamageInfo info ) {
    // any machine may call this, but mutate only on owner
    TakeDamageRpc( info );
}

[Rpc.Owner]
void TakeDamageRpc( DamageInfo info ) {
    // runs on victim's owner — parryWindowTimer is correct here
    if ( parryWindowTimer > 0 && IsParryAngle( info ) ) { DoParrySuccess( info ); return; }
    if ( IsGuarding ) { shield.AbsorbDamage( info ); return; }
    health -= info.Damage;
    OnDamageReceived( info );
}
```

Anti-pattern: reading `parryWindowTimer` or an i-frame flag on the attacker's machine — proxy values are always 0/stale.

### C. Penetrating hitscan — `IEnumerable<SceneTraceResult>` (ataco.sdoomresurrection)

The standard `.Run()` returns only the first hit. For weapons that pierce multiple targets (SSG pellets, energy beams, chain-lightning), call `.RunAll()` which returns `IEnumerable<SceneTraceResult>` sorted by distance. Apply damage to each; break on the first solid (non-passthrough) surface.

`sdoomresurrection/Code/weapon/Weapon.cs`:

```csharp
var results = Scene.Trace.Ray( ray, range )
    .WithAnyTags( "monster", "player", "bulletclip", "blocking" )
    .Size( radius )
    .RunAll();

foreach ( var tr in results ) {
    tr.GameObject?.Components.GetAll<IDamageable>( FindMode.EverythingInSelfAndAncestors )
        .FirstOrDefault()?.TakeDamage( DamageInfo.FromBullet( ... ) );
    if ( tr.Tags.Has( "blocking" ) ) break;   // stop at solid wall
}
```

Spread is applied per-pellet as `Rotation.FromYaw( rand * spread.x ) * Rotation.FromPitch( rand * spread.y )` applied to the base aim ray — cleaner than `WithAimCone` when you need asymmetric X/Y spread.

### D. Frame-table weapon state machine without an animgraph (ataco.sdoomresurrection)

When a weapon must match a sprite-sheet or HUD animation frame-by-frame (retro FPS, 2D sidebar weapon), drive state with a `switch(State)` tic counter instead of an animation graph. Each case sets the sprite, queues the next frame timer, and fires side-effects.

`sdoomresurrection/Code/weapon/DoomShotgun.cs` (condensed):

```csharp
enum WeaponState { Ready, Fire, Flash, Reload, Empty }
[Sync] WeaponState State;
TimeUntil NextFrame;

protected override void OnFixedUpdate() {
    if ( IsProxy || NextFrame > 0 ) return;
    switch ( State ) {
        case WeaponState.Fire:
            SetHudSprite( "SHTGA0" ); MuzzleFlash();
            FirePellets( 7, spreadX, spreadY );
            NextFrame = TicsToSeconds( 3 );
            State = WeaponState.Flash; break;
        case WeaponState.Flash:
            SetHudSprite( "SHTGB0" );
            NextFrame = TicsToSeconds( 7 );
            State = WeaponState.Reload; break;
        // ...
    }
}
```

Anti-pattern: using `Task.DelaySeconds` chains for frame pacing — they accumulate uncancellable continuations if the weapon is dropped mid-sequence.

### E. Combo cancel windows from a `WeaponDefinition` GameResource (aethercore.versus)

Hardcoding attack durations makes re-timing animations break the cancel system. Store combo durations and cancel windows as **normalized fractions** in a `[GameResource]` so re-exporting animations never requires code changes.

`versus/Code/Data/WeaponDefinition.cs` (key fields):

```csharp
[GameResource( "Weapon Definition", "weapon", "Melee weapon data" )]
public class WeaponDefinition : GameResource {
    public List<float> AttackDurations { get; set; }        // abs seconds per combo hit
    public List<float> AttackDamages { get; set; }
    public List<float> AttackCancelStartsNormalized { get; set; } // 0-1 fraction of clip
}

// At runtime, resolve to absolute cancel time:
float cancelStart = def.AttackCancelStartsNormalized[comboIndex] * def.AttackDurations[comboIndex];
// After elapsed >= cancelStart, open the cancel window for input-buffered chain/dodge-cancel
```

Input buffering: pressing the next attack during a swing sets `attackBuffer = bufferDuration`; the cancel window polls `attackBuffer > 0` and consumes it. Dodge-cancel has higher priority than combo-chain.

### F. Floating damage numbers via static pub/sub + `PointToScreenPixels` (aethercore.versus)

Decouple floating combat text from any specific HUD component. A static queue accepts world-space events; the HUD's `OnUpdate` projects and fades them.

`versus/Code/CombatEvents.cs` + `uicodes/PlayerHud.razor`:

```csharp
// Zero-dependency emitter — any combat code calls this
public static class CombatEvents {
    record DamagePopup( Vector3 WorldPos, float Amount, string Type, RealTimeSince Age );
    static List<DamagePopup> _popups = new();
    public static void AddPopup( Vector3 pos, float amount, string type )
        => _popups.Add( new( pos, amount, type, 0 ) );
}

// In HUD OnUpdate (Razor panel):
foreach ( var p in CombatEvents.Popups ) {
    var screen = Scene.Camera.PointToScreenPixels( p.WorldPos );
    // render at screen + offset by age, fade alpha by age
}
```

Anti-pattern: passing a UI reference into combat code — creates circular dependencies and breaks when the HUD is rebuilt.

### G. Per-recipient outline via ghost-clone + `Rpc.FilterInclude` (despawn.murder)

To show a wallhack/radar outline only to specific players (radar buyer + dead spectators) without revealing it to others, clone the target's `SkinnedModelRenderer` into a tagged ghost and `NetworkSpawn` it with a restricted audience. The real model is untouched.

`murder/Code/Systems/EquipmentShop/Items/Radar.cs` (`RadarOutlineFactory`):

```csharp
void CreateOutlineFor( Connection buyer ) {
    var ghost = target.SkinnedModelRenderer.GameObject.Clone();
    ghost.Tags.Add( "outline" );
    ghost.Components.Create<HighlightOutline>(); // or equivalent tint/postfx
    ghost.NetworkSpawn();
    // only buyer + any dead spectators see the ghost
    using ( Rpc.FilterInclude( c => c == buyer || IsSpectator( c ) ) )
        ShowOutlineRpc( ghost );
}

[Rpc.Broadcast]
void ShowOutlineRpc( GameObject ghost ) { ghost.Enabled = true; }
```

Clean up by tag on radar expiry: `Scene.GetAllObjects().Where(o => o.Tags.Has("outline")).ToList().ForEach(o => o.Destroy())`.

Anti-pattern: recoloring the real player's renderer — visible to everyone.

### H. `[Rpc.Host]` purchase re-validation — price from ConVar, not the item (despawn.murder)

Never trust the client's claimed item price. Re-validate the full purchase server-side; read the price from a server ConVar so live rebalancing requires no asset rebuild.

`murder/Code/Systems/EquipmentShop/EquipmentShopManager.cs`:

```csharp
[Rpc.Host]
public void PurchaseHost( string itemKey ) {
    var caller = Rpc.Caller;
    if ( !_items.TryGetValue( itemKey, out var item ) ) return;
    if ( !item.IsEnabled ) return;
    var pawn = GetPawn( caller );
    if ( pawn is null || !item.CanPurchase( pawn ) ) return;
    int price = GameConVars.GetPowerupPrice( itemKey, fallback: 3 );  // from ConVar, not item
    if ( pawn.CluesCollected < price ) return;
    pawn.CluesCollected -= price;
    item.OnPurchase( pawn );
}
```

Anti-pattern: `price = item.Price` (client-authored field) — a cheater can call the RPC without paying.

---

### Updated "read these games" pointer

For weapon combat, hitscan, projectiles, melee, and combos, the most instructive codebases are:

| Game | Strength |
|---|---|
| `sandbox` / `simple-weapon-base` | Canonical base-class hierarchy, hitscan trace, physical projectile, cancellable reload |
| `sbox-grubs` | Radial explosion, ballistic arc prediction |
| `aethercore.versus` | Full melee kernel: anim-event damage windows, combo cancel windows (normalized GameResource), input buffering, `[Rpc.Owner]` damage routing, damage popups |
| `ataco.sdoomresurrection` | Penetrating hitscan (`RunAll`), frame-table weapon FSM, `IDamageable` via `GetAll` |
| `despawn.murder` | Per-recipient outlines (ghost clone + `Rpc.FilterInclude`), host re-validated shop purchases |
