# Co-op Kitchen / Assembly-Line (Overcooked-like) Recipe

Build a 1–4-player co-op throughput game: players race a shift clock to assemble multi-stage items at shared stations — grab an ingredient, carry it, process it, hand it off, deliver it against time-limited order tickets for a shared star score. Distilled mainly from `gabreusenra.wjse` (an Overcooked-style **packing & shipping** game — dispense → pack → label → deliver), with composable pieces lifted from `luckygaming.doner_kiosk` (a co-op kebab kiosk: ordered ingredient assembly + dual win/lose counters + co-op disconnect cleanup) and `emg.everything_must_go` (carry-boxes-to-shelf stocking + host-authoritative discipline + co-op roles).

## What defines the genre

A co-op kitchen game is **shared-station throughput under a clock**. Distinct from `party-microgame` (no elimination, no per-round rules-swap) and from `round-match` alone (the *interaction loop* is the genre, not the timer). The irreducible spine:

- **Carryable items + hand-off.** Items are physics props you pick up (one per hand), carry between stations, drop, and throw. Co-op *is* the hand-off: one player dispenses, another packs, a third delivers.
- **Multi-stage assembly.** A finished product is built across N stations. The "recipe" is the *accumulated state on the item itself*, not a recipe asset — graded only at delivery.
- **Order tickets + per-order timers.** A board spawns time-limited orders; each has its own countdown. Deliver a matching item before it expires.
- **Shared score, soft fail.** Everyone feeds one score; wrong/late deliveries dock points rather than ending the run. The shift clock ends the round → star rating.
- **Round flow.** `Lobby (pick map + character) → Playing (timer + continuous orders) → GameOver (score → stars) → Restart/Menu`.

### Core loop

`StartShift → roll/spawn orders on an interval (soft-capped) → player A dispenses ingredient → carries → station processes it (timed, stacks state onto the item) → hand off → player B delivers to the turn-in → validate vs the soonest matching order → additive-penalty score → order expires or clears → shift clock hits 0 → map score→stars → save best → Restart or Menu`. (wjse: `Cooking/*`, `OrderManager.cs`, `GameLoopManager.cs`)

## The system stack to compose

Build these as separate components. References point to existing system/genre docs where one applies — lift those and add the kitchen-specific glue here.

| System | Role | Reference / source |
|---|---|---|
| Look-and-use interaction interface | trace → `IUse` → focus-track → press E dispatch | below (wjse `Cooking/IUse.cs`) |
| Carryable items (pick up / drop / throw) | physics prop with `[Sync]` holder, hand-IK, arc throw | below (wjse `Cooking/Ingredient.cs`) |
| Assembly stations | dispense / process-with-progress / consumable / hand-off | below (wjse `PackingStation.cs`, doner `EntityBoard.cs`) |
| State-on-the-object recipe | accumulate `itemId` + stacked types + label on the item | below (wjse `Box.cs`, `PackingStation.cs`) |
| Order board + ticket spawner | interval spawn, soft cap, per-order countdown, allow-lists | below (wjse `OrderManager.cs`); `references/systems/spawning-waves.md` |
| Turn-in validator + scoring | quality-graded delivery → additive penalty score | below (wjse `OrderManager.DeliverBox`); `references/systems/economy-currency.md` |
| Shift clock + star rating | host-authoritative `[Sync] TimeRemaining` → thresholds | `references/systems/round-match.md` |
| Lobby: map vote + character pick | vote tally + tie-break, picks carried across scene load | `references/genres/social-hub.md` (wjse `LobbyManager.cs`) |
| Best-run save (content-keyed) | per-map high score, write-on-improvement | `references/systems/save-persistence.md` (wjse `SaveManager.cs`) |
| Proximity world UI on stations | enable a world panel only when the local player is near | below (wjse `IngredientBox.cs`) |
| Item registry (id → prefab/icon) | small `GameResource` DB, lookup by int id | `references/systems/data-assets.md` (wjse `ItemResource.cs`) |
| Co-op cleanup on disconnect | reclaim/close the leaver's stations | below (doner `GameNetworkManager`) |

The first five are the irreducible core — a kitchen game is *interaction interface + carryables + stations + an order board + a turn-in validator*. The rest is flow and polish.

## The interaction interface (lift this first — every station rides it)

One tiny interface is the whole interaction surface (wjse: `Cooking/IUse.cs`):

```csharp
public interface IUse
{
    bool CanUse( GameObject user );      // gate (e.g. empty hands, has-box)
    void OnUse( GameObject user );        // do the thing
    void LookingAt( bool isLooking );     // focus enter/exit → world "inspect" UI
}
```

The player runs one per-frame trace, tracks the focused `IUse`, fires `LookingAt` on focus changes, and routes the `use` press (wjse: `Player/PlayerInventory.RaycastDetection`):

```csharp
var tr = Scene.Trace.Ray( eyePos, eyePos + eyeFwd * Reach )
    .UseHitboxes().WithoutTags( "player" ).Run();         // camera ray self-hits the body — exclude it
var hit = tr.GameObject?.Components.Get<IUse>( FindMode.EverythingInSelfAndParent );
if ( hit != _focused ) { _focused?.LookingAt( false ); hit?.LookingAt( true ); _focused = hit; }
if ( Input.Pressed( "use" ) )
{
    if ( _focused is not null && _focused.CanUse( GameObject ) ) _focused.OnUse( GameObject );
    else DropHeldItem();                                   // looking at nothing → drop
}
```

Every station (`IngredientBox`, `PackingStation`, `LabelStation`, `DeliveryStation`) implements `IUse`. To add a station: implement the interface, no dispatcher change. This trace→`IUse`→focus→press-E trio is the most-reused thing in the genre. (See also the document-sim `Interactable.RunLogic()` variant — same idea, abstract `Component` instead of an interface.)

## Carryable items + hand-off (the co-op verb)

A carried item disables its own physics while held and re-enables it when dropped, every frame — so it never falls/collides in-hand but is fully physical when set down or thrown (wjse: `Cooking/Ingredient.cs`):

```csharp
protected override void OnUpdate()
{
    bool held = PlayerHolder is not null;
    Rigidbody.Enabled = !held;
    Collider.Enabled  = !held;            // carried = no collision; dropped = real prop
    if ( held ) WorldTransform = PlayerHolder.HoldTransform.WorldTransform; // snap to the hand
}
```

`PlayerHolder` should be `[Sync]` so every client sees who holds what. Throwing is an arc velocity + random spin for skill-based long deliveries (wjse: `PlayerInventory.ThrowItem`). Hand posing: drive Citizen IK live rather than authoring an anim state — `PlayerRenderer.Set("ik.hand_left.position", ...)`, and lower the targets as `EyeAngles.pitch` increases so a carried item follows the camera looking down (wjse: `PlayerInventory.UpdateDynamicIK`).

**Hand-off** falls out for free: player A's `DropHeldItem()` clears `PlayerHolder` → the item becomes physical on the counter → player B looks at it, presses use, sets `PlayerHolder = self`. No bespoke transfer RPC needed.

## State-on-the-object assembly (no Recipe asset)

The defining structural trick: the "recipe" for a finished product is the **accumulated component state on the item**, built up across stations and graded only at delivery. wjse's `Box` carries `int itemID`, a *stacked* `List<BoxType> Type`, and a `LabelType Label`. The packing station copies the prior box's state forward and appends its own, enabling combos like `[common, fragile]` (wjse: `Cooking/PackingStation.cs:192-232`):

```csharp
newBox.Type = new List<BoxType>();
foreach ( var t in existingBox.Type ) newBox.Type.Add( t );   // carry prior wraps forward
newBox.Type.Add( this.Type );                                  // stack THIS station's wrap on top
newBox.itemID = existingBox.itemID;                            // carry the contained item id
newBox.Label  = existingBox.Label;                             // carry any prior label
```

The ordered, linear-recipe variant (doner `EntityBoard.cs`) is worth lifting when stages are *fixed-sequence on one bench* rather than spread across rooms: a `BoardState` enum (`AwaitBread → AwaitSauce → AwaitMeat → … → Finish`), each `GiveX()` enables the next ingredient model and advances the state, and the player validates via two dictionaries — item→required-state and item→place-action — refusing out-of-order ingredients with a "Need {x}" hint (doner: `Code/Player/Player.cs`, `Code/Game/EntityBoard.cs`):

```csharp
_itemToBoardState = { { bread, AwaitBread }, { sauce, AwaitSauce }, { meat, AwaitMeat } };
_placementActions = { { bread, b => b.GiveBread() }, { meat, b => b.GiveMeat() } };
if ( board.AwaitItemBoard != requiredState ) { Hint($"Need {requiredState}"); return; }
```

**Compose them:** use wjse's stack-state-forward for *parallel, recombinant* assembly (any order, combos), and doner's `BoardState` FSM + hold-to-place gate for a *single fixed-sequence* prep bench. Both grade only at the end.

## Stations: dispense, process, consume, hand-off

Each station is an `IUse` component with a small role. The four archetypes (wjse `Cooking/`):

- **Dispenser** — `OnUse` clones the item prefab and `NetworkSpawn()`s a loose carryable above the station; `CanUse` gates on empty hands. (`IngredientBox.cs`)
- **Processor (timed)** — accept a held item, run a progress bar, then spawn the next-stage item carrying state forward:
  ```csharp
  protected override void OnUpdate()
  {
      if ( !Networking.IsHost || !HasInput ) return;
      PackingProgress += Time.Delta;
      if ( PackingProgress >= TimeToPack ) FinishPacking();   // spawn output, carry state, reset
  }
  ```
  (`PackingStation.cs`)
- **Consumable resource** — a station can have a depletable supply that *blocks* it at zero and is refilled by using it while holding a specific item id. wjse's tape: `[Sync] int CurrentTapeUses`, decremented per finished box, packing blocked at 0, refilled by using the station holding item id 4 (consumes the held tape roll). A logistics pressure gauge layered onto a station. (`PackingStation.cs:14-54,219-221`)
- **Turn-in** — validate the item against active orders and destroy it (next section). Make it both `IUse` (hand it in) **and** `ITriggerListener` so a *thrown* item that lands in the zone auto-delivers — skill-based long-range deliveries (wjse: `DeliveryStation.cs` is both).

## The order board: interval spawn, soft cap, per-order timers

The board is a **continuous procedural spawner with a soft cap**, not discrete waves. Every `SpawnInterval` it generates one order while `ActiveOrders.Count < Max`. Make difficulty *data-driven* via per-scene allow-lists a designer fills (no code change to add a level), and grant more time for harder combos (wjse: `OrderManager.GenerateRandomOrder`):

```csharp
if ( !Networking.IsHost ) return;
if ( SpawnTimer >= SpawnInterval && ActiveOrders.Count < 8 )
{
    var item  = Game.Random.FromList( AllowedItems );             // designer-authored pool
    var label = (LabelType)Game.Random.Int( 1, 5 );               // skip None
    var box   = PickBaseBoxType( excluding: BoxType.Fragile );
    float maxTime = BaseTime;
    if ( Game.Random.Float() < 0.5f ) { box.Add( BoxType.Fragile ); maxTime *= 1.5f; } // combo = more time
    ActiveOrders.Add( new OrderData { ItemId = item.ItemId, LabelType = label, RequiredBoxes = box, MaxTime = maxTime } );
}
foreach ( var o in ActiveOrders ) o.TimeRemaining -= Time.Delta;  // host ticks each ticket
```

Each order owns its own `MaxTime`/`TimeRemaining`. (wjse: `OrderManager.cs`)

## Turn-in validator + quality-graded scoring

The "transaction" is order validation at the turn-in; the currency is **score**. The cleanest reusable shape is **additive penalty scoring** off a perfect base, with *forgiving matching* — find an order whose item matches, else fall back to the soonest-expiring one so a "close enough" delivery still clears something (wjse: `OrderManager.DeliverBox`):

```csharp
var target = ActiveOrders.FirstOrDefault( o => o.ItemId == box.itemID )
          ?? ActiveOrders.OrderBy( o => o.TimeRemaining ).First();  // forgiving: clear the most urgent
int points = 100;
if ( box.itemID == -1 )            points -= 500;   // empty / unpacked
else if ( box.itemID != target.ItemId ) points -= 100;   // wrong item
if ( box.Label != target.LabelType )     points -= 40;    // wrong label
bool comboOk = target.RequiredBoxes.Count == box.Type.Count
            && target.RequiredBoxes.All( t => box.Type.Contains( t ) );
if ( !comboOk )                    points -= 40;    // wrong/incomplete packaging
CurrentScore += points;                              // (host-only — see gotchas)
```

Expired orders cost a flat penalty (`-50`). At game-over, `GameLoopManager` maps the final score to 0–3 stars via three thresholds (`Star1/2/3Score`). This is the genre's *quality-graded turn-in* economy — the same shape as document-sim's verdict scoring and shopkeeper order fulfilment, so lift the grading idea from whichever you've already built. (wjse: `OrderManager.cs`, `GameLoopManager.cs`)

## Round flow, lobby, and best-run save

The shift clock is a host-authoritative countdown — `if (!Networking.IsHost) return;`, `[Sync] TimeRemaining`/`IsGameOver`, star thresholds, `GetTimeFormatted()` → `m\:ss`, host-only **Restart** (`Game.ActiveScene.Load` the same scene) or **Menu**. This is the bog-standard round skeleton — see **`references/systems/round-match.md`** and lift it wholesale. (wjse: `GameLoopManager.cs`, `EndGameUI.razor`)

For the **dual win/lose** variant (a success target *and* a failure cap rather than just a clock), lift doner's twin-counter spine: `CustomerNeedCount` (hit 0 → win) vs `PlayerErrorCount` (hit 4 → lose), each polled in `OnUpdate` with a one-shot latch so the ending fires exactly once (doner: `Code/Game/GameSettings.cs`). Its `RestartSettings()` is a clean **soft-restart without a scene reload** (re-enable the controller, destroy all spawned NPCs/items, reposition players, reset the board).

**Lobby (map vote + character pick carried into the match)** and **best-run save (content-keyed)** are social-hub / save-persistence sub-patterns — see **`references/genres/social-hub.md`** and **`references/systems/save-persistence.md`**. The two non-obvious bits to lift from wjse:
- The chosen characters are copied into a **static** dict (`LobbyManager.StartingPlayers`) *just before* `Game.ActiveScene.Load`, then read by the next scene's spawner — the static dict is the deliberate bridge across the scene-load boundary. (wjse: `LobbyManager.cs`, `GameSpawner.cs`)
- The save is keyed by `Game.ActiveScene.Source?.ResourcePath` (the map's content path), writes only when the new score/stars **beat** the record, and the game-over UI uses a `_hasSavedRecord` latch so it writes once, not 60×/sec. (wjse: `SaveManager.cs`, `EndGameUI.razor`)

## Proximity world UI on stations (no per-frame panel churn)

Stations show a world-space panel only when the *local* player is near — and force it on for an out-of-resource state so a broken station is visible from afar (wjse: `IngredientBox.cs`, `PackingStation.cs`):

```csharp
var local = Scene.GetAllComponents<PlayerInventory>().First( p => p.IsMe );
StationUI.Enabled = Vector3.DistanceBetween( WorldPosition, local.WorldPosition ) <= ViewDistance
                 || CurrentTapeUses <= 0;     // force-on when out of tape
```

For Razor reactivity, override `BuildHash()` over exactly the fields that should trigger a re-render (order count, score, rounded timer) so panels only re-render on meaningful change (wjse: `OrderScreen.razor`, `EndGameUI.razor`). Emoji-as-icon via `[Icon("📦")]` enum attributes is a cheap iconography path with no texture pipeline.

## Co-op cleanup on disconnect

When a player leaves mid-shift, reclaim or reset their stations so the round isn't soft-locked. doner finds the *other* player's named props (curtain/light/door) and force-closes them on disconnect (doner: `GameNetworkManager.OnDisconnected`). The general rule: anything a leaver could be holding or had reserved (a held carryable's `PlayerHolder`, a station's in-progress input) must be released host-side on `INetworkListener` disconnect, or it strands forever. emg's host-authoritative `Shop` (`if (!Networking.IsHost) return;` simulate-on-host, clients are render-only mirrors) is the cleanest discipline to copy if you want all station state owned in one place — it sidesteps most of these orphan cases by construction.

## Build order

1. **Interaction interface + carryables.** `IUse` + the player trace/focus/press-E loop; a `Carryable` with `[Sync] PlayerHolder` and physics-off-while-held. Verify: spawn a box, pick it up, carry, drop, hand it to a second client. This is the whole co-op verb — get it solid before anything else.
2. **One dispenser + one turn-in.** Dispenser clones+`NetworkSpawn`s a carryable; turn-in destroys it and adds a flat score. Verify a full grab→carry→deliver with score moving (host-side).
3. **A processing station + state-on-the-object.** Add a timed `PackingStation` that spawns the next-stage item carrying `itemId`/types/label forward. Now you have multi-stage assembly.
4. **Order board.** `OrderManager` with allow-listed pools, interval spawn + soft cap, per-order `TimeRemaining` ticked host-side, and `OrderScreen.razor` with a `BuildHash`. Wire the turn-in validator to grade against the soonest matching order (additive penalties).
5. **Shift clock + stars.** Lift `round-match`: `[Sync] TimeRemaining`, star thresholds, host-only Restart/Menu.
6. **Lobby + best-run save.** Lift social-hub map-vote/character-pick (static carry-over dict → next-scene spawner) and the content-path-keyed write-on-improvement save with a one-shot latch.
7. **Polish.** Consumable station resources (tape), throw-to-deliver trigger, proximity world UI, dynamic carry IK, disconnect cleanup, a stress/relief mechanic if you want texture.

## Gotchas & anti-patterns (wjse ships these — teach the fix)

The primary game is feature-complete but its networking includes real footguns. **Score and validation on the host only.** wjse's `DeliverBox` and a `[Rpc.Broadcast] SyncScore` run wherever the *delivering client* is and let any caller mutate `CurrentScore` with no `Networking.IsHost` re-check — a cheat client could broadcast arbitrary points. Correct: validate + score on host, `[Sync]` the result. (wjse: `OrderManager.cs:161-210`) Run `networking_lint` to catch money/score as plain `[Sync]` and unguarded mutators.

- **Don't broadcast the order list every frame.** wjse's `OnUpdate` calls a `[Rpc.Broadcast]` that resends the entire `List<OrderData>` *plus* a per-order timer RPC *every frame* — enormous redundant bandwidth. A mutating `[Sync]` collection already replicates; tick timers host-side and rely on `[Sync]`. (wjse: `OrderManager.cs:51-79`) Note `[Sync]` can't replicate a `List<CustomClass>` directly — if you hit that, use the **struct-of-arrays / parallel `NetList<primitive>`** workaround (emg: `Shop.CheckoutSync.cs`; see `references/engine/networking-authority.md`).
- **Don't put host-only logic inside `[Rpc.Broadcast]`.** wjse's `GenerateRandomOrder`/`FindItem` broadcast to every peer but only the host branch does work — conflates "run on all" with "host-only." Cleaner: a plain host method + `[Sync]`/targeted RPC.
- **Authority gate on interaction RPCs is the competitive boundary.** wjse's `LabelStation` RPCs have the host check commented out, so label state is whoever-clicked-last. Fine for friendly co-op; gate on host the moment scoring depends on it.
- **Camera-origin ray self-hits the player body** — `.WithoutTags("player")` (and `.UseHitboxes()`) on the interaction trace, every time.
- **Resolve the carryable/player up the hierarchy** — the collider is usually a child; use `FindMode.EverythingInSelfAndParent` when reading a component off a trace/trigger hit.
- **`[Sync] TimeUntil`/countdowns read on the host; clients only display.** Don't let a client tick the shift clock or order timers.
- **Save once, on improvement.** Latch the game-over write (`_hasSavedRecord`) and only `WriteJson` when the score beats the record — or you write every frame the end screen is open.

## Verify live

The installed SDK is the source of truth — confirm signatures with reflection before coding (the networking surface shifts between versions):

- `describe_type Sandbox.GameObject` / `search_types Carryable` — confirm `NetworkSpawn`, `WorldTransform`, `Components.Get<T>(FindMode)`.
- `describe_type Scene` then the `Trace` API — confirm `Scene.Trace.Ray(...).UseHitboxes().WithoutTags(...).Run()` and `SceneTraceResult` members.
- `search_types Rpc` / `describe_type SyncFlags` — confirm `[Rpc.Host]`/`[Rpc.Broadcast]`, `[Sync(SyncFlags.FromHost)]`, and `TimeUntil`/`TimeSince` before writing the clock.
- `describe_type Component.ITriggerListener`, `INetworkListener` — confirm the throw-to-deliver trigger and the disconnect-cleanup hooks.
- `describe_type GameResource` — confirm the item-registry `[GameResource("Item Data","item")]` data-asset shape.
- Then run **`networking_lint`** and **`scene_validate`** on the scene to catch unguarded `[Sync]` score mutators and trigger-vs-trace mismatches.

## Which games to read

- **`gabreusenra.wjse`** — the near-complete reference for this genre. `Cooking/IUse.cs` (interaction interface), `Cooking/Ingredient.cs` (carryable), `Cooking/PackingStation.cs` (timed processor + state-stacking + consumable tape), `Cooking/IngredientBox.cs`/`LabelStation.cs`/`DeliveryStation.cs` (station archetypes), `OrderManager.cs` (order board + scoring — *and the anti-patterns*), `GameLoopManager.cs` + `EndGameUI.razor` (round flow + stars + save latch), `Map and Lobby/LobbyManager.cs` + `GameSpawner.cs` (map vote + cross-scene character carry-over), `SaveManager.cs` (content-keyed best-run), `ItemResource.cs`/`LevelData.cs` (data-asset backbone).
- **`luckygaming.doner_kiosk`** — lift the **ordered linear-recipe** assembly bench (`EntityBoard.cs` `BoardState` FSM + item→state/action dicts + hold-to-place gate), the **dual win/lose counter** round spine + **soft-restart-without-reload** (`GameSettings.cs`), the **`BaseInteractor : IPressable`** tooltip-pressable prop base, and **co-op disconnect cleanup** of a partner's named stations.
- **`emg.everything_must_go`** — lift the **host-authoritative discipline** (one host-owned sim component, clients render-only mirrors; authority-forked read properties), the **struct-of-arrays `NetList` workaround** for replicating lists of structs (`Shop.CheckoutSync.cs`), and the **carry-box → snap-onto-surface** stocking engine (`Shelf.cs`/`SurfaceLevel.cs`) if your "delivery" is placing items on a rack rather than into a truck.

Cross-links: **`references/systems/round-match.md`** (shift clock + stars), **`references/genres/social-hub.md`** (lobby/map-vote), **`references/systems/save-persistence.md`** (best-run save), **`references/systems/spawning-waves.md`** (order spawner), **`references/engine/networking-authority.md`** (host authority + `NetList` SoA). Use the **sbox-api** skill for reflection-verified type signatures and **sbox-build-feature** for the screenshot-driven build-and-verify loop.
