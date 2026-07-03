# Social-Hub Genre Recipe

How to build a persistent shared lobby that rotates self-contained minigames/experiences in modern s&box (GameObject/Component/Scene), distilled from the shipped title `playbtg.elevator` (Elevator S&box).

## What defines the genre

A social-hub game is **one persistent networked scene where players hang out, then get funnelled through a rotation of short, self-contained experiences** (a minigame, a horror level, a shop, a survival round). The hub itself (the Elevator) is the stable home; the "level" is ephemeral content cloned in, played for a timed round, and torn down. Around that spine sits the universal social-game meta: an interaction/use-prompt loop, a coin economy, an inventory+shop, XP/streak progression, and cloud persistence so the numbers stick between sessions.

The core loop, from the shipped game's own summary:
> hang out in a shared lobby -> doors open into a randomly-shuffled experience (one of ~15: King of the Hill, Red Light Green Light, SCP-173, Lava Rising, Glass Bridge, a Shop...) -> survive a timed round -> ride the elevator to the next one (elevator: summary).

The defining architectural decision, and the one that dictates everything else: **the whole game is strictly host-authoritative and built to survive host migration.** Every spawned object is tagged for cleanup, every must-survive value is `[Sync(SyncFlags.FromHost)]`, and any collection state that can't be synced directly is serialized to a string so a promoted host can rebuild it. Get this discipline right up front; retrofitting it is painful.

## The system stack to compose

Compose these in roughly this order. Each maps to a deeper system reference where one exists.

1. **Host-authoritative network bootstrap** (`references/systems/round-match.md`) — `Component.INetworkListener` creates the lobby and spawns one owned player prefab per connection.
2. **Round / experience rotation engine** (`references/systems/round-match.md`) — the singleton manager that shuffles a queue, clones the next level prefab, runs a synced countdown, and tears it all down.
3. **Data-driven experience catalog** (`references/systems/spawning-waves.md` for the spawn cadence) — `GameResource` definitions + an abstract `BaseLevelController` so a new minigame is purely additive.
4. **Lobby / ready-up state machine** (`references/systems/round-match.md`) — host-only ready counting + a synced pre-game countdown that kicks off the first round and loops.
5. **Interaction / use-prompt** — raycast-hover -> world-panel glyph + outline -> `[Rpc.Broadcast] Interact()`. The shared verb behind shop, doors, pickups.
6. **Coin economy** (`references/systems/economy-currency.md`) — a synced `Balance` + world coin pickups spawned on a player-scaled timer.
7. **Inventory + hotbar + weapon deploy** (`references/systems/inventory.md`) — slot list, active-slot sync, clone-and-parent equipment prefabs.
8. **Shop / vendor** (`references/systems/shop-vendor.md`) — a `BaseLevelController` that displays 3 random items with a confirm-gated purchase.
9. **Health / death / spectator / ragdoll** — edge-detected life-state transition centralizing all death side effects.
10. **XP / level / streak + cloud persistence** (`references/systems/progression-upgrades.md`, `references/systems/save-persistence.md`, `references/systems/leaderboards-services.md`) — `Services.Stats` for the headline numbers, `Services.Achievements` for unlocks.
11. **NavMesh chase-AI base** (`references/systems/spawning-waves.md`) — a reusable `ChasingActor` the horror levels subclass.

## Build order

Build the hub-and-rotation skeleton before any single minigame. Vertical-slice order:

**1. Network bootstrap.** A `Component.INetworkListener` that creates the lobby in `OnLoad` and spawns an owned player per connection in `OnActive` (a host callback). Notify the rotation manager so it can track joiners.

```csharp
public class HubNetworkHelper : Component, Component.INetworkListener
{
    [Property] public GameObject PlayerPrefab { get; set; }

    protected override async Task OnLoad()
    {
        if (Scene.IsEditor) return;
        if (!Networking.IsActive)
            Networking.CreateLobby(new());            // become the host
    }

    public void OnActive(Connection channel)          // runs on the host
    {
        var player = PlayerPrefab.Clone(FindSpawn(), name: $"Player - {channel.DisplayName}");
        player.NetworkSpawn(channel);                  // client owns its own player
        if (player.Components.TryGet<ElevatorPlayer>(out var p))
            ExperienceManager.Instance?.OnPlayerJoined(p);
    }
}
```
(elevator: Code/Utility/ElevatorNetworkHelper.cs:30 OnLoad/CreateLobby, :46 OnActive.) Player identity is resolved by `Connection`/SteamId, not a stable id — keep that in mind for any per-player lookup.

**2. Rotation engine.** ONE singleton `Component`. All authoritative state is `[Sync(SyncFlags.FromHost)]` so it survives migration; every mutator early-returns `if (!Networking.IsHost) return;`. The round queue is a `List<T>` that *can't* be synced, so it's mirrored into a synced comma-joined string of titles.

```csharp
public class ExperienceManager : Component
{
    public static ExperienceManager Instance { get; private set; }
    // Synced so state survives host migration:
    [Sync(SyncFlags.FromHost)] public float NextLevelChange { get; set; }
    [Sync(SyncFlags.FromHost)] public bool ExperienceLoaded { get; set; }
    [Sync(SyncFlags.FromHost)] public string ExperienceTitle { get; private set; }
    [Sync(SyncFlags.FromHost)] private string ExperienceOrderSerialized { get; set; } = "";

    protected override void OnAwake() => Instance = this;  // no null-guard — one per scene only
}
```
(elevator: Code/Experiences/ExperienceManager.cs:15 Instance, :16-29 the synced state block + serialized queue, :35 OnAwake.) `Instance` is set with no guard, so two managers clobber each other — keep exactly one.

**3. Load a level by clone + tag + orphan + spawn.** The whole reason every spawned thing gets a tag: after a host migration the manager's object *reference* is gone, so cleanup is done by tag, not by reference. `SetOrphanedMode(NetworkOrphaned.Host)` makes orphaned objects transfer to the promoted host instead of vanishing.

```csharp
public void BeginNextLevel(string forceExperience = null)
{
    if (!Networking.IsHost) return;
    if (_experienceOrder.Count == 0) RestoreExperienceOrder();   // rebuild after migration
    if (_experienceOrder.Count == 0)
        foreach (var exp in ExperienceRegistry.Current.Experiences.Shuffle())
            _experienceOrder.Add(exp);                            // (+ a Shop every 3rd)

    var next = _experienceOrder[0];
    _experienceOrder.RemoveAt(0);
    ExperienceOrderSerialized = string.Join(",", _experienceOrder.Select(e => e.Title));

    DestroyLevelObjects();                                        // wipe the previous round first
    var level = GameObject.Clone(next.Prefab);
    level.Tags.Add("active-level");                               // tag = the cleanup handle
    level.NetworkMode = NetworkMode.Object;
    level.Network.SetOrphanedMode(NetworkOrphaned.Host);         // survive migration
    level.NetworkSpawn();
    ExperienceTitle = next.Title; ExperienceLoaded = true;       // [Sync] props replicate automatically
}
```
(elevator: ExperienceManager.cs:174 BeginNextLevel, :214 serialize queue, :233-237 clone+tag+orphan+spawn, :353 RestoreExperienceOrder.) `RestoreExperienceOrder` splits the synced string back through `GetByTitle` — so titles are the de-facto primary key; renaming one silently breaks forced loads and queue restore.

**4. Tear down by tag.** The counterpart. Note it's `[Rpc.Broadcast]` so the destroy runs on every peer, and it queries the scene by tag — never by a held reference.

```csharp
[Rpc.Broadcast]
private void DestroyLevelObjects()
{
    foreach (var o in Scene.GetAllObjects(true).Where(o => o.Tags.Has("active-level")).ToList())
        o.Destroy();
    foreach (var o in Scene.GetAllObjects(true).Where(o => o.Tags.Has("temporary")).ToList())
        o.Destroy();
}
```
(elevator: ExperienceManager.cs:373 DestroyLevelObjects.) **Every spawned thing MUST be tagged `active-level` or `temporary` or it leaks across rounds.** Coins, ragdolls, checkpoints, AI — all tagged `temporary`.

**5. Data-driven experiences.** A new minigame is purely additive: a `GameResource` asset + a level prefab + a `BaseLevelController` subclass. The engine above is untouched.

```csharp
[GameResource("Experience", "exp", "A minigame definition")]
public class ExperienceDefinition : GameResource
{
    public string Title { get; set; }
    public GameObject Prefab { get; set; }       // the level prefab to clone
    public float Duration { get; set; } = 60f;
    public int ExperienceReward { get; set; }
    public float CoinSpawnMultiplier { get; set; } = 1f;
    public int MinimumRequiredPlayers { get; set; }
}

public abstract class BaseLevelController : Component
{
    public virtual bool EndLevelOnDeadOrInElevator { get; set; } = false;
    public virtual bool ShouldDisableFallDamage => false;
    public virtual void OnLevelStarted() { }
    public virtual void OnPlayerJoined(ElevatorPlayer player) { }
    public virtual void OnPlayerDeath() { }      // e.g. drop coins
    public virtual void OnLevelFinished() { }    // award XP / achievements
}
```
(elevator: Code/Experiences/ExperienceDefinition.cs:4; Code/Experiences/Levels/BaseLevelController.cs:10 + virtual hooks :46.) `ExperienceRegistry` holds a `[Property] List<ExperienceDefinition>` exposed as a static `Current` singleton with a linear `GetByTitle()` (elevator: Code/Experiences/ExperienceRegistry.cs:18). Same trick for items via `EquipmentDefinition`.

**6. Lobby ready-up + loop.** A second host-only singleton counts `IsReady` players, runs a synced pre-game countdown, flips `HasFirstGameStarted`, calls `BeginNextLevel`, then auto-detects level-end to start a short post-game countdown — the continuous elevator loop. (elevator: Code/Utility/LobbyManager.cs:26 ready/countdown, :81 start next level.) Lobby and Experience managers share timing by *polling each other's synced fields*, not events — an order-of-update coupling to be aware of.

## The interaction loop (shared verb)

Every interactable derives from `BaseInteractable` (tagged `can_hover`). The player raycasts from the eye each frame, resolves the hit `BaseInteractable`, toggles a hover outline + a billboarded `WorldPanel` glyph, and on "use" fires:

```csharp
[Rpc.Broadcast]
public void Interact(ElevatorPlayer interactor) => OnInteract(interactor);
```
(elevator: Code/Interaction/BaseInteractable.cs:90 Interact, :43 SetHovered.) **`Interact()` is `[Rpc.Broadcast]`, so `OnInteract` runs on ALL clients** — a subclass that does anything stateful MUST guard `if (interactor != ElevatorPlayer.Local) return;` or the action fires once per peer. The shop does this; copy it. Walk-over pickups skip the prompt and use a `Collectible : Component, ITriggerListener` base instead (elevator: Code/Interaction/Interactables/Collectible.cs:5).

## Economy, shop, inventory (the social meta)

- **Coins:** `Balance` is a `[Sync] int`; `AddCoins/RemoveCoins` are `[Rpc.Owner]` and also persist via `Services.Stats.Increment("coins")`. `Coin.Create()` is host-only, clones a coin prefab, merges any coins within 32u into one stacked value, tags it `temporary`, and network-spawns; the manager spawns them on a timer that shrinks 10% per player above 3 (floor 2s). (elevator: Code/Actors/ElevatorPlayer.Score.cs:43 AddCoins; Code/Interaction/Interactables/Coin.cs:65 Create, :48 OnCollect.) `Balance` is a public-set `[Sync]` (not host-validated) — fine for a casual game, exploitable in a competitive one. See `references/systems/economy-currency.md`.
- **Inventory:** `InventoryComponent` holds `List<InventorySlot>` with only `ActiveSlot` synced; contents are rebuilt per-client via `[Rpc.Owner]` grants. `DeployWeapon` clones the equipment prefab, parents it to the player GO at local origin, and network-spawns. (elevator: Code/Inventory/InventoryComponent.cs:109 DeployWeapon, :138 GiveItem.) The weapon GO must be a *direct child* of the player — `BaseWeapon.Owner` uses `GetComponentInParent`. See `references/systems/inventory.md`.
- **Shop:** a `ShopController : BaseLevelController` picks 3 random `EquipmentDefinition`s into `ShopDisplay` components (3D product model + a `ShopSign` world panel). "use" opens a razor confirm dialog; on confirm it checks `Balance >= Cost`, `RemoveCoins`, `GiveItem`, and fires an `OnPurchase Action<Player>` hook so non-item purchases (upgrades/effects) work too. (elevator: Code/Interaction/Interactables/ShopInteraction.cs:19 TryPurchase; Code/Experiences/Levels/ShopController.cs:16.) Purchase runs client-side with no host re-validation of price. See `references/systems/shop-vendor.md`.

## Death, progression, AI

- **Life-state edge detection** centralizes all death/respawn side effects in one place: a `LifeUpdate()` compares `_previousLifeState` vs `IsAlive` each frame and runs the alive->dead transition exactly once (enable spectator, build a runtime networked ragdoll tagged `temporary`, clear inventory+effects, reset streak, call `LevelController.OnPlayerDeath`). A `_safeDeath` flag distinguishes scripted teleport-kills (level transitions) from real deaths so you don't penalize a player on a transition. (elevator: Code/Actors/ElevatorPlayer.Life.cs:25 LifeUpdate, :71 CreateRagdoll, :105 Respawn.)
- **XP / level / streak** are synced ints; `AddExperience` applies a streak multiplier, recomputes level off a power curve, and persists via `Services.Stats.Increment`. `RefreshStats()` rehydrates exp/streak/wins/coins from `Services.Stats.LocalPlayer` on spawn; achievements unlock via `Services.Achievements`. (elevator: Code/Actors/ElevatorPlayer.Score.cs:80 AddExperience, :65 RefreshStats; Code/Utility/AchievementHelper.cs:28.) Persistence is cloud-only (no local file) and last-write-wins — `Sum` vs `LastValue` semantics differ per stat. See `references/systems/leaderboards-services.md` + `references/systems/save-persistence.md`.
- **NavMesh chase-AI:** `ChasingActor` (requires `NavMeshAgent` + `CitizenAnimationHelper`) is a reusable horror base. Targeting + contact damage are host-only; it picks the nearest player with a *complete* `Scene.NavMesh.CalculatePath` (skips unreachable), `MoveTo`s, and re-validates periodically. **Animation is replicated by broadcasting inputs, not the pose:** the host reads `Agent.Velocity/WishVelocity`/look and `[Rpc.Broadcast]`s those three vectors; each client feeds them into its own `CitizenAnimationHelper`. (elevator: Code/Actors/ChasingActor.cs:57 OnUpdate, :116 UpdateAnimation, :167 SelectTargetByDistance.) `Scene.NavMesh.SetDirty()` MUST be called when a level loads or paths return `Incomplete` forever. See `references/systems/spawning-waves.md`.

## Standout patterns worth copying

- **Tag-driven lifecycle cleanup as a host-migration safety net** — never hold a reference to the active level; tag everything `active-level`/`temporary` and tear down by `Scene.GetAllObjects().Where(o => o.Tags.Has(...))`. Pair with `SetOrphanedMode(NetworkOrphaned.Host)`. This is the single most reusable s&box meta-pattern (elevator: ExperienceManager.cs:373, :234-237).
- **Serialize-to-`[Sync]`-string so a promoted host can rebuild ephemeral state** — a `List<T>` can't be synced, so join it to a `[Sync(FromHost)]` comma string of titles and rebuild via `GetByTitle`. The general recipe for surviving migration with any collection state (elevator: ExperienceManager.cs:214 serialize, :353 restore).
- **Host computes animation, broadcasts the inputs not the pose** — for networked NPCs, broadcast velocity/wishvelocity/look; let each client animate its own `CitizenAnimationHelper`. Perfect sync at minimal bandwidth (elevator: ChasingActor.cs:116-130).
- **Abstract Component + GameResource = zero-engine-change content pipeline** — a new minigame is an `.exp` asset + a prefab + a `BaseLevelController` subclass added to a registry list; the rotation engine is untouched. The cleanest data-driven content pattern in the corpus (elevator: BaseLevelController.cs:10; ExperienceDefinition.cs:4).
- **Edge-detected life-state transition** — run all alive->dead / dead->alive side effects exactly once from a single `LifeUpdate()` diff, with a `_safeDeath` flag for scripted kills, instead of scattering death handling across damage callsites (elevator: ElevatorPlayer.Life.cs:25-60).
- **Guard every `[Rpc.Broadcast]`** — broadcasts run on every peer; stateful handlers (`Interact`, coin award, purchase) must filter to the local/owning player or the effect fires N times.

## Verify live

s&box's API shifts between SDK versions — reflection is the source of truth, not this doc or training data. Before writing against an unfamiliar type, confirm it: `describe_type` / `search_types` for `Component`, `Component.INetworkListener`, `Sandbox.Networking` (`CreateLobby`, `NetworkSpawn`, `SetOrphanedMode`, `NetworkOrphaned`), `Sandbox.Services.Stats` / `Services.Achievements`, `NavMeshAgent` + `Scene.NavMesh`, `WorldPanel`, `GameResource`, and the `[Sync(SyncFlags.FromHost)]` / `[Rpc.Owner]` / `[Rpc.Broadcast]` attributes; `Scene.Trace` for the interaction raycast. Stop play mode before scene edits; screenshot visual changes and read the PNG.

Cross-links: see the **sbox-api** skill for authoritative type/method signatures, and the **sbox-build-feature** skill for the screenshot-driven build loop and the sandbox gotcha list (MathF restricted, head-bone case sensitivity, NavMesh must be baked + set dirty on level load, Cloud assets ephemeral).

## Corpus refresh (2026): more reference implementations

The three games below add four net-new techniques for social-hub lobby presence not covered above.

---

### 1. `GameObjectSystem<T>` as the network bootstrap — no scene object required (facepunch.jumper)

The spine uses a `Component` on a root GameObject for the network helper. `facepunch.jumper` shows a cleaner alternative: the manager is a `GameObjectSystem<T>` (+ `INetworkListener`, `ISceneStartup`), which the engine instantiates automatically per scene with no scene wiring and no risk of the GO being deleted.

```csharp
public class HubGameSystem : GameObjectSystem<HubGameSystem>, INetworkListener, ISceneStartup
{
    public HubGameSystem(Scene scene) : base(scene) { }

    // Called once on the host before the scene runs — the race-free hook for lobby setup
    void ISceneStartup.OnHostPreInitialize(SceneFile sf)
    {
        // Stamp lobby visibility BEFORE networking spins up — never lose a race with CreateLobby
        LaunchArguments.Privacy = LaunchPrivacy.Public;   // or Friends
        Networking.CreateLobby(new LobbyConfig { MaxPlayers = 32 });
    }

    public void OnActive(Connection channel)   // host callback: a new player connected
    {
        var player = PlayerPrefab.Clone(FindSpawn(), name: $"Player - {channel.DisplayName}");
        player.NetworkSpawn(channel);
    }
}
```

(jumper: `Code/GamePlay/GameManager.cs:1`; sino: `Code/Core/CasinoLobbyPrivacySystem.cs:1` for the `OnHostPreInitialize` lobby-privacy hook.) **Anti-pattern to avoid:** trying to set lobby privacy after `Networking.CreateLobby` — `ISceneStartup.OnHostPreInitialize` is the correct, race-free hook. `sino.s_sino` ships exactly this pattern (`CasinoLobbyPrivacySystem : GameObjectSystem<T>, ISceneStartup`).

---

### 2. Diff-based per-connection lobby preview reconciler (gabreusenra.wjse)

The spine spawns one player per `OnActive` call. For a lobby waiting room you often want a **preview doll** visible before the match starts, reconciled as connections come and go. `wjse/LobbyManager` does this with a simple dictionary diff:

```csharp
// Host-only, called every frame during lobby phase
private void UpdateNetworkedPreviews()
{
    if (!Networking.IsHost) return;

    // Spawn a preview for any new connection
    foreach (var conn in Networking.Connections)
    {
        if (_previews.ContainsKey(conn.Id)) continue;
        var doll = PreviewPrefab.Clone(GetPreviewSlot(conn), name: $"Preview - {conn.DisplayName}");
        doll.NetworkSpawn(conn);   // conn owns their own preview; engine replicates Destroy
        _previews[conn.Id] = doll;
    }

    // Destroy previews for disconnected connections
    var live = new HashSet<Guid>(Networking.Connections.Select(c => c.Id));
    foreach (var id in _previews.Keys.Where(id => !live.Contains(id)).ToList())
    {
        _previews[id]?.Destroy();
        _previews.Remove(id);
    }
}
```

(wjse: `Map and Lobby/LobbyManager.cs:124-187`.) Key point: `NetworkSpawn(conn)` makes the **chosen player own their own preview**, so the engine handles proxy destruction when the owner disconnects. No manual cleanup needed on disconnect.

**Also from wjse — carrying character/map choices across a scene load via a static dictionary:**

```csharp
// Before calling Game.ActiveScene.Load(nextMap), copy ephemeral NetDictionary into a static:
public static Dictionary<Guid, int> StartingCharacters = new();

void StartGame()
{
    foreach (var (id, pick) in PlayerCharacters)   // PlayerCharacters is a [Sync] NetDictionary
        StartingCharacters[id] = pick;
    Game.ActiveScene.Load(ChosenMap.SceneFile);
}

// In the next scene's GameSpawner.OnActive:
int charPick = LobbyManager.StartingCharacters.GetValueOrDefault(channel.Id, 0);
var prefab = CharacterPrefabs[charPick];
prefab.Clone(...).NetworkSpawn(channel);
```

(wjse: `Map and Lobby/LobbyManager.cs:240-289`, `GameSpawner.cs:37-84`.) The `static` dict is the deliberate bridge across the scene-load boundary; `NetDictionary` lives only for the scene's lifetime.

---

### 3. One-player-per-station with a 30-second reservation grace (sino.s_sino)

A recurring social-hub need: a table/terminal/seat that only one player can use at a time, with a hold period after they leave so they can walk away briefly without losing their spot.

```csharp
public class InteractionStation : Component, Component.IPressable
{
    [Sync] public string OccupiedBySteamId { get; private set; }
    [Sync] public double ReservedUntilTime { get; private set; }  // Time.Now + 30

    public bool IsOccupied => !string.IsNullOrEmpty(OccupiedBySteamId);
    public bool IsOccupiedByLocal => OccupiedBySteamId == Connection.Local.SteamId.ToString();
    // A reservation holds for 30 s after the owner leaves
    public bool IsReservedAgainstMe => Time.Now < ReservedUntilTime && !IsOccupiedByLocal;

    public bool Press(IPressable.Event e)
    {
        if (IsOccupied && !IsOccupiedByLocal) return false;   // seat taken
        if (IsReservedAgainstMe) return false;                 // grace period
        ClaimSeat();
        return true;
    }

    [Rpc.Broadcast]
    private void ClaimSeat()
    {
        OccupiedBySteamId = Connection.Local.SteamId.ToString();
        ReservedUntilTime = 0;
    }

    public void ReleaseSeat()   // call when the player closes the overlay
    {
        OccupiedBySteamId = "";
        ReservedUntilTime = Time.Now + 30.0;   // 30s grace
    }

    protected override void OnUpdate()
    {
        // Non-proxy owner auto-clears a stale reservation after it expires
        if (!IsProxy && !IsOccupied && Time.Now >= ReservedUntilTime)
            ReservedUntilTime = 0;
    }
}
```

(sino: `Code/Core/GamingTerminalStation.cs:52-80`.) Only `[Sync]` primitives — no custom RPC for the claim; `[Rpc.Broadcast]` writes the synced fields so every peer sees the seat flip in one step.

---

### 4. Ping-corrected shared game clock for timed rounds (barrelproto.ragroll)

The spine uses `Time.Now` directly for round timing. For a drop-in social hub where players join mid-round, a host-broadcast `[Sync]` timestamp + client-side ping correction gives everyone the same apparent clock without drift:

```csharp
public class HostClock : Component
{
    [Sync] private float _hostTimestamp { get; set; }   // written only by host
    private float _localOffset;
    private float _lastBroadcast;

    // Every peer reads this — it's the authoritative shared time
    public float HostTime => _hostTimestamp + _localOffset;

    protected override void OnUpdate()
    {
        if (Networking.IsHost)
        {
            if (Time.Now - _lastBroadcast > 0.4f)
            {
                _hostTimestamp = Time.Now;   // [Sync] replicates to all clients
                _lastBroadcast = Time.Now;
            }
        }
        else
        {
            // Clients: keep advancing locally; only snap on significant drift
            var estimated = _hostTimestamp + Connection.Host.Ping * 0.001f;
            if (MathX.Abs(estimated - (_hostTimestamp + _localOffset)) > 0.1f)
                _localOffset = estimated - _hostTimestamp;
            else
                _localOffset += Time.Delta;
        }
    }

    public void OnBecameHost()
    {
        // Keep whichever is later so clock never jumps backward on promotion
        _hostTimestamp = MathX.Max(_hostTimestamp, Time.Now);
        _localOffset = 0;
    }
}
```

(ragroll: `Code/mode/networking/HostClock.cs`.) Use `HostClock.HostTime` instead of `Time.Now` wherever the round timer, spawn windows, or streak windows need to agree across clients. **Note:** `MathF.Abs` does not exist in the s&box sandbox — use `MathX.Abs`.

### 5. `NetworkOrphaned.ClearOwner` for drop-in mode objects (barrelproto.ragroll)

The spine uses `NetworkOrphaned.Host` for level objects (they migrate to the new host). For the **mode manager itself** — the singleton that owns game state — `ragroll` uses `ClearOwner` instead:

```csharp
// Host clones the mode prefab once; ClearOwner means it survives migration as unowned
// OnBecameHost then re-asserts control of the now-unowned object
void InitializeMode()
{
    if (!Networking.IsHost) return;
    _modeGO = ModePrefab.Clone();
    _modeGO.NetworkSpawn();
    _modeGO.Network.SetOrphanedMode(NetworkOrphaned.ClearOwner);  // survives host leave
}

public void OnBecameHost()
{
    // Take back authority over the orphaned mode object
    _modeGO?.Network.TakeOwnership();
    // Rebuild any ephemeral state from [Sync] fields here
}
```

(ragroll: `GameController.cs`, `RollMode.cs:80`; also `lavagame.sandmoney_` uses the same `OnBecameHost` + `RecoverFromHostMigration` pattern.) **When to use `ClearOwner` vs `Host`:** use `Host` for level content (persists under the new host's tree), use `ClearOwner` for the mode/manager object itself (survives unowned, then `OnBecameHost` re-asserts — avoids a circular ownership dependency).

---

### Read these games

For social-hub lobby presence, read these in priority order:

1. `playbtg.elevator` — the canonical hub architecture (rotation, tags, migration, interaction, economy)
2. `sino.s_sino` — seat occupancy/reservation, external-server economy, `ISceneStartup` lobby privacy, WorldPanel batching fix
3. `barrelproto.ragroll` — `ClearOwner`+`OnBecameHost` mode migration, ping-corrected clock, `GameObjectSystem<T>`
4. `gabreusenra.wjse` — per-connection preview reconciler, map-vote + character-pick across scene load
5. `facepunch.jumper` — `GameObjectSystem<T>` network helper, Steam avatar in Razor via `avatar:SteamId` URL
