# Networking & Authority

Host-authoritative multiplayer in modern s&box: who simulates, who writes, and how clients request changes without trusting each other. Read this before touching `[Sync]`, `[Rpc.*]`, ownership, or the lobby lifecycle.

## Mental model

Every machine runs the same C#. Three roles decide who is allowed to *act*:

- **Owner** simulates its own object (input, movement, prediction). Gate with `!IsProxy`.
- **Host** is authoritative for shared/global state (money, score, roster, spawns). Gate with `Networking.IsHost`.
- **Proxies** only render — they interpolate replicated state and run cosmetic effects. A proxy that mutates synced state is silently rolled back.

The single most-repeated convention: at the top of a networked component's `OnUpdate`/`OnFixedUpdate`, `if ( IsProxy ) return;` before reading `Input` or running movement/AI/shooting (sbox-scenestaging: `Code/ExampleComponents/Gun.cs:7`). The owner is authoritative for *its own* object; the host is authoritative for *everyone's* shared state. Clients never write authoritative state directly — they receive it via `[Sync(SyncFlags.FromHost)]` and request changes through `[Rpc.Host]`.

> `Network.IsOwner` is null/false in a solo editor playtest (no lobby → no owner), so an `IsOwner`-only guard silently disables whole systems until real multiplayer starts. Prefer `!IsProxy`, or pair with a `LocalSimulation` `[Property]`: `ShouldSimulate => LocalSimulation || Network.IsOwner`.

---

## Patterns

### 1. Gate input & simulation on `!IsProxy` (owner-authoritative)

```csharp
protected override void OnUpdate()
{
    if ( IsProxy ) return;                 // proxies only render replicated state
    var look = Components.GetInAncestors<PlayerController>().EyeAngles.ToRotation();
    if ( Input.Pressed( "Attack1" ) ) Fire( look.Forward );
}
```

Owner-authoritative state is plain `[Sync]` and only mutated when `!IsProxy` — e.g. `[Sync] public Angles EyeAngles`, `[Sync] public bool IsRunning`, written only inside the `!IsProxy` branch (sbox-scenestaging: `Code/ExampleComponents/PlayerController.cs:17,27,42`). Pair effect bodies with `if ( Application.IsDedicatedServer ) return;` so a headless server skips particles/sounds.

### 2. Host-authoritative writes: guard every mutator with `Networking.IsHost`

```csharp
[Sync( SyncFlags.FromHost )] public float SalaryMultiplier { get; set; } = 1f;

public void SetMultiplier( float v )
{
    Assert.True( Networking.IsHost, "SetMultiplier is host-only" ); // loud in dev
    if ( !Networking.IsHost ) return;                              // bail in release
    SalaryMultiplier = v;
}
```

Authoritative, cheat-sensitive fields are `[Sync(SyncFlags.FromHost)]` so only the host may author them (dxrp: `game/code/GameManager.cs:21`). A host-only broadcast can also self-assert with `Networking.IsHost` before counting (sbox-scenestaging: `Code/ExampleComponents/SnapshotTest.cs:13`).

### 3. The host-wrapper idiom (one authoritative writer, callable from anywhere)

Make the public mutator network-transparent: if not the host, forward through a private `[Rpc.Host]` that re-calls the public method on the host. The mutation body only ever executes host-side.

```csharp
public void SwitchWeapon( BaseCarryable weapon, bool allowHolster = false )
{
    if ( !Networking.IsHost ) { HostSwitchWeapon( weapon, allowHolster ); return; }
    // ... real mutation runs only on the host ...
    ActiveWeapon = weapon;
}

[Rpc.Host]
private void HostSwitchWeapon( BaseCarryable weapon, bool allowHolster = false )
    => SwitchWeapon( weapon, allowHolster );
```

Verbatim shape from the base game (sandbox: `Code/Player/PlayerInventory.cs:530`). `ActiveWeapon` itself is `[Sync(SyncFlags.FromHost)]` so clients can't author it directly.

### 4. NEVER trust the client — re-validate inside every `[Rpc.Host]`

A malicious client can invoke any `[Rpc.Host]` directly with forged args. `NetFlags` restrict who may *invoke*, which is **not** security. Inside the host body: validate the caller, re-check authority, clamp inputs, and rate-limit.

```csharp
[Rpc.Host]
private void RequestOwnershipHost( GameObject go )
{
    var caller = Rpc.Caller;                // trusted server-side identity
    var callerId = Rpc.CallerId;

    // rate-limit, keyed by caller, so spamming the RPC can't bypass limits
    if ( !go.IsValid() ||
         Cooldown.Current.CheckAndStartCooldown( $"{callerId}:ownership:take", cost ) )
        return;

    if ( !GameUtils.HasPermission( caller, go ) ) return; // re-check authority server-side
    // ... mutate ...
}
```

From dxrp (`game/code/GameManager.cs:212`). The base sandbox-plus-plus equivalent re-checks `if ( !c.GameObject.HasAccess( Rpc.Caller ) ) return;` before a reflection setter (sandbox-plus-plus: `Code/GameLoop/GameManager.cs:235`). `HasAccess` = admin bypass / unowned-is-public / owner==caller via an `Ownable` component.

### 5. Pick the right replication attribute

| Field kind | Attribute | Why |
| --- | --- | --- |
| Owner-predicted (`EyeAngles`, `IsRunning`, `IsNoclipping`) | `[Sync]` | owner-writable, owner→everyone |
| Authoritative / cheat-sensitive (money, score, `SteamId`, team) | `[Sync(SyncFlags.FromHost)]` | only the host may author |
| Derived (`FuelPct`, `CanStartEngine`) | *plain computed property, no `[Sync]`* | recompute from synced primitives on every client — fewer bytes, no desync |

Plain `[Sync]` on money/health/score is a classic exploit — the client can author the value. Use `FromHost` for anything authoritative (dxrp: `game/code/GameManager.cs:21`; sandbox: `Code/Player/PlayerInventory.cs:15`).

### 6. React with `[Change]`, don't poll

```csharp
[Sync( SyncFlags.FromHost ), Change] public BaseCarryable ActiveWeapon { get; private set; }

// Convention: On<PropertyName>Changed( old, @new ) — fires on every client when it changes
void OnActiveWeaponChanged( BaseCarryable oldW, BaseCarryable newW )
{
    if ( oldW.IsValid() ) oldW.GameObject.Enabled = false;
    if ( newW.IsValid() ) newW.GameObject.Enabled = true;
}
```

Drive client visuals/audio off replicated state instead of diffing in `OnUpdate` (sandbox: `Code/Player/PlayerInventory.cs:15`). Late-joining proxies rebuild visual state in `OnStart` by reading synced flags.

### 7. Separate authority from presentation (compute on host, broadcast effects)

Damage/ammo/death/score are computed host-side; cosmetic results fan out with `[Rpc.Broadcast]`.

```csharp
[Rpc.Broadcast( NetFlags.HostOnly )]                 // only the host may originate
public void NotifyDeath( string victim, string killer ) { /* kill-feed UI everywhere */ }

[Rpc.Broadcast( NetFlags.Unreliable )]               // high-frequency cosmetic only
public void ShootEffects() { if ( Application.IsDedicatedServer ) return; /* sfx */ }
```

`NetFlags.HostOnly` marks a broadcast only the host may send (sbox-scenestaging: `Code/ExampleComponents/SnapshotTest.cs:19`). State-critical broadcasts (death, spawn) keep `NetFlags.Reliable`; high-frequency cosmetic-only calls use `Unreliable`/`UnreliableNoDelay`. A common beginner failure is running gameplay inside a broadcast, or running effects only on the shooter.

### 8. Choose the RPC target deliberately

- `[Rpc.Broadcast]` — effects/sounds/chat/anims everyone should see.
- `[Rpc.Owner]` — a command only the owning client should run (`Kill`, `Kick`, `Refuel`), or push owner-targeted reliable state with `[Rpc.Owner(NetFlags.HostOnly | NetFlags.Reliable)]`.
- `[Rpc.Host]` — a client→host request. Add `NetFlags.OwnerOnly` so only an object's owner may request its own action:

```csharp
[Rpc.Host( NetFlags.OwnerOnly | NetFlags.Reliable )]
private void RequestRespawn() { /* host validates, then respawns this owner */ }
```

Funnel each kind of mutation through exactly one authority.

### 9. Unicast / exclude with `Rpc.FilterInclude` / `Rpc.FilterExclude`

Scope a broadcast server-side instead of sending to everyone and filtering on the client (which leaks data and wastes bandwidth).

```csharp
using ( Rpc.FilterInclude( c => c.Id == player.ConnectionId ) )
    PromptPlayerConsent();                                   // unicast to one player

using ( Rpc.FilterExclude( Owner.GameObject.Network.Owner ) )
    PlayWorldSound();                                        // everyone except the local shooter
```

The include form is verbatim from dxrp (`game/code/GameNetworkManager.cs:387`); the wrapped RPC is typically `[Rpc.Broadcast(NetFlags.HostOnly | NetFlags.Reliable)]`. Filter-exclude on the shooter is the reusable first-person audio idiom (simple-weapon-base: `code/swb_base/Weapon.cs:391`).

### 10. Spawn networked objects: configure → `NetworkSpawn`

`Clone()` alone makes a **local-only** object. Configure it FIRST, then call `NetworkSpawn` — passing a `Connection` assigns ownership (the owner is who simulates it).

```csharp
var o = ObjectToSpawn.Clone( pos );
o.Components.Get<Rigidbody>().Velocity = dir * 500f;  // configure BEFORE spawning
o.NetworkSpawn();                                     // or NetworkSpawn( connection ) to assign owner
```

From sbox-scenestaging (`Code/ExampleComponents/Gun.cs:20`). Spawn on exactly one machine — that `Gun` already sits behind `if ( IsProxy ) return;`, so only the owner clones. Forgetting `NetworkSpawn`, or calling it before configuring, or spawning on every client (→ N duplicates) are the three classic bugs. After spawning props that must survive their owner leaving, set the knobs in pattern 12.

### 11. Lobby + connection lifecycle via `INetworkListener`

Implement `Component.INetworkListener` on a manager. Create the lobby if none exists so a solo playtest auto-hosts; spawn host-side in `OnActive`.

```csharp
public sealed class GameNetworkManager : Component, Component.INetworkListener
{
    [Property] public GameObject PlayerPrefab { get; set; }

    protected override void OnStart()                         // auto-host for solo playtest
    {
        if ( !Networking.IsActive ) Networking.CreateLobby( new LobbyConfig() );
    }

    public void OnActive( Connection channel )                // host-side, per join
    {
        var clothing = new ClothingContainer();
        clothing.Deserialize( channel.GetUserData( "avatar" ) );
        var player = PlayerPrefab.Clone( SpawnPoint.WorldTransform );
        if ( player.Components.TryGet<SkinnedModelRenderer>( out var body,
                FindMode.EverythingInSelfAndDescendants ) )
            clothing.Apply( body );
        player.NetworkSpawn( channel );                       // assign ownership to the joiner
    }
}
```

`OnActive` is verbatim from sbox-scenestaging (`Code/ExampleComponents/GameNetworkManager.cs:8`). Set `channel.CanSpawnObjects = false` so clients can't spawn networked objects directly; `OnDisconnected` finds the player by `Network.Owner == connection` and cleans up; lobby metadata syncs via `Networking.SetData`/`GetData`.

### 12. Production lifecycle: validate-before-spawn, reuse-on-reconnect, reap orphans

Reject in `AcceptConnection` BEFORE the player exists, and `await`-validate before `NetworkSpawn` so a banned player never flashes into the world.

```csharp
public bool AcceptConnection( Connection channel, ref string reason )
{
    if ( !Config.Current.Game.AllowFamilySharePlayers && channel.OwnerSteamId != channel.SteamId )
    { reason = "Family shared accounts are not permitted."; return false; }
    return true;
}
```

From dxrp (`game/code/GameNetworkManager.cs:62`). On reconnect, find the disconnected player object and `AssignOwnership(channel)` (reusing equipment) instead of respawning (`:276`). Run a ~1 Hz pass that kicks connections that never got a player (`:141`). Naive "spawn first, validate later" leaks half-init connections and loses state on rejoin.

After spawning persistent objects:

```csharp
go.Network.SetOwnerTransfer( OwnerTransfer.Fixed );          // pin ownership
go.Network.SetOrphanedMode( NetworkOrphaned.Host );          // host takes over on owner disconnect
```

Without `OrphanedMode.Host`, props vanish when their owner leaves (sbox-grubs: `Code/Systems/Network/GrubsNetworkManager.cs:27`).

### 13. Non-replicable types: sync a `Guid`, resolve it

`Connection` and `GameObject` are local handles, NOT network-serializable. Sync the stable `Guid` and resolve.

```csharp
[Sync( SyncFlags.FromHost )] private Guid _ownerId { get; set; }

public Connection Owner
{
    get => Connection.All.FirstOrDefault( c => c.Id == _ownerId );
    set => _ownerId = value?.Id ?? Guid.Empty;
}
```

Verbatim from sandbox-plus-plus (`Code/Components/Ownable.cs:8`). For object refs, `[Sync] Guid OccupantId` + resolve via `Scene.Directory.FindByGuid( occupantId )`. The client whose input drives a shared object must OWN it — `vehicle.Network.AssignOwnership( occupant.Network.Owner )` on enter — and clear the driver gate in `OnDestroy` so input doesn't get stuck.

### 14. `Network.Refresh()` after out-of-band host mutations

Properties set via reflection (`TypeLibrary…SetValue`) or any non-`[Sync]` path won't auto-replicate. Force a re-snapshot:

```csharp
prop.SetValue( c, value );
// c.GameObject.Network.Refresh( c );   // per-component overload is unreliable, in-repo "doesn't work??"
c.GameObject.Network?.Refresh();         // whole-object refresh — use this
```

The base game's own comment flags the per-component overload as broken (sandbox-plus-plus: `Code/GameLoop/GameManager.cs:243`). Use the whole-object refresh. Also call `Network.ClearInterpolation()` after any hard teleport (not just spawn) to avoid one-frame origin ghosts.

### 15. Host-loss failover: heartbeat + `RealTimeSince` timeout

s&box doesn't always cleanly notify clients when a host vanishes, so relying only on `Connection` events leaves clients frozen.

```csharp
[Rpc.Broadcast( NetFlags.UnreliableNoDelay )]
private void RpcHostHeartbeat() => _timeSinceHostHeartbeat = 0;   // client resets on receipt

protected override void OnUpdate()
{
    if ( !Connection.Host.IsValid() || _timeSinceHostHeartbeat > 45f )
        LeaveOrResumeSolo();                                       // don't hang
}
```

Pattern from sgba (`Code/Networking/NetworkManager.cs:221`). Cheap, robust liveness every networked game should copy.

### 16. Send intent, not state (sparse discrete events)

For death/round-result/role-reveal style events, don't replicate a stateful networked entity per event — serialize the event to JSON/bytes, send through ONE RPC, deserialize back into the same typed event on the client and call `Run()` locally (the host keeps the canonical accumulation; client replay is presentation only). Far cheaper than per-field sync churn (concept verified in ttt-reborn `code/gameevents/base/NetworkableGameEvent.cs:32`; its `ConCmd` transport is legacy, the intent concept is timeless). For large blobs that exceed the per-message cap: `DeflateStream`-compress and write both raw + compressed length so the receiver pre-sizes its buffer, route lossy data over `Unreliable` and must-arrive data over `Reliable`; or fragment into `(payloadHash, index, total, partial)` packets and reassemble by hash (sgba: `Code/Networking/LinkCablePackets.cs:203`).

### 17. Host-migration watchdog: reclaim orphans, reconcile the `[Sync]` registry, sanity-restart

s&box has a known bug where networked GameObjects are **often destroyed during host migration regardless of `NetworkOrphaned` setting**, and the new host inherits stale transient state. Don't try to preserve mid-game state through a migration — detect becoming host, aggressively reconcile, and force a clean restart if anything looks broken. Detect the transition with `isHost && !_wasHost`, then **defer the sanity check ~1s** so in-flight packets that haven't applied yet don't make a healthy board look broken.

```csharp
protected override void OnUpdate()
{
    bool isHost = Networking.IsHost;
    if ( isHost && !_wasHost ) OnBecameHost();
    _wasHost = isHost;

    if ( !_settled && _timeSinceBecameHost > 1.0f ) { _settled = true; ValidatePostMigration(); }
}

void OnBecameHost()
{
    _timeSinceBecameHost = 0f; _settled = false;
    _generator.ForceResetTransientFlags();   // wipe flags the dead host's async tasks would have cleared
    ReclaimOrphanedFlags();                   // Network.TakeOwnership() each orphan so we can manage/destroy it
    RebuildFlagMappings();                    // rebuild handle→handle maps by world-position matching
    ReconcileFlaggedRegistry();               // make the [Sync] list match the visible scene
}
```

The four moves, all verbatim from sweeper_otso (`Code/HostWatchdog.cs`): **(1)** `Network.TakeOwnership()` every orphaned object (`:162`) — without authority the new host can't destroy ownerless objects, and they float forever; **(2)** rebuild any handle→handle mapping by matching `WorldPosition.Distance(...) < tol` because object `Id`s don't survive (`:225`); **(3)** reconcile the `[Sync]` registry against what's actually in the scene — drop entries with no visible object, add visible objects missing from the list — because a place/remove broadcast can be lost mid-migration (`:180`); **(4)** the deferred `ValidatePostMigration()` (`:80`) decides intact-vs-broken from the *real scene* (expected-vs-actual child count with a 10% tolerance, plus "is anyone still tagged `playing`?") and calls `ClearBoard()` for a clean round rather than limping along. Pair with the market-recovery variant `RecoverFromHostMigration` (lavagame.sandmoney_ `Code/Core/MarketManager.cs`).

### 18. Vote-kick + temp-ban (host-authoritative, enforced on connect)

Player-moderation in a host-authoritative game: clients `[Rpc.Host]`-request a vote, the host owns the tally and the ban map, and bans are re-checked on every connection.

```csharp
private Dictionary<long, DateTime> _bans = new();          // host-only: steamId -> expiry
private const float BanDurationMinutes = 30f;

void EnforceBans()                                         // run on the host each tick / on connect
{
    foreach ( var conn in Connection.All )
        if ( _bans.TryGetValue( conn.SteamId, out var expiry ) && DateTime.UtcNow < expiry )
            conn.Kick( "You were voted out. Try another server." );
}

[Rpc.Host]
public void RequestVoteKick( long targetSteamId )
{
    var caller = Rpc.Caller;
    if ( (long)caller.SteamId == targetSteamId ) return;   // can't kick yourself
    // ... open a vote, auto-yes the initiator, 20s timer ...
}
```

From sweeper_otso `Code/VoteKickSystem.cs`: the ban map is `steamId → DateTime expiry` (`:23`), expired entries are swept each pass (`:46-53`), `conn.Kick(reason)` enforces (`:59`), the threshold is majority `(playerCount / 2) + 1`, and players can change their vote. **The whole tally and ban map are host-only state** — a client never holds them; it only sends the `[Rpc.Host]` request, which (per pattern 4) re-validates the caller.

### 19. Lobby: map-vote with tie-break + per-slot networked character preview

A pre-game lobby that votes on a map and shows each joined player's chosen character. Votes/picks live in `NetDictionary<Guid,…>` (collections can't be plain `[Sync]`), the winning map is chosen with a **random tie-break**, and each slot spawns a clone with `NetworkSpawn(conn)` so everyone sees everyone's avatar — cleaned up on leave.

```csharp
[Sync] public NetDictionary<Guid, int> MapVotes { get; set; } = new();
[Sync] public NetDictionary<Guid, int> CharacterPicks { get; set; } = new();

int ResolveWinningMap()
{
    var tally = new int[Maps.Count];
    foreach ( var v in MapVotes.Values ) tally[v]++;
    int best = tally.Max();
    var winners = Enumerable.Range( 0, tally.Length ).Where( i => tally[i] == best ).ToList();
    return winners[Game.Random.Int( winners.Count - 1 )];   // random tie-break, never index 0 bias
}

void ShowPreviewFor( Connection conn, int characterIndex )
{
    var preview = CharacterPrefabs[characterIndex].Clone( SlotTransform( conn ) );
    preview.NetworkSpawn( conn );                            // everyone sees this player's avatar
    _previews[conn.Id] = preview;                            // destroy in OnDisconnected
}
```

Pattern from wjse `Code/Map and Lobby/LobbyManager.cs` (`NetDictionary` votes/picks, tie-break random, clone + `NetworkSpawn(conn)` per slot, floating Steam nameplates, `BroadcastStartGame` scene load). The cleanup-on-leave is load-bearing — orphaned preview clones accumulate every rejoin otherwise.

### 20. `NetworkMode.Never` for local-only cosmetics + a per-client static registry

For a **purely cosmetic** visual a proxy can recompute locally (a crowd body, a thought-bubble, a held-item prop), don't replicate it — set the child's `NetworkMode = NetworkMode.Never`. On a late-join, a replicated cosmetic spawns a *second* body/bubble alongside the locally-built one; `NetworkMode.Never` keeps it strictly local so there's exactly one. And to reach all instances of a hot component without the per-frame cost of `GetAllComponents`, keep a `static List<T> _all` maintained in `OnEnabled`/`OnDisabled`.

```csharp
public sealed class CustomerNpc : Component
{
    static readonly List<CustomerNpc> _all = new();
    public static IReadOnlyList<CustomerNpc> All => _all;
    protected override void OnEnabled()  { if ( !_all.Contains( this ) ) _all.Add( this ); }
    protected override void OnDisabled() => _all.Remove( this );

    void BuildVisuals()
    {
        var body = new GameObject( true, "body" );
        body.NetworkMode = NetworkMode.Never;       // local-only — no doubled body on late-join
        body.SetParent( GameObject );
        // ...bubble + held item also NetworkMode.Never...
    }
}
```

Verbatim from scoops `Code/CustomerNpc.cs` (`_all` registry `:14-18`, `body.NetworkMode = NetworkMode.Never` `:92`, bubble/item `:136,:162`). Use the `_all` registry anywhere a system iterates "every X each frame" (separation/boids, nearest-of, counts) — it turns an O(n) scene scan into a cached list.

---

## Gotcha table

| Gotcha | Fix |
| --- | --- |
| Mutating synced state on a proxy/client silently rolls back | Gate every mutator: `if ( IsProxy ) return;` (owner) or `if ( !Networking.IsHost ) return;` (host); add `Assert.True(Networking.IsHost,…)` in dev |
| `[Rpc.Host]` is callable by ANY client with forged args | `NetFlags` is not security — re-validate ownership/permission/limits AND rate-limit (cooldown keyed by `Rpc.CallerId`) inside the host body |
| Plain `[Sync]` on money/health/score lets a client author it | Use `[Sync(SyncFlags.FromHost)]` for anything authoritative |
| `Clone()` is local-only | `NetworkSpawn` is required to replicate; configure BEFORE spawning; spawn on exactly one machine (`!IsProxy`/`IsHost`) or get N duplicates |
| `Connection` / `GameObject` aren't `[Sync]`-able | Sync a `Guid`; resolve via `Connection.All` / `Scene.Directory.FindByGuid`; keep any object handle as a non-networked local field |
| Reflection / non-`[Sync]` writes don't replicate | Call `GameObject.Network.Refresh()` (whole-object; per-component overload is unreliable) |
| Effect/broadcast bodies run on the headless server too | Early-return `if ( Application.IsDedicatedServer ) return;` |
| High-frequency cosmetic RPCs on the Reliable channel clog it | Use `NetFlags.Unreliable`/`UnreliableNoDelay` for effects; reserve `Reliable` for spawn/death/roster |
| `Controller.Velocity` & physics fields valid only on the owner | Mirror with a guarded `[Sync]` write + a local-vs-proxy accessor when the host must read it |
| Spawn-first-validate-later flashes banned players in | Reject in `AcceptConnection`; `await`-validate before `NetworkSpawn`; reap orphaned connections on a timer |
| No reliable notice of an ungraceful host exit | Add an unreliable heartbeat + `RealTimeSince` timeout |
| Props vanish when their owner disconnects | `Network.SetOrphanedMode(NetworkOrphaned.Host)`; set `channel.CanSpawnObjects = false` to block client spawns |
| Driving client's input does nothing on a shared object | It must OWN it (`AssignOwnership`); clear the driver/occupant gate in `OnDestroy` |
| `Network.IsOwner` is null in solo editor playtests | Don't guard solely on `IsOwner`; use `!IsProxy` or `LocalSimulation || Network.IsOwner` |
| Networked objects vanish on host migration even with `OrphanedMode.Host` | Known s&box bug | On becoming host (`isHost && !_wasHost`): `TakeOwnership` orphans, reconcile the `[Sync]` registry against the scene, **defer the sanity check ~1s**, force a clean restart if broken (sweeper_otso `HostWatchdog.cs`) |
| Vote-kick tally / ban map trusted from clients | A client could forge the result or self-unban | Keep tally + `steamId→expiry` map host-only; clients only send the `[Rpc.Host]` request; re-check the caller; enforce bans via `conn.Kick` on connect (sweeper_otso `VoteKickSystem.cs`) |
| Lobby vote/pick stored in a plain `[Sync] Dictionary` | Collections don't replicate as `[Sync]` | Use `NetDictionary<K,V>`; random-tie-break the winning map; destroy per-slot preview clones in `OnDisconnected` (wjse `LobbyManager.cs`) |
| Cosmetic crowd body/bubble doubles on late-join | A replicated cosmetic spawns a 2nd copy beside the locally-built one | Set the cosmetic child's `NetworkMode = NetworkMode.Never`; recompute it locally on every client (scoops `CustomerNpc.cs:92`) |
| `GetAllComponents<T>()` every frame stutters | O(scene) scan in a hot loop | Maintain a `static List<T> _all` in `OnEnabled`/`OnDisabled` and iterate that (scoops `CustomerNpc.cs:14`) |

---

**Verify live:** API names drift between SDK builds — confirm members before writing with `describe_type` / `search_types` / `get_method_signature` (reflection is authoritative for the installed SDK, not memory or this doc). The bridge is single-client and cannot synthesize key presses or a second connection, so replication/ownership/refresh CANNOT be proven from one editor instance — verify with `execute_csharp` plus a human/second-client playtest.

See also the **sbox-api** skill (look up exact `[Rpc.*]`/`SyncFlags`/`Network.*` signatures) and the **sbox-build-feature** skill (the screenshot-driven build loop these patterns plug into).

---

## Corpus refresh (2026): more reference implementations

Five shipped games added net-new networking patterns beyond patterns 1–20. Cited by game + relative file.

### 21. Host-migration-safe round timer: re-arm `TimeUntil` against the new host's clock

`TimeUntil` stores an absolute time off the *old* host's clock — after migration the remaining seconds are still correct, but the epoch is wrong. Re-arm explicitly (despawn.murder `Systems/Rounds/RoundManager.cs::ValidateStateAfterMigration`):

```csharp
void ValidateStateAfterMigration()
{
    if ( State is null ) { StateIndex = -1; TransitionNext(); return; }     // stale ref
    float remaining = MathX.Max( State.TimeLeft.Relative, 0f );             // seconds left off the dead clock
    State.TimeLeft = remaining;                                              // re-arm against THIS host's epoch
    if ( State is PostRoundState ) { TransitionNext(); return; }            // migrated mid-post-round → skip
}
```

Without this, the timer is either already expired (instant round-end) or astronomically far in the future (frozen round) depending on clock drift.

### 22. Host-event + mirror RPC: converge host and clients instantly on phase transitions

The host mutates authoritative state locally AND sends a `[Rpc.Broadcast(HostOnly)]` that re-raises the same event on every client. Both paths call the same local handler, so a client that also happens to be the host doesn't miss or double-apply (despawn.murder `Systems/Rounds/RoundManager.cs`):

```csharp
void TransitionTo<T>( Action<T> init = null ) where T : RoundState
{
    State?.Finish();
    State = States.OfType<T>().First();
    init?.Invoke( (T)State );
    State.Begin();                               // host applies immediately
    BroadcastRoundStateStart( State.Identifier ); // proxies re-apply via same local Begin path
}

[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
void BroadcastRoundStateStart( string identifier )
{
    var s = States.FirstOrDefault( x => x.Identifier == identifier );
    s?.Begin();
}
```

Anti-pattern: calling `Begin()` host-side only and waiting for `[Sync]` to trickle to clients — clients react to the round-state change up to one replication interval late, causing desync in timers and phase-gated actions.

### 23. Paired `ApplyXLocal` + `[Rpc.Broadcast] RpcApplyX` for low-latency phase transitions

`[Sync]` is the durable source of truth; the RPC is the low-latency nudge so clients converge *this frame* rather than one tick later. Each transition calls both (vault108.suspectra `GameManager.cs`):

```csharp
void BeginDiscussion()
{
    if ( !Networking.IsHost ) return;
    CurrentState = GameState.Discussion;
    _discussionTimer = DiscussionDuration;
    ApplyDiscussionStateLocal();               // host converges immediately
    RpcApplyDiscussionState();                 // proxies converge immediately
}

[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
void RpcApplyDiscussionState() => ApplyDiscussionStateLocal();

void ApplyDiscussionStateLocal() { /* enable chat, show skip button, etc. */ }
```

Revision-counter variant for edge-only events (start/cancel) without an extra RPC: increment a `[Sync] int LobbyCountdownStartRevision` on start; the client detects the edge (`if ( rev != _lastRev ) { _lastRev = rev; PlayStartSFX(); }`). Cheaper than a broadcast when you only need "did this counter change" (suspectra `GameManager.cs`).

### 24. Spawn-disabled → `await Task.Frame()` → enable to fix proxy replication race

Cloning a prefab and immediately enabling it races with replication — proxies can receive the `Start()` call before they have the GameObject. Spawn with `StartEnabled = false`, wait one frame for the object to arrive on all clients, then enable (slamdunk.minigolf `RoundManager.cs::NextHole`):

```csharp
var hole = nextHolePrefab.GameObject.Clone();
hole.NetworkSpawn( new NetworkSpawnOptions { StartEnabled = false } );
await Task.Frame();          // let the GameObject arrive at proxies
hole.Enabled = true;         // now safe to start — everyone has it
hole.GetComponent<HoleDefinition>().Start();
```

The old hole is destroyed only *after* a scoreboard-display delay, never during the race window.

### 25. Double-write authority: owner writes to `Stats`, host writes to the authoritative store

Two authorities coexist in the same callback. The owning client is the only one who can write to Facepunch `Stats` (it's Steam-account-local). The host is the one who writes the authoritative score (slamdunk.minigolf `HoleTrigger.cs`):

```csharp
void OnTriggerEnter( Collider other )
{
    if ( !other.IsProxy )                          // I am the ball's owner
        StatManager.LogScore( holeName, strokes ); // write MY stat — only I can

    if ( Networking.IsHost )                       // I am the authoritative host
        RoundManager.Instance.OnHoleCompleted( ... ); // write shared scorecard
}
```

These two guards are independent; on the host-who-is-also-the-owner BOTH run. On a pure client only the first runs. On a dedicated server only the second runs.

### 26. Hash-gated `NetList` mirror to suppress per-frame network churn

When a host maintains a private `Dictionary` that must be mirrored to a `[Sync] NetList`, only rewrite the list when the content actually changes. Compute an order-independent hash over every field; skip the rewrite if it matches (fluffybagel.chess_otb `ArenaSystem.cs::HostSyncPlayers`):

```csharp
int ComputePlayersHash()
{
    var h = new HashCode();
    foreach ( var p in _players.Values.OrderBy( x => x.SteamId ) )
        h.Add( HashCode.Combine( p.SteamId, p.Score, p.Streak ) );
    return h.ToHashCode();
}

void HostSyncPlayers()
{
    int hash = ComputePlayersHash();
    if ( hash == _lastSyncHash ) return;           // nothing changed — skip the NetList rewrite
    _lastSyncHash = hash;
    ArenaPlayers.Clear();
    foreach ( var p in _players.Values ) ArenaPlayers.Add( p );
}
```

Without the hash gate, a host calling this every tick rewrites the `NetList` every frame even during a stable arena — generating constant replication traffic.

### 27. Targeted RPC for per-player cloud writes: `Rpc.FilterInclude` + re-validate `Rpc.Caller.IsHost`

`Stats.SetValue` is local to the Steam account — only the owning client can write their own stat. The host computes the new value and pushes it to exactly one client with `Rpc.FilterInclude`. The RPC body re-validates the caller is the host so a client can't invoke it directly (fluffybagel.chess_otb `ChessOtbModeRpcs.cs::HostPushEloStat`):

```csharp
void HostPushEloStat( Connection target, int newElo, int wDelta, int lDelta, int dDelta )
{
    if ( !Networking.IsHost ) return;
    Assert.True( wDelta + lDelta + dDelta == 1 );   // exactly one result per game
    using ( Rpc.FilterInclude( target ) )
        RpcWriteMyEloStat( newElo, wDelta, lDelta, dDelta );
}

[Rpc.Broadcast( NetFlags.HostOnly | NetFlags.Reliable )]
void RpcWriteMyEloStat( int newElo, int wDelta, int lDelta, int dDelta )
{
    if ( !Rpc.Caller.IsHost ) return;              // re-validate: only host may call this
    Stats.SetValue( "Otb_elo_blitz", newElo );
    Stats.Increment( "Otb_wins", wDelta );
    // …
}
```

### 28. Tag-based player state instead of list-of-references (migration-resilient)

Lists of `GameObject` or `Connection` references to "who is playing" are wiped or corrupted on host migration. Tags on each player's own `GameObject` survive — a fresh host can re-derive all round-membership state with a single `GetAllObjects(tag:"playing")` pass (mostudio.sweeper_otso `MINESWEEPER.cs`):

```csharp
// Host writes state via tags, never via a private list
void ExcludePlayer( GameObject player )
{
    player.Tags.Remove( "playing" );
    player.Tags.Add( "excluded" );
    BroadcastTagUpdate( player.Id, "excluded" );  // replicate the tag change explicitly
}

// Any system can query without a stale list
int PlayingCount => Scene.GetAllObjects( false )
    .Count( go => go.Tags.Has( "playing" ) );

// Fresh joiner auto-detected in OnUpdate — no list to update
protected override void OnUpdate()
{
    if ( !Networking.IsHost ) return;
    foreach ( var p in Scene.GetAllObjects( false ).Where( IsPlayer ) )
        if ( !p.Tags.Has( "playing" ) && !p.Tags.Has( "excluded" ) )
            ExcludePlayer( p );   // late joiner mid-round → auto-exclude
}
```

### 29. `[Sync] NetList<Vector3>` position registry as migration-proof truth

`Dictionary<Guid,…>` and per-object `[Sync]` fields don't survive host migration — Guids, network ids, and per-component ownership all shift. A `[Sync(SyncFlags.FromHost)] NetList<Vector3>` of world positions on the **manager** survives migration, auto-delivers to joiners, and lets the new host re-derive ownership by spatial matching (mostudio.sweeper_otso `Mine.cs`):

```csharp
// On the manager — survives host migration and late joins
[Sync( SyncFlags.FromHost )] public NetList<Vector3> FlaggedPositions { get; set; } = new();

// Query from anywhere — no per-flag ownership needed
bool IsFlagged( Vector3 tileWorldPos )
    => FlaggedPositions.Any( p => p.Distance( tileWorldPos ) < 10f )  // (a) registry
    || GameObject.Tags.Has( "flagged" )                                 // (b) fast tag
    || Scene.GetAllObjects(false).Any( go =>                            // (c) last-ditch scan
           go.Name == "Flag_Cover" && go.WorldPosition.Distance(tileWorldPos) < 10f );
```

The three-layer fallback order — synced registry → local tag → scene scan — is the key resilience design. Only the manager's `NetList` is truly migration-safe; the others degrade gracefully.

### 30. Optimistic tag-before-spawn to close the race between "flagged" and "stepped"

NetworkSpawn takes several milliseconds. A player who flags a tile and immediately steps on it can detonate in the gap because the flag's GameObject hasn't arrived yet. Broadcast the tag before spawning; revert it on failure (mostudio.sweeper_otso `FlagPlacer.cs`):

```csharp
[Rpc.Broadcast]
void RequestPlaceFlag( Guid tileId )
{
    if ( !Networking.IsHost ) return;
    var tile = Scene.Directory.FindByGuid( tileId );
    tile.Tags.Add( "flagged" );                     // tag first — immediate protection
    FlaggedPositions.Add( tile.WorldPosition );

    var flag = FlagPrefab.Clone( tile.WorldPosition );
    flag.NetworkSpawn();                             // may take a frame to arrive at proxies
    // if spawn throws, revert:
    // tile.Tags.Remove("flagged"); FlaggedPositions.Remove(tile.WorldPosition);
}
```

Note from the source: `Rpc.Caller` inside an `[Rpc.Host]` returned the host's own connection even for a proxy-initiated call on this SDK version — use `Network.Owner` of the *component* as the real placer identity.

### 31. Reliable networked `Destroy` of multi-owner objects: broadcast then host-seize

`host.Destroy(someoneElsesObject)` silently no-ops. `TakeOwnership()+Destroy()` back-to-back is a race condition because the transfer hasn't propagated yet. The robust pattern: broadcast so *every client destroys the objects it owns*, then a host-only pass seizes and destroys any genuinely ownerless leftovers (mostudio.sweeper_otso `MINESWEEPER.cs::BroadcastForceDestroyAllFlags`):

```csharp
[Rpc.Broadcast]
void BroadcastForceDestroyAllFlags()
{
    // Each client destroys their own flags
    foreach ( var flag in Scene.GetAllObjects( false )
                  .Where( go => go.Name == "Flag_Cover" && go.Network.Owner == Connection.Local ) )
        flag.Destroy();

    // Host seizes any ownerless ones left behind
    if ( Networking.IsHost )
        foreach ( var orphan in Scene.GetAllObjects( false )
                      .Where( go => go.Name == "Flag_Cover" && go.Network.Owner is null ) )
        {
            orphan.Network.TakeOwnership();
            orphan.Destroy();
        }
}
```

### 32. De-duped teleport with serial number: send 3× for packet-loss resilience

`[Rpc.Owner]` doesn't reliably loop back to the caller (especially during or just after host migration), and a single send may be lost during packet bursts. Use a serial number to de-dupe, and send the same teleport RPC up to 3 times (mostudio.sweeper_otso `Teleport.cs`):

```csharp
int _teleportSerial;
RealTimeSince _lastTeleport;

async Task TeleportPlayer( GameObject player, Vector3 pos )
{
    int serial = ++_teleportSerial;
    for ( int attempt = 0; attempt < 3; attempt++ )
    {
        if ( player.Network.Owner == Connection.Local )   // host teleporting self — skip RPC
            DoTeleport( pos );
        else
            ReceiveTeleport( pos, serial );               // [Rpc.Owner] call
        await Task.Frame();
    }
}

[Rpc.Owner]
void ReceiveTeleport( Vector3 pos, int serial )
{
    if ( serial <= _lastAppliedSerial ) return;           // de-dupe: discard old/duplicate
    _lastAppliedSerial = serial;
    DoTeleport( pos );
}

void DoTeleport( Vector3 pos )
{
    _controller.Enabled = false;
    WorldPosition = pos;
    GameObject.Network.ClearInterpolation();
    _controller.Enabled = true;
    _freezeVelocityUntil = 1.5f;                         // freeze velocity for 1.5s to prevent sweep-off
}
```

### 33. Counter + authoritative recount cross-check before any irreversible round decision

A maintained counter is fast but can drift from overlapping concurrent events, host-migration packet loss, or simultaneous flood-fills. Gate every round-ending action on a fresh scene recount, and if they disagree, resync the counter and abort (mostudio.sweeper_otso `MINESWEEPER.cs`):

```csharp
void RegisterUncovered( TileCover tile )
{
    SafeTilesRemaining--;
    if ( SafeTilesRemaining <= 0 ) TryTriggerWin();
}

void TryTriggerWin()
{
    int actual = Scene.GetAllComponents<TileCover>().Count( t => !t.IsRevealed );
    if ( actual != SafeTilesRemaining )
    {
        SafeTilesRemaining = actual;          // re-sync the counter
        if ( actual > 0 ) return;             // counter was wrong — not actually won yet
    }
    TriggerWin();                             // only reach here if scene agrees
}
```

Anti-pattern: trusting only the counter. A simultaneous flood-fill or a mid-migration packet can send `SafeTilesRemaining` negative (false win) or leave it stuck at 1 (round never ends).

---

### Gotcha additions (corpus refresh)

| Gotcha | Fix |
| --- | --- |
| `TimeUntil` absolute epoch becomes wrong after host migration | On `isHost && !_wasHost`: read `State.TimeLeft.Relative` (remaining seconds) and re-arm `State.TimeLeft = remaining` against the new host's clock (despawn.murder `RoundManager.cs`) |
| Phase transition is delayed by one replication tick for clients | Use the paired `ApplyXLocal` + `[Rpc.Broadcast(HostOnly)] RpcApplyX` pattern so both host and proxies converge this frame; `[Sync]` stays as the durable truth (suspectra `GameManager.cs`) |
| Proxy receives `Start()` on a freshly cloned hole before its GameObject has arrived | `NetworkSpawn( new NetworkSpawnOptions { StartEnabled = false } )` → `await Task.Frame()` → then enable (slamdunk.minigolf `RoundManager.cs`) |
| `Stats.SetValue` silently no-ops on non-owner clients | Only the owning client can write a Steam account stat; use `Rpc.FilterInclude(target)` to unicast the write request, re-validate `Rpc.Caller.IsHost` inside (chess_otb `ChessOtbModeRpcs.cs`) |
| Host `Dictionary<Guid,…>` of "who is playing" evaporates on migration | Store round membership as **tags on each player's GameObject**; a fresh host re-derives membership via `GetAllObjects(tag:"playing")` (sweeper_otso `MINESWEEPER.cs`) |
| `[Sync]` per-flag fields and Guid maps don't survive migration | Maintain a `[Sync(SyncFlags.FromHost)] NetList<Vector3>` position registry on the *manager*; match by `Distance < tol` in the new host (sweeper_otso `Mine.cs`) |
| Flag placed just before stepping doesn't protect because the GO hasn't arrived | Broadcast the "flagged" tag **before** calling `NetworkSpawn`; revert on failure (sweeper_otso `FlagPlacer.cs`) |
| `host.Destroy(otherOwnersObject)` silently no-ops | Broadcast so every client destroys its own; host-only pass seizes+destroys orphans (sweeper_otso `MINESWEEPER.cs`) |
| `[Rpc.Owner]` doesn't reliably loop back to the caller during migration | Detect `Network.Owner == Connection.Local` and call `DoTeleport` directly; send the RPC 3× with a serial de-dupe for packet-loss resilience (sweeper_otso `Teleport.cs`) |
| Per-host `NetList` mirror rewrites every frame even when nothing changed | Compute an order-independent hash; only rewrite when it changes (chess_otb `ArenaSystem.cs`) |
| `SafeTilesRemaining` counter can go negative or stick at 1 from concurrent reveals / migration | Re-count from the scene before any round-ending action; if counter disagrees with scene, re-sync and abort (sweeper_otso `MINESWEEPER.cs`) |

---

**Read these games for networking-authority patterns** (ranked by density of net-new material):
- `mostudio.sweeper_otso` — the definitive migration-survival playbook (patterns 28–33 above; `HostWatchdog.cs`, `MINESWEEPER.cs`, `FlagPlacer.cs`, `Teleport.cs`, `Mine.cs`)
- `despawn.murder` — host-migration-safe timers + host-event+RPC mirror pattern (`RoundManager.cs`, `RoundState.cs`)
- `vault108.suspectra` — paired local+RPC phase transitions + revision-counter edge detection (`GameManager.cs`)
- `fluffybagel.chess_otb` — hash-gated NetList mirroring + targeted Stats RPC (`ArenaSystem.cs`, `ChessOtbModeRpcs.cs`)
- `slamdunk.minigolf` — spawn-disabled+frame-wait race fix + double-write authority (`RoundManager.cs`, `HoleTrigger.cs`)
