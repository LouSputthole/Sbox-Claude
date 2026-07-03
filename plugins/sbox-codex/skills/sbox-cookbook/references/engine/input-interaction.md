# Input & Interaction

Reading player input in s&box: named actions, the `IPressable` use-flow, designer-editable bindings, analog feel, and routing one player's input into vehicles/turrets/possessed objects.

## Mental model

- **Input is polled per-frame by NAME, never by raw key.** You declare named actions (with a keyboard *and* a gamepad binding) once, then ask `Input.Down("jump")`. Hardcoding `KeyboardCode` in gameplay breaks rebinding and gamepads and is the #1 anti-pattern here.
- **Two layers of "interaction":** the *mechanical* layer is `Component.IPressable` (the engine raycasts and calls `CanPress`/`Press`/`Release`). The *presentation* layer is a project interface you define (e.g. `IContextualObject`) that the HUD scans to draw "Press E to ...". Keep them separate.
- **Authority still applies.** Any input that drives an `[Rpc.*]` must re-validate ownership inside the host/owner body — `NetFlags` restrict who *invokes*, not who's *allowed*. A client can call your RPC with forged args.
- **The bridge cannot synthesize keystrokes.** Visuals (a prompt rendering) verify with `screenshot_from`; behavior verifies with `execute_csharp` or a human playtest.

---

## Patterns (recipes)

### 1. Define named actions, poll by name

List each action in `ProjectSettings/Input.config` with a `KeyboardCode` **and** a `GamepadCode`, then read it by name. Never reach for `KeyboardCode` in gameplay (sgba: `ProjectSettings/Input.config:1`).

```csharp
protected override void OnUpdate()
{
    if ( Input.Pressed( "jump" ) ) Jump();          // edge: this frame only
    if ( Input.Down( "attack1" ) ) Fire();          // held
    if ( Input.Released( "use" ) ) StopUsing();

    var move = Input.AnalogMove;                     // WASD + left stick, already merged
    var look = Input.AnalogLook;                      // mouse + right stick (degrees)
    var trigger = Input.GetAnalog( InputAnalog.RightTrigger );
}
```

`AnalogMove` merges keyboard + stick; `GetAnalog` reads a single raw axis. Apply your own deadzone constant to sticks/triggers before using them.

### 2. Designer-editable, rebindable binding field

Wrap an `[InputAction]` string in a tiny serializable type so the inspector renders an action dropdown, and expose `Pressed`/`Down` as `[JsonIgnore]` computed props that delegate to the engine (SBox-Visual-Novel-Base: `Libraries/VNBase/Code/Systems/Input.cs:7-26`).

```csharp
public class InputBinding
{
    [InputAction] public string Action { get; set; } = "";

    [Hide, JsonIgnore] public bool Pressed => Sandbox.Input.Pressed( Action );
    [Hide, JsonIgnore] public bool Down    => Sandbox.Input.Down( Action );

    public static implicit operator string( InputBinding b ) => b.Action;
    public static implicit operator InputBinding( string a ) => new() { Action = a };
}
```

A settings component can then hold `[InlineEditor] public List<InputBinding> SkipActions` and gameplay asks `SkipActions.Any( x => x.Pressed )` — no string literals scattered through code.

### 3. `IPressable` for use/press, with its two real gotchas

Implement `Component.IPressable` on the component for the modern button/lever/usable flow. Two things bite everyone:

**(a) Presses land on the COLLIDER object, not your logical root.** On interaction-start, add a `PressablePropagate` to the collider so child-collider presses bubble up to the root component (dxrp: `game/Code/Player/Player.Interact.cs:19`).

```csharp
private void OnStartInteract()
    => Controller.ColliderObject.GetOrAddComponent<PressablePropagate>();
```

**(b) `IPressable.Release` is currently unreliable — don't depend on it.** Capture the presser's `Connection` on `Press`, then detect release yourself in `OnUpdate` (wirebox: `Code/wirebox/components/WireButtonComponent.cs:8-20`).

```csharp
Connection _pressedBy;

bool IPressable.CanPress( IPressable.Event e ) => GateType == GateMode.Constant; // forbid when state disallows
bool IPressable.Press( IPressable.Event e )
{
    SetOn( !On );
    _pressedBy = e.Source.Network.Owner;   // who pressed it
    return true;
}

protected override void OnUpdate()
{
    // poll for release instead of trusting IPressable.Release
    if ( Connection.Local == _pressedBy && !Input.Down( "use" ) )
    {
        SetOn( false );
        _pressedBy = null;
    }
}
```

If `SetOn`/the effect is an `[Rpc.Broadcast]`, validate `e.Source.Network.Owner == Rpc.Caller` before mutating in multiplayer — see the authority gotcha below.

### 4. Contextual "Press E to ..." prompt over `IPressable`

`IPressable` does the mechanics; for the HUD hint, define a *project* interface and have interactables implement both. The HUD scans nearby objects and reads `InputHint` to render the correct glyph for the bound action. Even the player can implement both, making a player "usable" (dxrp: `game/Code/Player/Player.Interact.cs:4-14`).

```csharp
public partial class Player : IContextualObject, Component.IPressable
{
    public Vector3 ContextPosition  => WorldPosition + Vector3.Up * 40f;
    public float   ContextMaxDistance => 120f;
    public bool    ShouldShow()      => !IsDead && !IsLocalPlayer;
    public string  InputHint         => "use";        // -> glyph for the "use" action
    public string? DisplayText       => Job.Interaction;
}
```

### 5. Analog feel from binary keys: read-raw → filter, separate rise/fall

Read raw input once, gate to zero when there's no driver, then ramp each channel toward its target with **different rise vs fall rates** plus a deadzone snap-to-zero. Cheap, high-impact game feel (sbox-vehicle-kit: `Libraries/Vehicles.Maintenance/Code/Components/VehicleBase.InputFilter.cs:26-49`).

```csharp
void TickInputFilter( float dt )
{
    var rawT = MathF.Abs( _rawThrottle ) < InputDeadzone ? 0f : _rawThrottle;
    // wind up slower than you let off:
    var rate = MathF.Abs( rawT ) > MathF.Abs( ThrottleInput ) ? ThrottleRiseRate : ThrottleFallRate;
    ThrottleInput = ApproachF( ThrottleInput, rawT, rate * dt );
}

static float ApproachF( float cur, float target, float maxDelta )
{
    var diff = target - cur;
    return MathF.Abs( diff ) <= maxDelta ? target : cur + MathF.Sign( diff ) * maxDelta;
}
```

The raw values come from input, gated by the driver check: `_rawThrottle = HasDriver ? Input.AnalogMove.x : 0f;` and `_rawSteer = HasDriver ? -Input.AnalogMove.y : 0f;`. Note the **sign flip on `.y`** — verify your axis mapping against Project Settings → Input before trusting it. (Note: `MathF` is unavailable in some sandbox contexts — confirm, or hand-roll abs/sign.)

### 6. Indirect control: route a controller's input with `ClientInput.PushScope`

For vehicles, mounted weapons, turrets, possession, or spectator, a `GameObjectSystem` iterates the occupied seats and wraps the per-seat tick in `using var scope = ClientInput.PushScope(player)`. Controllable components then read the *seated* player's input through that scope (sandbox: `Code/Game/ControlSystem/ControlSystem.cs:46-66`).

```csharp
void RunControl( BaseChair chair, LinkedGameObjectBuilder builder )
{
    var player = chair.GetOccupant()?.GetComponent<Player>();
    if ( !player.IsValid() ) return;

    using var scope = ClientInput.PushScope( player );   // restores prior scope on dispose

    foreach ( var o in builder.Objects )
        foreach ( var c in o.GetComponentsInChildren<IPlayerControllable>() )
            if ( c.CanControl( player ) ) c.OnControl();   // reads input within the scope
}
```

Sort seats so the earliest occupant wins (`OrderBy(_occupiedSince)`), and skip objects another seat already claimed. Expose the binding as `[Property, Sync] ClientInput` so it's editable in-game. The disposable scope (restore-on-dispose) and the editable binding are reusable for any possession game.

### 7. Centralized dispatch with cancellable pre/post events

Instead of inline `if (Input.Pressed(...))` per action, register them declaratively so one base loop handles limit-checks, undo, achievements, and auto-derived HUD labels for free (sandbox-plus-plus: `Code/Weapons/ToolGun/ToolMode.cs:140`).

```csharp
RegisterAction( ToolInput.Primary, () => "Spawn", DoSpawn, InputMode.Pressed );
// base DispatchActions(): for each entry -> check Input.Pressed/Down ->
// fire cancellable OnToolAction -> run callback -> fire OnPostToolAction with Track()'d objects
```

### 8. Block UI click-passthrough by element type

A full-screen "click anywhere to advance" HUD will *also* fire when the user clicks a real button on top of it. Guard by checking `e.Target.GetType()` against an ignore-set (SBox-Visual-Novel-Base: `Libraries/VNBase/Code/UI/VNHud.razor.cs:69-86`).

```csharp
static readonly Type[] Ignore = [typeof(Button), typeof(DropDown), typeof(IconPanel)];

protected override void OnMouseDown( MousePanelEvent e )
{
    if ( Ignore.Contains( e.Target.GetType() ) ) return;   // clicked a real widget
    AdvanceDialogue();
}
```

---

## Gotcha table

| Gotcha | Fix |
|---|---|
| `IPressable.Release` doesn't fire reliably | Capture presser `Connection` on `Press`; poll `Connection.Local == pressedBy && !Input.Down("use")` in `OnUpdate` (wirebox `WireButtonComponent.cs:8-20`) |
| Press lands on the collider, not your root | `GetOrAddComponent<PressablePropagate>()` on the collider object so it bubbles to the root component (dxrp `Player.Interact.cs:19`) |
| Hardcoded `KeyboardCode` | Always go through named `[InputAction]`s so rebinding + gamepad work (sgba `Input.config:1`) |
| `AnalogMove` axis sign/mapping wrong | Verify x/y (and the `-.y` steer flip) against Project Settings → Input; same for `GetAnalog` axes (vehicle-kit `VehicleBase.InputFilter.cs`) |
| Vehicle keeps reacting when empty | Gate raw input to zero on `!HasDriver` before filtering |
| Sticks/filtered channels never rest at 0 | Apply a deadzone snap-to-zero before ramping (`< InputDeadzone ? 0f`) |
| Full-screen HUD double-fires on widget clicks | Early-return when `e.Target.GetType()` is in an ignore-set (VNBase `VNHud.razor.cs:69`) |
| Input-driven RPC trusts the caller | Re-validate `presser.Network.Owner == Rpc.Caller`/ownership inside every host/broadcast body — `NetFlags` ≠ security |
| Mutating synced state on a proxy | Gate mutators: `if (IsProxy) return;` / `if (!Networking.IsHost) return;` or the change silently rolls back |
| `Network.IsOwner` is false in solo editor | No lobby = no owner; pair with a `LocalSimulation` property so input isn't dead in single-instance playtests |
| `MathF` missing in sandbox | Confirm availability or hand-roll `abs`/`sign` in the input filter |

---

Verify live: API names drift between SDK builds — confirm `IPressable` signatures, `ClientInput`/`PushScope`, `Input.AnalogMove`/`GetAnalog`, and `[InputAction]` against the installed SDK with `describe_type`/`search_types` (reflection is authoritative), or `search_docs` for `gameplay/input/raw-input`, `ui/interactions`, `scene/components/component-interfaces`.

See also: **sbox-api** (resolve the exact installed signatures) and **sbox-build-feature** (the screenshot-driven loop to confirm a prompt actually renders, since the bridge can't keypress).

---

## Corpus refresh (2026): more reference implementations

### 9. Custom `IUse` interface with per-frame focus-tracking (`LookingAt`)

`IPressable` is the engine's built-in flow. For games that want full control over the "look at → hover highlight → interact" loop, define a project interface and drive it yourself. `gabreusenra.wjse` (`Cooking/IUse.cs`, `Player/PlayerInventory.cs`) does the minimal version:

```csharp
// Cooking/IUse.cs
public interface IUse
{
    bool CanUse( GameObject user );
    void OnUse( GameObject user );
    void LookingAt( bool isLooking );   // called every frame focus changes
}

// PlayerInventory.RaycastDetection() — called in OnUpdate
var tr = Scene.Trace.Ray( eyeRay, MaxRange )
              .UseHitboxes()
              .WithoutTags( "player" )
              .Run();

var hit = tr.GameObject?.GetComponent<IUse>();
if ( hit != _currentUsable )
{
    _currentUsable?.LookingAt( false );
    _currentUsable = hit;
    _currentUsable?.LookingAt( true );   // fires highlight/balloon on the station
}

if ( Input.Pressed( "use" ) )
{
    if ( _currentUsable != null && _currentUsable.CanUse( GameObject ) )
        _currentUsable.OnUse( GameObject );
    else if ( IsHoldingItem )
        DropHeldItem();
}
```

The `LookingAt` callback lets stations show/hide world-space inspect UI exactly once on enter/exit, avoiding per-frame panel checks. The "drop held item when using on nothing" fallback is a nice ergonomic default for any carry game.

Anti-pattern (`gabreusenra.wjse`): `LabelStation.ApplyLabel` is `[Rpc.Broadcast]` with no host-authority check, so last-writer wins. For any competitive game, validate `Rpc.Caller` inside the broadcast body before mutating state.

---

### 10. Drag-to-aim in Razor: screen-space `MousePanelEvent` + `Hud.DrawLine`, no `InputAction`

Some mechanics (slingshot, pool cue, golf putt) are better expressed as screen-space drag gestures than named input actions. `alcoholics.nice_putt_idiot` (`Code/UI/Hud.razor`) does this entirely in a Razor panel with no `Input.Pressed`/`Input.Down` call:

```csharp
// Hud.razor — runs on the local client only
Vector2 _dragStart;
bool _isDragging;

protected override void OnMouseDown( MousePanelEvent e )
{
    if ( golfBall.IsMoving ) return;               // gate: don't aim while ball is rolling
    _dragStart = e.LocalPosition;
    _isDragging = true;
}

protected override void OnMouseUp( MousePanelEvent e )
{
    if ( !_isDragging ) return;
    _isDragging = false;
    var drag = _dragStart - e.LocalPosition;       // pull-back = shoot direction
    golfBall.Putt( drag.Normal, drag.Length, MaxLineLength );
}

public override void Tick()
{
    if ( !_isDragging || golfBall.IsMoving ) return;

    // project the world object to screen, draw the aim line
    var origin = Camera.PointToScreenPixels( golfBall.WorldPosition );
    var dir    = _dragStart - Mouse.Position;
    // lerp line color by power fraction:
    var t      = MathX.Clamp( dir.Length / MaxLineLength, 0f, 1f );
    var col    = Color.Lerp( Color.Green, Color.White, t );
    Scene.Camera.Hud.DrawLine( origin, origin + dir, col, 2f );
}
```

Key points: `Camera.PointToScreenPixels` converts a world position to panel coordinates; `Scene.Camera.Hud.DrawLine` draws immediate-mode each frame (no retained geometry). The `IsMoving` gate on `OnMouseDown` prevents queuing a shot while the ball is still rolling. Anti-pattern: using `MathF.Min` here — use `MathX.Clamp` instead (`MathF` is unavailable in the s&box sandbox).

---

### 11. Hold-to-confirm with `TimeSince`/`Remap` + `Input.ReleaseAction` to consume the key

A "hold E for 1s to confirm" prompt that doesn't re-fire the moment it completes. `alcoholics.nice_putt_idiot` (`Code/UI/RestartPrompt/RestartPrompt.razor`):

```csharp
TimeSince _timeHeld;
bool _isHolding;

public override void Tick()
{
    var holding = Input.Down( "restart" );

    if ( holding && !_isHolding ) { _timeHeld = 0; _isHolding = true; }
    if ( !holding ) { _isHolding = false; return; }

    var progress = MathX.Clamp( (float)_timeHeld, 0f, HoldDuration );
    BarPanel.Style.Width = Length.Fraction( progress / HoldDuration );

    if ( _timeHeld >= HoldDuration )
    {
        _isHolding = false;
        Input.ReleaseAction( "restart" );   // consume the held press so it doesn't re-fire
        OnConfirmed?.Invoke();
    }
}
```

`Input.ReleaseAction` is the key call: without it, the action is still "held" next frame and the confirmation fires again immediately. Also suppress while the scene is mid-transition (`ball.IsInTransition`) so a held key across a respawn doesn't chain restarts.

---

### 12. Controller-agnostic key glyphs with `Input.GetGlyph`

Render the correct key icon or gamepad button texture for any named action at runtime:

```csharp
// InputHint.razor — refreshed each Tick
protected override void Tick()
{
    var tex = Input.GetGlyph(
        Action,                                        // e.g. "use", "jump"
        InputGlyphSize.Small,
        GlyphStyle.Light.WithNeutralColorABXY()        // ABXY buttons neutral color
    );
    GlyphImage.Texture = tex;
    GlyphImage.Style.Width  = tex.Width;
    GlyphImage.Style.Height = tex.Height;
}
```

`Input.GetGlyph` returns a `Texture` that reflects the *currently bound* device (keyboard or gamepad), so the prompt updates automatically on controller connect/disconnect. Anti-pattern: hardcoding a key name as a string in the hint panel — always go through `GetGlyph` on the action name. (alcoholics.nice_putt_idiot `Code/UI/InputHint/InputHint.razor`)

Bonus trick: for a gesture (like a drag) that has no single "pressed" state, drive the "pressed" visual from your own flag (`Hud.Instance._isDragging`) rather than from `Input.Down`, so the glyph reflects the *gesture*, not the raw button.

---

### 13. Block game input while a UI panel is open: `UseInputControls` + `Mouse.Visibility`

When a UI panel (shop, inventory, dialogue) is open, two things need to happen: the game's input system must stop reading movement/action keys, and the mouse cursor must appear. `bublic.stone_by_stone` (`Code/PlayerComponent.cs`) uses the built-in `PlayerController` toggle for both:

```csharp
void OpenPanel( Panel panel )
{
    panel.Style.Display = DisplayMode.Flex;
    Controller.UseInputControls = false;    // PlayerController stops reading Input.*
    Mouse.Visibility = MouseVisibility.Visible;
}

void ClosePanel( Panel panel )
{
    panel.Style.Display = DisplayMode.None;
    Controller.UseInputControls = true;
    Mouse.Visibility = MouseVisibility.Hidden;
}
```

`UseInputControls = false` is the one-line way to freeze a `PlayerController`'s movement/camera without nulling the component or gating every input site. Pair it with `Mouse.Visibility` so the cursor appears for clicking UI. Anti-pattern: setting each `Input.Down("move")` to zero manually — it's fragile and misses jump/attack/use.

---

**Read these games** for further input-interaction patterns: `gabreusenra.wjse` (trace→IUse focus loop, carry/drop routing), `alcoholics.nice_putt_idiot` (drag-to-aim, hold-to-confirm, `GetGlyph`), `bublic.stone_by_stone` (`UseInputControls` + `Mouse.Visibility`, single-trace multi-tag dispatch). Prior corpus: `wirebox` (IPressable poll-for-release), `dxrp` (PressablePropagate + IContextualObject), `sbox-vehicle-kit` (analog rise/fall filter), `sandbox` (ClientInput.PushScope), `SBox-Visual-Novel-Base` ([InputAction] binding field + UI passthrough guard).
