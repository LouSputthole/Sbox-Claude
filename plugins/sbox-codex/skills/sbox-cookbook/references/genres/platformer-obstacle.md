# Platformer / Obstacle-Course Recipe

How to build a precision-climber or obstacle-course game in modern s&box (GameObject/Component/Scene), distilled from two mined games: `facepunch.jumper` (Jump King-style vertical climber) and `yellowletter.terrys_crash_course` (2.5D time-trial obstacle course).

## What defines the genre

A platformer-obstacle game is **a skill-gated traversal loop against a fixed course**. Two flavors dominate, and they share most of their stack:

- **Precision climber** (jumper): one expressive movement verb (a charge-jump), unforgiving fall/bounce physics, height-above-start as score, persistent progress. Failure is *positional* — you fall back down.
- **Obstacle-course / time-trial** (crash_course): a fixed run from start to finish line against a countdown timer, dodging *self-resetting hazards*, racing your personal best. Failure is *temporal* — your time is worse.

**Core loop (both):** spawn at start -> attempt traversal using a tight movement verb -> hit hazards/gaps that punish mistakes -> reach a finish/height goal -> record the result (height/time) to disk or to s&box Services -> retry to beat it. The whole thing is **single-player-shaped even when networked** — both games gate logic behind `IsProxy` and use `[Sync]` mostly for shared presence (a height bar) rather than authoritative simulation (jumper/RaceTimerSystem.cs: all logic behind `if (IsProxy) return`).

## The system stack to compose

Pick from these. The first three are the genre spine; the rest are course flavor and meta.

1. **Movement verb** — the one mechanic the whole game tests. Climber = charge-jump; course-runner = a kinematic run/jump controller. Build on the stock `PlayerController` + a custom `Sandbox.Movement.MoveMode` subclass rather than rewriting the controller (jumper/Code/Player/JumperWalkCustom.cs:7). See **references/systems/player-controller.md**.
2. **Failure physics** — wall-bounce reflection (climber) or knockback/respawn (course). Bypasses controller collision response by writing `Body.Velocity` directly (jumper/Code/Player/JumperControllerInput.cs:199).
3. **Goal + scoring** — a finish-line trigger or height-above-start. `Component.ITriggerListener` zones for the finish (jumper/Code/GamePlay/JumperFinishLine.cs:14); a `[Sync]`'d Height ticked each fixed update (jumper/Code/Player/JumperPlayerStuff.cs). See **references/systems/trigger-zones.md**.
4. **Self-resetting hazard library** — every obstacle captures its start pose in `OnStart` and exposes `ResetToStart()`; a manager sweeps them between runs for deterministic retries (terrys_crash_course/Code/.../LevelStateManager.cs:251).
5. **Run/race state machine** — countdown -> race -> finish, with a movement lock (terrys_crash_course/Code/CrashCourse/RaceTimerSystem.cs:144). A string-keyed `MenuStateController` drives UI/music/input lock off one `State` property (MenuStateController.cs:16).
6. **Persistence + meta** — climber writes JSON to `FileSystem.Data`; course-runner uses `Sandbox.Services` (Stats=PB, Leaderboards, Achievements=medals). See **references/systems/persistence.md** and **references/systems/services-backend.md**.
7. **Manager + spawn** — a `GameObjectSystem<T>` for host/network glue and player spawn (jumper/Code/GamePlay/GameManager.cs:1). See **references/systems/game-manager.md**.
8. **Follow camera** — third-person follow with obstruction pull-in (jumper/Code/Camera/JumperCamera.cs:26). See **references/systems/camera.md**.

## Build order

1. **Manager + spawn first.** A `GameObjectSystem<GameManager>` implementing `ISceneStartup.OnHostInitialize` (create a lobby if none) and `INetworkListener.OnActive` (spawn). Spawn with `Clone(StartEnabled=false)` then `NetworkSpawn(owner)` so the transform is correct before components run (jumper/Code/GamePlay/GameManager.cs:20).
2. **Movement verb + camera.** Get the charge-jump (or run controller) feeling right in an empty box before any course exists. This is 80% of the game; tune it longest.
3. **Failure physics.** Add wall-bounce / fall handling so missing a jump *means* something.
4. **One hazard + the reset contract.** Build a single hazard with `ResetToStart()` and a manager reset sweep. Get the retry loop deterministic before adding eleven more hazards.
5. **Goal + scoring trigger.** Finish line or height tracking; wire it to record a result.
6. **Persistence + meta.** JSON save (climber) or Services Stats/Leaderboards/medals (course). Add last — it's plumbing, not feel.

## How the real games do it

### Charge-jump (the climber's whole game) — jumper/Code/Player/JumperControllerInput.cs:313

Holding `jump` while grounded accumulates charge, **zeroes `Body.Velocity`** (you can't walk while charging), and feeds the same normalized `alpha` into the animator's duck level — so the visible crouch wind-up *is* the charge meter (jumper/.../JumperControllerInput.cs:324). On release it quantizes the charge to 0.1 steps (makes jumps discrete and learnable), launches, and crucially calls `PreventGrounding` or the controller re-grounds the same tick and eats the launch:

```csharp
public void TryJump()
{
    if ( Input.Down( "reload" ) ) { TimeSinceJumpDown = 0; Animator.DuckLevel = 0; return; } // cancel

    if ( Input.Down( "jump" ) && CanJump )
    {
        TimeSinceJumpDown += Time.Delta;
        var alpha = TimeSinceJumpDown.LerpInverse( 0, TimeUntilMaxJump ); // 2s to max
        Controller.Body.Velocity = 0;          // no walking while charging
        Animator.DuckLevel = alpha;            // crouch == charge meter
    }

    var jumpAlpha = TimeSinceJumpDown / TimeUntilMaxJump;
    if ( jumpAlpha >= 1 || (!Input.Down( "jump" ) && jumpAlpha > 0) )
    {
        TimeSinceJumpDown = 0;
        jumpAlpha = ((int)(Math.Min( 0.4f + jumpAlpha, 1f ) * 10f)) / 10f; // quantize to 0.1
        var vel = Input.AnalogMove.Length > 0.01f
            ? Animator.Renderer.WorldRotation.Forward.WithZ( 0 ).Normal * (jumpAlpha * MaxJumpStrength * 0.5f)
            : Vector3.Zero;
        Controller.Body.Velocity = vel.WithZ( jumpAlpha * MaxJumpStrength ); // 885 max
        Controller.PreventGrounding( 0.1f );   // REQUIRED or the launch is eaten
    }
}
```

Gotcha: charging hard-zeroes `WishVelocity` AND `Body.Velocity` every tick, so wind/external forces are cancelled mid-charge (jumper/.../JumperControllerInput.cs:324-330).

### Wall-bounce reflection — jumper/Code/Player/JumperControllerInput.cs:199

When airborne, BBox-trace one tick ahead along velocity, classify the hit normal by angle vs `Up`, and for walls reflect elastically. This bypasses the controller's own collision response by writing `Body.Velocity` directly:

```csharp
var n = tr.Normal.Normal;
float angle = n.Angle( Vector3.Up );
if ( angle < 60f ) return;                       // ground: ignore
var into = Vector3.Dot( vel, n );
if ( into >= 0f ) return;                         // moving away: don't re-bounce
if ( angle <= 80f ) { Controller.Body.Velocity = ClipVelocity( vel, n, 1f ); return; } // slide
var reflected = vel - (1f + BounceBounciness) * into * n;  // e = 0.9 elastic reflect
Controller.Body.Velocity = reflected;
```

The `into >= 0` guard is what stops it re-bouncing off a surface you're already leaving (jumper/.../JumperControllerInput.cs:221-223). Tune `e` and the 60/80-degree bands per feel.

### Finish-line / hazard trigger zones — jumper/Code/GamePlay/JumperFinishLine.cs:14

Implement `Component.ITriggerListener` and gate on the player tag. Requires a `Collider` with `IsTrigger` and matching collision tags in the prefab:

```csharp
public sealed class JumperFinishLine : Component, Component.ITriggerListener
{
    void ITriggerListener.OnTriggerEnter( Collider other )
    {
        if ( !other.GameObject.Tags.Has( "player" ) ) return;
        var ui = other.GameObject.Parent.Components.Get<JumperEndUI>( FindMode.EnabledInSelfAndChildren );
        ui.Open = true;
    }
}
```

Gotcha: the player tag lives on a collider *child* while the gameplay components live on the parent/root — code climbs `.Parent` or `.Root` to find them, and jumper does it inconsistently between enter/exit (jumper/Code/FunStuff/JumperWindTunnel.cs). Pick one and be consistent.

**Crash Course idiom:** `OnTriggerEnter` only fires on *entry* — it misses bodies already overlapping when a hazard toggles on. Re-scan `collider.Touching` every `OnFixedUpdate` to catch them (terrys_crash_course/.../HammerComponent.cs:305).

### Self-resetting hazards for deterministic retries — terrys_crash_course

Each hazard captures its start pose and a manager sweeps them so every attempt is bit-identical:

```csharp
// on each hazard
protected override void OnStart() => _startLocal = LocalPosition;
public void ResetToStart() => LocalPosition = _startLocal;

// LevelStateManager.ResetCourseModules() — terrys_crash_course/...:251
foreach ( var h in Scene.GetAll<HammerComponent>() ) h.ResetToStart();
foreach ( var p in Scene.GetAll<MovingPlatform>() ) p.ResetToStart();
// ...one loop per hazard type
```

Timed hazards drive `LocalPosition` from a single accumulating `_time` plus a per-instance `TimeOffset` (pure math, no animgraph), so a stagger is one float. Missed abstraction worth fixing in your own build: there's no `IResettable` interface, so the manager hardcodes the type list (terrys_crash_course/.../LevelStateManager.cs:251).

### Countdown race timer + Services PB — terrys_crash_course/Code/CrashCourse/RaceTimerSystem.cs:236

`BeginCountdown` resets modules, teleports to spawn, locks movement; a per-second tick counts down; `StartRace` unlocks; `CurrentTime = timeSinceRaceStart.Relative` each update; `FinishRun` writes the PB only if better. The PB is written through `Sandbox.Services.Stats` and the **same stat key** backs the leaderboard and medal layer:

```csharp
public void FinishRun()
{
    RaceActive = false;
    CurrentTime = timeSinceRaceStart.Relative;
    SetMovementLocked( true );
    var statName = GetStatNameForLevel( LevelName );          // "race-time-{level}"
    if ( isFirstRun || CurrentTime < _runStartPersonalBestTime )
        Sandbox.Services.Stats.SetValue( statName, CurrentTime ); // lower = better (board uses Min)
    UnlockCompletionAchievements( completedLevel, CurrentTime ); // medal_gold/silver/bronze
}
```

Standout: **one deterministic key (`race-time-{level}`) is the PB key, the `Leaderboards.GetFromStat` key, AND the medal/progression source** — set board aggregation to `Min` for time (`Max` for progression) and your whole meta layer is one convention (terrys_crash_course/.../RaceTimerSystem.cs:495 + LevelLeaderboardService.cs:98). The backend board lags minutes behind a fresh run, so overlay the just-set local PB on the player's row until it catches up.

### Manager + spawn — jumper/Code/GamePlay/GameManager.cs:1

```csharp
public sealed partial class GameManager : GameObjectSystem<GameManager>,
    Component.INetworkListener, ISceneStartup
{
    public GameManager( Scene scene ) : base( scene ) { }
    void ISceneStartup.OnHostInitialize()
    {
        if ( !Networking.IsActive )
            Networking.CreateLobby( new Sandbox.Network.LobbyConfig() { MaxPlayers = 32 } );
    }
    void Component.INetworkListener.OnActive( Connection channel ) => SpawnPlayer( channel );
    public void SpawnPlayer( Connection owner )
    {
        var go = GameObject.Clone( "/prefabs/player/jumperplayer.prefab",
            new CloneConfig { StartEnabled = false, Transform = FindSpawnLocation() } );
        go.NetworkSpawn( owner ); // assigns ownership AFTER transform is set
    }
}
```

A `GameObjectSystem<T>` needs no GameObject and is the canonical place for global host/network glue. `StartEnabled=false` then `NetworkSpawn` is the order that spawns at the right transform before components tick.

## Reusable standout patterns

- **Gameplay state == animation value.** The charge `alpha` is the duck animgraph float — the model deforms continuously with charge, no separate HUD math (jumper/.../JumperControllerInput.cs:324).
- **Shared presence from `[Sync]` state + a Scene query, zero RPCs.** The height bar iterates `Scene.GetAllComponents<JumperPlayerStuff>()`, reads each `[Sync]`'d Height + `avatar:SteamId`, and draws opponents as markers — multiplayer presence for free (jumper/Code/UI/JumperHeightBar.razor:20).
- **Outcome-first, animation-cosmetic.** Decide the result up front, then ease the show toward a precomputed target (gacha strip in fishy; applies to any reward/result reveal).
- **VPCF-free particle bursts.** Confetti/gibs as hand-integrated `ModelRenderer`s — capture child renderers, give each a procedural velocity+spin, integrate gravity + a per-gib ground `Scene.Trace.Ray`, fade via `Tint` alpha. Sidesteps the particle editor entirely (terrys_crash_course/.../FinishComponent.cs:257) — valuable given the bridge's limited particle authoring.
- **Content-hash UI redraw.** Razor `BuildHash()` over a `State` string (or a `Version` int) so panels rebuild only on real change; some panels even fold an input toggle into `BuildHash` (`IsOpen ^= Input.Pressed("slot1")`) (jumper/Code/UI/JumperHeightBar.razor:61).

## Pitfalls

- Forgetting `PreventGrounding` after a launch — the controller re-grounds the same tick and eats it (jumper/.../JumperControllerInput.cs:354).
- Trusting `OnTriggerEnter` alone — re-scan `collider.Touching` each `OnFixedUpdate` for already-overlapping bodies (terrys_crash_course/.../HammerComponent.cs:305).
- `[Sync]` everything → chatty. jumper `[Sync]`s Height every fixed tick per player; fine at small scale, not for authoritative MP.
- Per-scene save filenames (`{Scene.Name}_progress.json`) orphan saves on a scene rename (jumper/Code/Player/JumperProgress.cs:17).
- `Vector3.SmoothDamped` is a struct — mutate `.Target`/`.SmoothTime`, then you must call `.Update(Time.Delta)` (jumper/Code/Player/JumperWalkCustom.cs).
- Achievement string keys must exist in the package's achievement config or `Unlock` silently no-ops.

## Verify live

API shifts between SDK builds — reflection is the source of truth, not this doc. Before coding, confirm signatures with the bridge: `describe_type` / `search_types` on `PlayerController`, `Sandbox.Movement.MoveMode`, `Component.ITriggerListener`, `GameObjectSystem`, `Sandbox.Services.Stats`, `Sandbox.Services.Leaderboards`, and `Sandbox.Network.LobbyConfig`.

Cross-links: pair this with **sbox-api** (look up exact type signatures via reflection before writing) and **sbox-build-feature** (the screenshot-driven iteration loop to actually build and verify it in-editor).

## Corpus refresh (2026): more reference implementations

Two of the four newly-mined games (`facepunch.jumper`, `yellowletter.terrys_crash_course`) are the originals this recipe was distilled from — already fully covered above. `stepdev.xtrem_road` is actually `stepdev.fishy` (a fishing/tycoon game; `Game.Ident` → `stepdev.fishy`) and contains no platformer-obstacle material. The net-new source is `alcoholics.nice_putt_idiot`, a physics-rigidbody rage-climber ("Getting Over It" meets mini-golf) that adds several techniques not yet covered.

### Physics-rigidbody as the player, not a character controller — nice_putt_idiot/Code/Pawns/GolfBall.cs

When your "player" IS a physics body (ball, cube, rag-doll), skip `PlayerController` entirely. The ball is a `Rigidbody`; input is applied via `ApplyImpulse`; movement-lock is `Velocity.Length > threshold`. The orthographic follow camera tracks only two axes for the 2.5D side view:

```csharp
public void Putt( Vector2 direction, float dragDistance, float maxDrag )
{
    if ( IsMoving ) return;  // can't putt while rolling
    var t = MathX.Clamp( dragDistance / maxDrag, 0f, 1f );
    var power = MinPower + t * (MaxPower - MinPower);
    // Y/Z plane only — X is locked for 2.5D
    Rigidbody.ApplyImpulse( new Vector3( 0, direction.x * power, direction.y * power ) );
    Client?.IncrementStrokes();
}
public bool IsMoving => Rigidbody.Velocity.Length > MaxVelocityForPutt;
```

Anti-pattern in source: `Math.Min` is used instead of `MathX.Clamp` — `System.Math` doesn't exist in the s&box sandbox; use `MathX.Clamp(value, 0f, 1f)`.

### Drag-to-aim via screen-space HUD, no InputAction — nice_putt_idiot/Code/UI/Hud.razor

The entire aim-and-fire mechanic is a `MousePanelEvent` on the HUD panel, not a world-space input action. Project the world object to screen, build a drag vector, draw immediate-mode, and on mouse-up call the launch method. This lets the "feel" live in UI code rather than in a movement component:

```csharp
// inside Razor panel: track drag start in OnMouseDown, call on OnMouseUp
var screenPos = Camera.PointToScreenPixels( golfBall.WorldPosition );
var dragVec   = e.LocalPosition - screenPos;       // screen-space vector
// draw aim line each frame while dragging
Scene.Camera.Hud.DrawLine( screenPos, e.LocalPosition, Color.Green.WithAlpha(0.8f), 2f );
if ( mouseUp ) golfBall.Putt( dragVec.Normal, dragVec.Length, MaxLineLength );
```

Reusable for: golf, slingshot, billiards, artillery, Angry Birds. The cookbook has no prior example of this pattern.

### Vignette transition gate (hide hard cuts behind a fade) — nice_putt_idiot/Code/Pawns/GolfBall.cs

Every disruptive operation (respawn, load-save, restart, teleport) routes through a 4-phase state machine that fades the screen out, runs the action at full black, then fades back in — hiding the hard cut. No coroutines; just a phase enum and `Time.Delta`:

```csharp
enum TransitionPhase { None, FadingIn, ExecutingAction, FadingOut }
TransitionPhase _phase; Action _pendingAction; float _vignetteAlpha;

public void StartVignetteTransition( Action action )
{
    _pendingAction = action; _phase = TransitionPhase.FadingIn;
}
void UpdateVignette()
{
    if ( _phase == TransitionPhase.FadingIn ) {
        _vignetteAlpha = MathX.Clamp( _vignetteAlpha + Time.Delta * 2f, 0f, 1f );
        if ( _vignetteAlpha >= 1f ) { _pendingAction?.Invoke(); _phase = TransitionPhase.FadingOut; }
    } else if ( _phase == TransitionPhase.FadingOut ) {
        _vignetteAlpha = MathX.Clamp( _vignetteAlpha - Time.Delta * 2f, 0f, 1f );
        if ( _vignetteAlpha <= 0f ) _phase = TransitionPhase.None;
    }
    // drive a Vignette post-process component's Intensity from _vignetteAlpha
}
```

Every teleport/respawn path in the game calls `StartVignetteTransition(...)`. Cross-genre utility.

### Hold-to-confirm with input consumption — nice_putt_idiot/Code/UI/RestartPrompt/RestartPrompt.razor

A hold-to-confirm bar (restart / quit / revive) with zero timers or tweens — just `TimeSince` + `Remap` + `Input.ReleaseAction` to prevent re-trigger on the still-held key:

```csharp
TimeSince _timeRestartHeld;
void OnUpdate()
{
    if ( Input.Down("Restart") && !ball.IsInTransition ) {
        BarWidth = MathX.Remap( (float)_timeRestartHeld, 0f, HoldDuration, 1f, 0f );
        if ( _timeRestartHeld > HoldDuration ) {
            Input.ReleaseAction( "Restart" );  // consume so it doesn't re-fire
            ball.RestartWithTransition();
        }
    } else {
        _timeRestartHeld = 0;  // reset on release
    }
}
```

### Services leaderboard with metadata columns — nice_putt_idiot/Code/GoalPoint.cs + UI/Leaderboard/Leaderboard.razor

The `nice_putt_idiot` leaderboard passes a metadata `Dictionary<string,object>` alongside the stat value, so the board shows stroke count and formatted time alongside the raw score — no custom backend:

```csharp
// on finish (GoalPoint.OnTriggerEnter, owner-only):
var meta = new Dictionary<string, object> {
    { "player_name", Connection.Local.DisplayName },
    { "stroke_count", client.StrokeCount },
    { "formatted_time", FormatTime( elapsed ) }
};
Services.Stats.SetValue( "best-time", elapsed, meta );
Services.Stats.Increment( "completions", 1 );
```

Leaderboard read pattern matches crash_course (`SetAggregationMin` + `SetSortAscending`) but only fetches 5 entries and diffs with `EntriesAreEqual` before calling `StateHasChanged()` to prevent Razor thrash at a 60s poll interval.

Anti-pattern: stat writes happen on the client side from `OnTriggerEnter` with no host validation. A spoofed client can submit any time. For competitive games, route the stat write through `[Rpc.Host]` with server-side time verification.

### Binary save with "ball at rest" gate — nice_putt_idiot/Code/SaveManager.cs

An alternative to JSON saves: positional `BinaryWriter`/`BinaryReader` to `FileSystem.Data`. Gate autosave on the physics body being still (avoids persisting a mid-fall state), with a minimum interval:

```csharp
// SaveManager.OnUpdate — only if local owner:
if ( !ball.IsMoving && !IsProxy ) {
    if ( _wasBallMoving ) { _ballStoppedTime = 0; _wasBallMoving = false; }
    if ( _ballStoppedTime > 0.5f && _lastSaveTime > 10f )
        { SavePlayerProgress(); _lastSaveTime = 0; }
} else _wasBallMoving = true;
```

**Anti-pattern in source worth fixing:** the binary format has no version byte or magic header. Any future field addition or reorder silently corrupts old saves — the only guard is a try/catch returning null. Fix: prefix with a `byte version` and wrap reads in a `switch(version)` migration block. For most obstacle-course games JSON saves (the jumper pattern) are safer and easier to migrate.

### No-checkpoint design as an intentional genre choice — nice_putt_idiot/Code/Pawns/Base/Client.cs

`nice_putt_idiot` has no checkpoints by design — a fall sends you back to the start, `TimeSince RunStartTime` accumulates from spawn, and `ResetRun()` restarts both time and stroke count. The only persistence is your *saved position* (so you resume where you left off across sessions, but mid-run you fall to zero). This is the "Getting Over It" philosophy: the punishment IS the game. Contrast with crash_course's checkpoint system (`CheckpointComponent.SetActiveVisualState`, gibs, model bodygroup) — add checkpoints when frustration tolerance matters; omit them when the fall is the mechanic.

### Proxy nametag via WorldPanel + `avatar:` URL — nice_putt_idiot/Code/Pawns/GolfBall.cs

For co-presence "ghost" multiplayer (every player sees others but runs their own game independently), proxy balls are dimmed to 30% alpha and get a floating nametag spawned in `OnStart`:

```csharp
if ( IsProxy ) {
    _model.Tint = Color.White.WithAlpha( 0.3f );
    _outline.Width = 0f;  // no "puttable" highlight for proxies
    var tag = GameObject.Components.Create<GolfBallTag>();  // WorldPanel + avatar + name
    // GolfBallTag.OnUpdate: WorldPosition = ball.WorldPosition + Vector3.Up * offset
}
```

The nametag component positions itself each `OnUpdate` — no parenting needed (avoids physics body transform interference).

### Updated "read these games" pointer

For this genre, the primary references remain `facepunch.jumper` (charge-jump climber; height scoring; save-restore-on-load; GameObjectSystem manager) and `yellowletter.terrys_crash_course` (time-trial; self-resetting hazards; additive level streaming; medal tiers; crossfade music; Services leaderboard with friends filter + self-row). Add `alcoholics.nice_putt_idiot` for: physics-rigidbody-as-player, drag-to-aim from screen space, vignette-gated transitions, hold-to-confirm UX, Services metadata columns, binary save with physics-rest gate, and the no-checkpoint rage-climber design pattern. The `stepdev.xtrem_road` package is a fishing/tycoon game (`stepdev.fishy`) with no platformer-obstacle content — do not cite it for this genre.
