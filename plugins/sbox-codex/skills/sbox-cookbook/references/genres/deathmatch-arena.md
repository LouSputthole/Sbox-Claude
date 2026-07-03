# Deathmatch / Arena-Combat Recipe

How to build a deathmatch-arena game in modern s&box (GameObject/Component/Scene), distilled from two mined games: `ataco.sdoomresurrection` (a from-scratch Doom engine — hitscan, carriable weapons, monster AI, custom movement) and `aethercore.versus` (a 1v1 host-authoritative soulslike fighter — round state machine, melee damage windows, trade-bug-hardened hit resolution).

## What defines the genre

A deathmatch arena is a **kill-driven combat loop in a bounded space**: actors spawn, fight with hitscan/projectile/melee, deal damage through a shared `IDamageable` contract, die, and respawn (or the round resets and rescores). There is no economy or persistence core — the genre *is* the combat exchange plus the match scaffolding that wraps it.

Two sub-shapes appear:

- **Arena FPS / horde** (`sdoomresurrection`): a carriable-weapon player fights AI monsters or other players with hitscan + projectiles. Most logic is host/local-authoritative; networking is light. Doom reimplements *everything* by hand (movement, hitscan, AI, even the use-key), which makes it the best teaching corpus for the raw mechanics even though it leans single-player.
- **Versus duel** (`aethercore.versus`): a tightly-networked 1v1 melee fighter built on a `[Sync]` round state machine, with damage delivered by **animation-driven hitbox windows** and resolved defensively on the victim. This is the shape to copy when the match itself is networked and authoritative.

**Core loop:** `spawn → acquire/ready a weapon → aim & fire → trace hits an IDamageable → apply DamageInfo → death → frag/score → respawn or round-reset`. Everything else (match FSM, HUD, spawns, pickups) is scaffolding around that exchange.

## The system stack to compose

Build these as separate components. References point to existing system docs.

| System | Role | Reference |
|---|---|---|
| `IDamageable` damage contract | One-method interface every hurtable thing implements | — (below) |
| Health + death/respawn | Per-actor HP, `OnKilled`, frag credit | `references/systems/progression-upgrades.md` (stats/scoring seam) |
| Weapon / carriable framework | Equip, holster, fire FSM, ammo | `references/systems/inventory.md` |
| Hitscan & projectile resolution | `Scene.Trace.Ray/Box` + spread → DamageInfo | — (below) |
| Melee damage window | Animation-gated hitbox collider | — (below) |
| Player movement | `PlayerController` (default) or hand-rolled swept-BBox | — (below) |
| Match / round state machine | `[Sync]` FSM: countdown → fight → score → reset | `references/systems/round-match.md` |
| Spawn / respawn points + waves | Pick a spawn, network-spawn, AI horde | `references/systems/spawning-waves.md` |
| Pickups & use-prompt | Touch-overlap + manual `IUse` eye-trace | `references/systems/inventory.md` |
| AI opponents (optional) | State-machine monster/bot | `references/systems/spawning-waves.md` |
| Leaderboard / stats push | Frags, win streak → backend | `references/systems/leaderboards-services.md` |

## The two authority idioms that make it work

**Pick one per system.** Doom is *local/host-implicit* (movement & AI aren't synced — fine for single-player/host horde). Aethercore is *host-authoritative with owner-resolved defense* (the shape for real PvP).

1. **Host-authoritative match, fighters as `[Sync] GameObject`.** Store actor refs as synced `GameObject` properties (not component fields) so they survive host migration; gate the FSM tick to the non-proxy.

```csharp
[Sync] public MatchState State { get; set; }
[Sync] public int MatchWinner { get; set; } = -1;
// Store fighters as synced GameObjects — fields go null on the new host after migration.
[Sync] public GameObject ActivePlayer1Obj { get; set; }
PlayerController P1 => ActivePlayer1Obj?.GetComponent<PlayerController>();

protected override void OnUpdate()
{
    if ( IsProxy ) return;                 // host-only simulation
    switch ( State ) { /* RoundIntro → Countdown → Fighting → RoundEnded → MatchEnded */ }
}
```
(aethercore.versus: Code/ArenaManager.cs:30-49 sync fighters + the migration comment, :63-65 IsProxy-gated FSM, :51 MatchState enum)

2. **Resolve defense on the victim's owner.** The attacker's trace/collider only *reports* the hit; the parry/block/i-frame check runs where the defender's non-synced state lives. Aethercore routes hits to the victim via an owner RPC; this avoids trusting attacker-side timing.

```csharp
void Component.ITriggerListener.OnTriggerEnter( Collider other )
{
    if ( !isActive ) return;                         // damage window closed
    var hit = other.GameObject;
    if ( hit is null || hit.Root == GameObject.Root ) return;   // no self-hit
    if ( GameObject.Root.IsProxy ) return;           // only the attacker's owner runs this
    // Attacker-validity gate (trade-bug fix): if the owner got flinched/parried, the
    // hit_end keyframe that closes the window may never fire — verify attacker STATE,
    // not collider state, before applying damage.
    var owner = GameObject.Root.GetComponent<PlayerController>();
    bool inAttack    = owner.IsAttacking || owner.IsHeavyAttacking;
    bool interrupted = owner.IsGuardBroken || owner.IsParrying || (owner.Health?.IsHit ?? false);
    if ( !inAttack || interrupted ) return;
    // ...route to victim owner to resolve block/parry/i-frames, then apply.
}
```
(aethercore.versus: Code/WeaponDamage.cs:56-83 the full trade-bug-hardened gate; :44 `EffectiveShieldDamage` = optional shield-vs-body split)

## Build order

Build single-player first; the damage contract and weapon FSM are identical offline, and the `IsProxy`/`IsHost` short-circuits make the PvP path additive.

1. **`IDamageable` + `DamageInfo`.** One interface, `void TakeDamage(DamageInfo)`. Every hurtable component implements it. This is the seam the whole genre hangs on — do it first.
2. **Health + death.** HP field, armor absorb, `OnKilled` (obituary, drop inventory, respawn timer). Credit the frag to `info.Attacker`.
3. **Player movement.** Use the stock `PlayerController` unless you need bespoke feel (Doom hand-rolls it — see below).
4. **Weapon framework.** Carriable components, equip/holster, a per-weapon fire FSM in fixed ticks.
5. **Hitscan / projectile.** Trace a ray (with spread) or spawn a projectile; on hit, build a `DamageInfo` and apply to every `IDamageable` on the hit object *and its parents*.
6. **Melee window (if applicable).** A collider on the weapon, enabled only during the attack animation's active frames, resolved on the victim's owner.
7. **Pickups + use.** Touch-overlap for run-over pickups; a manual eye-trace for `IUse` (the old `TickPlayerUse` is gone).
8. **Match FSM.** `[Sync]` round state machine: spawn fighters → countdown → fight → detect round-over → score → reset/respawn → match-over.
9. **AI opponents (optional).** A state-machine monster/bot that targets, chases, and fires.
10. **HUD, spawns, leaderboard push.**

## How the real games do each piece

### Damage contract — one interface, trace to parents
`IDamageable` is a single-method interface (`void TakeDamage(DamageInfo)`). Hitscan applies to **every** `IDamageable` found with `FindMode.EverythingInSelfAndParent`, so a collider on a child still damages the actor root. Build the `DamageInfo` with the fluent helpers and stamp the attacker/weapon for obituaries.

```csharp
var dmg = DamageInfo.FromBullet( tr.EndPosition, forward * 100 * force, damage )
    .UsingTraceResult( tr ).WithAttacker( Owner ).WithWeapon( GameObject );
foreach ( var id in tr.GameObject.Components.GetAll<IDamageable>( FindMode.EverythingInSelfAndParent ) )
    id.TakeDamage( dmg );
```
(sdoomresurrection: Code/weapon/Weapon.cs:73-79 build+apply; :55 cached `Surface`). The victim's `TakeDamage` does armor absorption, knockback (`Velocity += info.Force/100f`), pain/death sounds, and `OnKilled` → obituary + drop inventory (sdoomresurrection: Code/pawn/DoomPlayer.cs:289-332).

### Hitscan — trace ray with spread, multi-result
Rotate the fire direction by a random yaw/pitch within the spread cone, trace with the right tag filter, ignore the shooter's hierarchy, and iterate results (so pellets can pass through / multi-hit). `ShootBullets(n, ...)` just loops `ShootBullet` with `force/n`.

```csharp
forward *= Rotation.FromYaw( (RNG.NextDouble()*2-1) * spread.x )
         * Rotation.FromPitch( (RNG.NextDouble()*2-1) * spread.y );
var tr = Scene.Trace.Ray( pos, pos + forward.Normal * 5000 )
    .WithAnyTags( "monster", "blocking", "player", "bulletclip" )
    .IgnoreGameObjectHierarchy( Owner ).Size( bulletSize ).Run();
```
(sdoomresurrection: Code/weapon/Weapon.cs:40-50 `TraceBullet`, :56-86 `ShootBullet`, :100-108 `ShootBullets`). **Your colliders must carry the traced tags** or shots pass through them. For projectiles, spawn a moving GameObject that traces forward each tick and calls the same `DamageInfo` path on impact.

### Weapon framework — reparent-to-owner carriable Components
Weapons are full `Component`s on their own `GameObject`. Equipping **reparents** the weapon under the player and toggles its `ModelRenderer.Enabled`; holster/equip fire `OnHolster`/`OnEquip`. A generic factory spawns them; a name→type switch rehydrates for save/load. Ammo lives on the *player*, not the weapon.

```csharp
public static T Create<T>( Scene scene ) where T : Weapon, new()
{
    var go = new GameObject { Parent = scene };
    return go.AddComponent<T>();
}
// Inventory.Add reparents under the player and hides the renderer until active;
// SetActive() calls OnHolster on the old, OnEquip on the new.
```
(sdoomresurrection: Code/weapon/Weapon.cs:117-122 `Create<T>`, :124-135 `CreateFromName` switch; Code/weapon/Inventory.cs:60 Add/reparent, :31 SetActive). The per-weapon fire/idle timing is a small frame FSM ticked in 35-fps Doom units (Code/weapon/DoomGun.cs:20). **Gotcha:** `CreateFromName` is a hardcoded string→type switch — update it whenever you add a weapon, or save/load throws.

### Melee — animation-gated damage window
For melee, don't trace — enable a hitbox `Collider` only during the attack's active frames (opened/closed by animation events), and implement `Component.ITriggerListener`. Resolve on the attacker's owner, gate on real attacker state (see idiom #2 above), and dedupe by `Root` so one swing hits each target once. Aethercore also keys faction so you don't friendly-fire, and supports an optional shield-vs-body damage split (aethercore.versus: Code/WeaponDamage.cs:40-83).

### Player movement — stock controller vs. hand-rolled swept BBox
Default path: drop a `PlayerController` and read input — see the spawn recipe in `references/systems/` / the `sbox-build-feature` skill. Doom instead hand-rolls a kinematic controller for exact retro feel: each `OnFixedUpdate` it builds a "short man" BBox (full hull minus `StepHeight`), box-traces the move, deflects velocity along the hit normal and re-traces up to 3× (slide), then traces down to step, then a stationary box-trace confirms it can stand before committing.

```csharp
var shortMan = new BBox( ply.Hull.Mins, ply.Hull.Maxs.WithZ( ply.Hull.Maxs.z - StepHeight ) );
var trace = Scene.Trace.Box( shortMan, start + Vector3.Up*StepHeight, end + Vector3.Up*StepHeight )
    .WithAnyTags( "blocking","monster","player","playerclip" )
    .IgnoreGameObjectHierarchy( GameObject ).Run();
while ( trace.Hit && fractionLeft > 0.01f && steps++ < 3 ) {     // slide
    Velocity -= trace.Normal * Velocity.Dot( trace.Normal );      // deflect along surface
    trace = Scene.Trace.Box( shortMan, trace.EndPosition, trace.EndPosition + Velocity*dt*fractionLeft )... .Run();
}
```
(sdoomresurrection: Code/pawn/DoomController.cs:22-118 full algorithm; :49 ground box-trace, :85 shortMan, :89-97 slide loop, :99-111 step-down + stand check). **Gotchas:** position/velocity are *not* `[Sync]`'d (single-player movement); your world colliders **must** be tagged or you fall through; constants are tuned to Doom's fixed-point thrust + 35-tic delta, so re-tune if you reuse it. (Note: Doom uses `MathF` — the *game* sandbox allows it; the bridge *editor* addon does not. Use `MathX`/`System.Math` in editor-side code.)

### Pickups & the use-prompt (TickPlayerUse is gone)
Two pickup paths, used belt-and-suspenders: a per-frame box-overlap on `"weapon"`-tagged things **and** `OnTriggerEnter` both forward to `thing.OnTouched(player)`. The old `TickPlayerUse` was removed, so the player **manually** re-implements use: on `Input.Pressed("Use")`, eye-trace 48u forward and call `OnUse` on every `IUse` on the hit object — this manual eye-trace is the current correct pattern.

```csharp
if ( Input.Pressed( "Use" ) ) {
    var tr = Scene.Trace.Ray( EyePosition, EyePosition + EyeRotation.Forward * 48f )
        .IgnoreGameObjectHierarchy( GameObject ).Run();
    if ( tr.Hit )
        foreach ( var use in tr.GameObject.Components.GetAll<IUse>().ToList() )
            use.OnUse( this );
}
```
(sdoomresurrection: Code/pawn/DoomPlayer.cs:262-282 overlap + manual use-trace, :334-339 `OnTriggerEnter`; Code/IUse.cs:3 the interface; Code/entities/things/Medkit.cs:12 `OnTouched` gates on `health<100`, mutates, plays a sound, `Destroy()`s). **Gotcha:** overlap + trigger can double-fire the same pickup — guard with `IsValid()`/`Destroy()` so it can't be grabbed twice.

### AI opponents — IEnumerator-as-state-machine
Doom's standout pattern: each monster AI "state" is a method returning `IEnumerator` where **every `yield return null` is one logical decision step**, not a delay. `OnFixedUpdate` advances a frame timer; when the current animation finishes, `StepState()` pumps `MoveNext()`. Subclasses override only the states (`StateSee`/`StateMissile`/`StateMelee`/`StateDeath`...) and virtual stats they change. Swapping state nulls the handler so a stale enumerator can't keep running.

```csharp
private void StepState() {
    if ( !GetStateHandler().MoveNext() ) { StateHandler = null; GetStateHandler(); } // rebuild on finish
}
public void SetState( MonsterState s ) {
    if ( currentState != s ) { StateHandler = null; animationSteps = ""; nextFrame = Time.Now; }
    currentState = s; GetStateHandler();
}
```
(sdoomresurrection: Code/entities/monsters/Monster.cs:47-65 OnFixedUpdate frame timer, :67-74 StepState/MoveNext, :88-100 GetStateHandler switch, :77-86 SetState; Code/entities/monsters/Imp.cs:28 per-monster override). This is an allocation-light alternative to a giant `switch`+enum-tick for any NPC/bot. **Gotcha:** it's driven off `OnFixedUpdate` and is *not* synced — host/single-player only; networked clients would desync the AI.

### Match / round state machine
A `[Sync]` enum FSM owned by the host (`if (IsProxy) return;`) walking `WaitingForPlayers → RoundIntro → Countdown → Fighting → RoundEnded → MatchEnded`, with a `[Sync] StateTimer` counted down per tick and `[Sync] MatchWinner`. Detect round-over by reading the synced fighter HP; on round end, increment score and reset; on match end, declare a winner and teleport to lobby (aethercore.versus: Code/ArenaManager.cs:51-59 states, :63-90 tick). See `references/systems/round-match.md` for the generic round/timer/scoring skeleton.

## Pitfalls (from the mined code)

- **Store networked fighters as `[Sync] GameObject`, never component fields** — fields go null on the new host after migration and the round-over check loops forever (aethercore comment at ArenaManager.cs:33-36).
- **Resolve defense on the victim's owner, and gate damage on attacker *state* not collider state** — a flinch/parry can skip the `hit_end` keyframe and leave a damage collider stuck open, causing unfair "trades" (WeaponDamage.cs:67-83).
- **Tag your colliders** with whatever the traces filter on (`blocking`/`player`/`monster`/`bulletclip`/`weapon`) — Doom's movement *and* hitscan both `WithAnyTags(...)`, so untagged geometry is invisible to them.
- **Apply damage to `FindMode.EverythingInSelfAndParent`** — put `IDamageable` on the root or an ancestor of whatever the trace hits, or hits land on nothing.
- **`TickPlayerUse` no longer exists** — re-implement use with a manual eye-trace; don't search training data for the old API.
- **Doom's movement/AI aren't `[Sync]`'d** — it's effectively single-player/host-driven. For real PvP, sync position/velocity (or use the stock networked `PlayerController`) and keep AI host-authoritative.
- **`CreateFromName`/save-load weapon switch is hand-maintained** — adding a weapon means editing the string→type map too, or rehydrate throws.
- **Overlap + trigger pickups double-fire** — guard the grant with `IsValid()`/`Destroy()`.
- **`MathF` exists in the game sandbox but not the bridge editor addon** — use `MathX`/`System.Math` for any editor-side code.

## Verify live

API surfaces drift between SDK versions — confirm before relying on a signature. Use `describe_type` / `search_types` reflection against the installed SDK as authoritative for: `DamageInfo` (`FromBullet`/`UsingTraceResult`/`WithAttacker`/`WithWeapon`), `Scene.Trace.Ray`/`Scene.Trace.Box` (`.WithAnyTags`/`.IgnoreGameObjectHierarchy`/`.Size`/`.Run`), `SceneTraceResult` (`Hit`/`Normal`/`Fraction`/`EndPosition`/`StartedSolid`), `Component.ITriggerListener` (`OnTriggerEnter`/`Exit`), `[Sync]`/`SyncFlags`/`IsProxy`/`[Rpc.Owner]`/`[Rpc.Host]`, `FindMode.EverythingInSelfAndParent`, `Components.GetAll<T>(FindMode)`, `PlayerController`, `Input.Pressed`/`AnalogMove`, and `BBox`/`Rotation.FromYaw`/`FromPitch`.

Cross-links: see the `sbox-api` skill for authoritative type lookups, and the `sbox-build-feature` skill for the screenshot-driven build/iterate loop.

## Corpus refresh (2026): more reference implementations

Five more mined games push the genre past the original two. The headline combat lessons that are net-new here: a **component-per-phase round FSM that survives host migration**, **identity-keyed combat state** (don't network the upgrade objects), **whitelist-synced stats** (don't sync all 250), the **owner-gated shared-trigger** idiom every multiplayer pickup gets wrong, a **killfeed + match-timeline ("hero of the round")**, **per-recipient wallhack outlines**, and **runtime-added prop health** so a build phase's props become destructible in battle. Pull the round/leaderboard/director scaffolds from these even when you keep Doom's combat or Aethercore's melee.

### Round FSM as component-per-phase (alternative to one enum switch)
`despawn.murder` and `barrelproto.ragroll` both improve on the single-enum FSM in idiom #1: each phase is its **own `Component`** with `Begin()/Tick()/Finish()` + `OnTimeUp`, and a manager ticks the active one host-only. Murder adds the crucial wrinkle — the manager raises round-start/end **locally on host AND via a host-only broadcast RPC**, so client-side systems (HUD, audio, kill feed) react to the exact same events without each one re-deriving state from `[Sync]`.

```csharp
// despawn.murder: Systems/Rounds/RoundManager.cs — transition + mirror so clients re-raise
public void TransitionTo<T>( Action<T> init ) where T : RoundState {
    State?.Finish();
    State = States.OfType<T>().First(); init( (T)State ); State.Begin();
    IRoundStateEvents.Post( x => x.OnRoundStateBegin( State ) );        // host-local
    BroadcastStateBegin( StateIndex );                                  // [Rpc.Broadcast(HostOnly)] → clients re-raise
}
```
(despawn.murder: Systems/Rounds/RoundManager.cs `TransitionNext`/`TransitionTo`; Systems/Rounds/RoundState.cs the `Begin/Tick/Finish/OnTimeUp` base with `[Sync(SyncFlags.FromHost)] TimeUntil TimeLeft`. barrelproto.ragroll: Code/mode/RollMode.cs `IGameMode` + `[Sync,Change] RagRollState` + `CanMove => state != Prepare` gating input from the FSM.) See `references/systems/round-match.md` for the generic skeleton; this is the **multi-phase, multi-system-react** upgrade to it.

### Host-migration-safe round timer (re-arm TimeUntil on the new host)
A `[Sync] TimeUntil` stores an *absolute* time off the **old** host's clock; after migration it points at the wrong instant. Fix: on becoming host, read the remaining seconds (`.Relative`) and re-arm against the new clock. Pairs with storing fighters/actors as `[Sync] GameObject` (existing pitfall).

```csharp
// despawn.murder: Systems/Rounds/RoundManager.cs::ValidateStateAfterMigration
var remaining = MathF.Max( State.TimeLeft.Relative, 0f );   // seconds left, old-host-relative
State.TimeLeft = remaining;                                  // re-arm on the new host's clock
```
(despawn.murder: RoundManager.cs `ValidateStateAfterMigration` — also resets a stale `State` ref to index -1 and skips a mid-PostRound migration to a fresh round.) For a *continuously* synced clock instead, `barrelproto.ragroll` Code/mode/networking/HostClock.cs broadcasts `[Sync] _hostTimestamp` every 0.4s, adds `Connection.Host.Ping*0.001f`, and only snaps if drift > 0.1s — a smooth shared clock for visible timers.

### Identity-keyed combat state — never network the upgrade objects
`facepunch.ss2` (a 300+-perk bullet-heaven) keeps every perk/weapon modifier as a **host-side `Dictionary<int,Perk>`** and syncs only `NetDictionary<int,int>` (typeIdentity→level). For the choice UI, the **host pre-renders** name/description/icon into parallel `[Sync] NetList<string>` so clients never run perk logic. `TypeDescription.Identity` ↔ `TypeLibrary.GetTypeByIdent` is the wire format. A client that only knows a type by identity has never run its static ctor, so call `TypeLibrary.GetType(t).Create<Perk>()` once purely to populate static display tables.

```csharp
// facepunch.ss2: Player.Perks.cs — sync identities + host-rendered strings, not objects
[Sync] public NetDictionary<int,int> SyncPerks { get; set; }           // typeIdentity → level
[Sync] public NetList<string> SyncCurrentPerkChoiceDisplayNames { get; set; }
public static int  TypeToIdentity( Type t ) => TypeLibrary.GetType(t).Identity;
public static Type IdentityToType( int id ) => TypeLibrary.GetTypeByIdent(id)?.TargetType;
```
(facepunch.ss2: Player.Perks.cs, PerkManager.cs, perks/Perk.cs `EnsureRegistered`.) Use this whenever loadouts/buffs/abilities are dozens of small classes — networking the *effect classes* is the trap.

### Whitelist-synced stats — don't replicate the whole stat table
Of ~250 `PlayerStat`s, ss2 mirrors only a hand-curated `_syncedStats` list to proxies via `[Sync] NetDictionary<PlayerStat,float>`; everything else stays host-authoritative and never goes on the wire. The UI reads `IsProxy ? GetSyncStat(s) : Stats[s]`.

```csharp
// facepunch.ss2: Player.Stats.cs — explicit bandwidth control
static readonly PlayerStat[] _syncedStats = { PlayerStat.Health, PlayerStat.MaxHp, /* …only what the HUD shows */ };
public float GetUiStat( PlayerStat s ) => IsProxy ? GetSyncStat(s) : Stats[s];
```
(facepunch.ss2: Player.Stats.cs.) Most s&box combat games sync far more than the HUD needs; pick the list deliberately.

### Owner-gated shared trigger — the multiplayer pickup idiom most code gets wrong
All player bodies overlap the same trigger/zone, so an `OnTriggerEnter` fires N times. `barrelproto.ragroll` resolves the entering collider up to its owning player and **bails unless it's the local owner**, so a collectible/score-zone registers once, for the right player. (This is the networked counterpart to the existing single-player "guard with `IsValid()`/`Destroy()`" note.)

```csharp
// barrelproto.ragroll: Code/.../Collectible.cs (and MovementTrigger) — shared trigger, local-only effect
void OnTriggerEnter( Collider other ) {
    var skate = other.GameObject.Root.GetComponent<PlayerRagdoll>()?.SkateOwner;
    if ( skate is null || !skate.Network.IsOwner ) return;   // only my body counts this overlap
    /* award / fire event locally */
}
```
Same game also guards combat integrity two more ways worth copying: a `[Sync]` score whose **setter early-returns unless `Owner.IsLocal()`** (clients only write their own score), and a **corrupted-connection guard** — `OnPlayerJoined`/`OnStart` try/catch reading `Network.Owner.DisplayName`, check `SteamId == default`, and host-destroy the half-joined object. (barrelproto.ragroll: Code/mode/RollMode.cs, NetworkPlayer.cs.)

### Killfeed + match-timeline → "hero of the round" / killcam
`despawn.murder` records every kill/clue/objective into a `RoundTimeline`, marks the **final kill**, and computes an MVP at round end; the post-round screen replays it. Critical sequencing: **copy the timeline out before `OnFinish()` destroys the phase component**, then ship it in the next state via `NetDictionary`/`NetList`. For the kill feed itself, both `despawn.murder` and `apl.sandboxwars` route deaths through an `IKillSource`-style event so HUD + scoring + obituary all subscribe.

```csharp
// despawn.murder: RoundManager.PreparePostRoundData — snapshot BEFORE Finish() nukes the component
var data = PreparePostRoundData( inProgress );   // copies timeline + roles out
TransitionTo<PostRoundState>( x => ApplyPostRoundData( x, data ) );
```
(despawn.murder: Systems/Rounds/RoundManager.cs, RoundTimeline.cs, `CalculateHeroPlayer`.) This is the generic "end-of-match summary + killcam feed" the original recipe lacked.

### Per-recipient wallhack outline (radar / spotted) via ghost clone
To highlight a target **only to certain players** (a radar buyer, spectators, your team), don't recolor the real renderer. Clone the target's `SkinnedModelRenderer` + bone-merged clothing into a tagged "Outline" ghost, add a `HighlightOutline`, and create it under `Rpc.FilterInclude(allowedConnections)` so only those clients spawn/see it; fade by alpha and clean up by tag.

```csharp
// despawn.murder: Systems/EquipmentShop/Items/Radar.cs::RadarOutlineFactory (sketch)
using ( Rpc.FilterInclude( buyer, spectators ) ) {                 // only these clients build the ghost
    var ghost = target.Clone(); ghost.Tags.Add("outline");
    ghost.GetComponent<SkinnedModelRenderer>(); ghost.AddComponent<HighlightOutline>();
}
```
(despawn.murder: Radar.cs.) Reusable for "spotted" markers, spectator ESP, ability highlights — any deathmatch where visibility is per-player.

### Data-key → spawned-behavior shop (in-round powerups)
`despawn.murder`'s equipment shop is a clean **string-key → component** dispatch: `[Rpc.Host] PurchaseHost(itemKey)` re-validates host-side (powerup enabled? caller has a pawn? `item.CanPurchase`?), runs `item.OnPurchase(pawn)`, then deducts the currency — and the **price comes from a ConVar, not the item**, so a server owner re-tunes the economy live. Many items are just `EquipmentShopPurchasableComponentItem<TComponent>` (attach a networked component).

```csharp
// despawn.murder: Systems/EquipmentShop/ItemComponentFactory.cs
static ShopItem Make( string key ) => key switch {
    "radar"  => new Radar(),  "swapper" => new Swapper(),
    "knife"  => new UpgradedMelee(), _ => null };
// EquipmentShopManager: [Rpc.Host] PurchaseHost re-validates, OnPurchase, then CluesCollected -= price
```
(despawn.murder: ItemComponentFactory.cs, EquipmentShopManager.cs, Items/*.cs; price via GameConVars.GetPowerupPrice.) See `references/systems/shop-vendor.md`; the deathmatch-specific bit is the **host-revalidated, currency-after-effect, per-owner-spawned** store.

### Runtime-added prop health — make build-phase props destructible in battle
`apl.sandboxwars` (a GMod-style build→battle) lets players freely spawn props during a Build phase, then on the Battle transition a host loop tags every `Rigidbody` that isn't a player/NPC/weapon with a `PropHealth` component — **so everything becomes destructible without touching the spawn path**. The same manager **heals all players to full** on phase change (a deliberate over-damage to force respawn) and **forces players off build tools** onto a real weapon.

```csharp
// apl.sandboxwars: Code/MiniGameManager.cs (host, every 2s) — universal prop destructibility
foreach ( var rb in Scene.GetAllComponents<Rigidbody>() ) {
    var go = rb.GameObject;
    if ( go.Tags.HasAny("player","npc","weapon") || go.GetComponent<PropHealth>() != null ) continue;
    go.AddComponent<PropHealth>();                       // now it takes DamageInfo + breaks
}
// ApplyBuildPhaseState heals to full; ApplyBattlePhaseState ForcePlayersOffTools()
```
(apl.sandboxwars: Code/MiniGameManager.cs `AddPropHealthToNewObjects`/`ApplyBuildPhaseState`/`ApplyBattlePhaseState`; deaths ragdoll + report via `IKillSource`.) Also a tidy **ragdoll-on-death** recipe: clone renderer + bone-merged clothing into a `ModelPhysics` object, apply the attacker's velocity, self-destruct after ~30s.

### Data-driven spawn director (alternative to scripted waves)
For horde/arena pressure, `facepunch.ss2` replaces scripted waves with a per-tick **weighted distribution**: each enemy has an `EnemySpawnConfig` (progress-curve weight + population cap + early-spawn/catch-up/late-game/threat multipliers, all per-difficulty arrays), hot-reloadable via `[ConCmd]`. `despawn.murder`'s Director paces *clue* spawns by **multiplying a base interval by N independent ~1.0 factors** (player-count, kill-inactivity, time-pressure, discovery-rate), clamped, then a per-map penalty. Either gives "handcrafted-feeling" pacing without authoring waves. (facepunch.ss2: Manager.Spawning.cs `EnemySpawnConfig`; despawn.murder: Systems/Rounds/RoundDirector/. See `references/systems/spawning-waves.md`.)

### Leaderboard score encoding for win/partial/loss on one board
`facepunch.ss2` packs three outcome types into **one numeric stat** so a single board sorts correctly: victory → `VICTORY_OFFSET(2_000_000) - elapsedTime` (faster = higher), boss-reached loss → `BOSS_DEFEAT_OFFSET + bossDamage%`, early death → raw `elapsedTime`; the UI decodes by range. The leaderboard version is **baked into the stat name** (`LEADERBOARD_VERSION=8`) so a balance change starts a fresh board. `barrelproto.ragroll` Code/mode/GlobalScores.cs is the matching read side: `Services.Leaderboards.GetFromStat(pkg, stat).FilterByWeek()`, 60s async refresh, `Texture.LoadAvatar(SteamId,32)` per entry, submit wrapped in `#if !DEBUG`. (facepunch.ss2: Manager.Stats.cs; barrelproto.ragroll: GlobalScores.cs, ModeScore.cs.) See `references/systems/leaderboards-services.md`.

### Read these games (in the corpus) for deathmatch/arena
- **`ataco.sdoomresurrection`** — hitscan, carriable weapons, hand-rolled swept-BBox movement, IEnumerator monster AI, the `IDamageable`+`DamageInfo` contract (covered in detail above).
- **`aethercore.versus`** — networked 1v1 melee: `[Sync]` round FSM, animation-gated hitbox windows, trade-bug-hardened victim-owner hit resolution (covered above).
- **`despawn.murder`** — combat round game done right: component-per-phase FSM + mirror RPC, host-migration-safe timer, killfeed/match-timeline/MVP, per-recipient wallhack outlines, host-revalidated in-round shop, pity-ticket role selection.
- **`facepunch.ss2`** — large-scale combat composition: identity-keyed networked state, whitelist-synced stats, data-driven spawn director, single-board multi-outcome score encoding, lifecycle event-bus fan-out for on-kill/on-hurt reactions.
- **`barrelproto.ragroll`** — multiplayer correctness primitives: owner-gated shared triggers, owner-gated score writes, corrupted-connection guard, ping-corrected `HostClock`, in-room scoreboard from `PlayerList`, swappable `IGameMode` + orphan-clear host migration.
- **`apl.sandboxwars`** — build→battle hybrid: runtime-added prop health for universal destructibility, heal-to-full + force-off-tools phase transitions, ragdoll-on-death, `IKillSource` kill feed.
