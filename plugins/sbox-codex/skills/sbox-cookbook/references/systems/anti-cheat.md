# Anti-Cheat / Host Authority

Stop clients lying about state (money, ammo, score, saves). In s&box the host is the trust boundary: clients *propose*, the host *validates and writes*.

## What this IS and when you need it

"Anti-cheat" in s&box isn't a kernel driver — it's **server authority over every value a player could profit from manipulating**. You need it the moment a value is contested (multiplayer economy, ammo, score, leaderboards) or persisted across sessions (save files a user can edit on disk). For solo/casual/cosmetic state, full authority is overkill — the games below that skip it (elevator, xtrem_road) do so deliberately.

Three layers, applied as the stakes rise:
1. **Replication authority** — clients can read but not write the value (`[Sync(SyncFlags.FromHost)]`).
2. **Mutation authority** — every change runs on the host, which re-validates the caller and re-clamps the inputs (`[Rpc.Host]`).
3. **Persistence integrity** — saved data is sanitized + signed on load so an edited JSON file can't inject impossible values.

## Canonical approach

### Layer 1 — host-owned replicated state

Make the contested value `[Sync(SyncFlags.FromHost)]` with a **private setter**. Clients literally cannot assign it; they only see the host's replicated value.

```csharp
public sealed class PlayerProgression : Component
{
    [Sync( SyncFlags.FromHost )] public int Money { get; private set; }
    [Sync( SyncFlags.FromHost )] public int AxeLevel { get; private set; } = 1;
}
```
(`vault77.chop_the_forest`: Code/Player/PlayerProgression.cs:37-49) — every currency/level is `FromHost` + private-set, so all economy logic is host-authoritative by construction.

### Layer 2 — client proposes, host validates and writes

A client that must *initiate* a change cannot write the synced field. It calls a `[Rpc.Host]`. The host method does four things in order: **(a) re-check the caller, (b) bounds-check inputs, (c) clamp, (d) write**. Never trust the number that arrived.

```csharp
public void SetAmmo( AmmoResource resource, int value )
{
    if ( resource is null ) return;
    if ( !Networking.IsHost ) { SetAmmoRpc( resource, Math.Clamp( value, 0, resource.MaxReserve ) ); return; }
    Pool[resource.ResourcePath] = Math.Clamp( value, 0, resource.MaxReserve );  // host writes
}

[Rpc.Host]
private void AddAmmoRpc( AmmoResource resource, int count )
{
    var current = Pool.TryGetValue( resource.ResourcePath, out var c ) ? c : 0;
    var toAdd = Math.Min( count, resource.MaxReserve - current ); // re-clamp on the HOST
    if ( toAdd > 0 ) Pool[resource.ResourcePath] = current + toAdd;
}
```
(`apl.sandboxwars`: sandbox/Code/Game/Weapon/AmmoInventory.cs:26-31, :77-84) — the host RPC re-derives `space` and re-clamps; it never just stores the client's `count`. The `Pool` is `[Sync(SyncFlags.FromHost)] NetDictionary<string,int>` (:11).

**Re-check the caller.** A `[Rpc.Host]` can be invoked by *any* client, not just the owning one. Verify identity before acting:

```csharp
[Rpc.Host]
public void RpcRequestMissionRewardResources( string missionId, int logs, int money, int planks )
{
    if ( Networking.IsActive && Rpc.CallerId != Network.OwnerId ) return;     // (a) only the owner may claim
    var id = NormalizeMissionRewardId( missionId );
    if ( string.IsNullOrWhiteSpace( id ) ) return;
    if ( !_claimedMissionRewardIds.Add( id ) ) return;                        // idempotency: claim once

    if ( logs < 0 || money < 0 || planks < 0                                  // (b) bounds-check every field
         || logs  > Math.Max( DefaultMaxHarvestAmountPerHit, MaxHarvestAmountPerHit )
         || money > 2_000_000 || planks > 3500 )
    {
        _claimedMissionRewardIds.Remove( id );                               // roll back the claim
        FlagAsCheater( $"Unrealistic mission reward: {logs}/{money}/{planks}" );
        return;
    }
    if ( !GrantMissionRewardResources( logs, money, planks, out _ ) )
        _claimedMissionRewardIds.Remove( id );
}
```
(`vault77.chop_the_forest`: Code/Player/PlayerProgression.cs:3615-3639) — this is the full pattern: caller re-check, one-shot idempotency guard, per-field bounds, rollback on failure, and a flag for telemetry. Server-side caps (`MaxHarvestAmountPerHit`, `MaxSellAmountPerRequest`) are `[Property, Group("Anti-Cheat")]` so designers tune them (:75-81), and known dev SteamIds are whitelisted out of the checks (:2630-2635).

### Layer 2b — request → apply → confirm (optimistic clients)

When the owning client needs an instant local result but the value is host-owned, run a **triad**: client applies optimistically + calls `[Rpc.Host]` Request → host re-validates + re-clamps + applies → `[Rpc.Owner]` Confirm echoes the authoritative value back. Re-sanitize on *both* sides.

```csharp
[Rpc.Host]
public void RpcRequestMergeBackendPetLevelsData( string data )
{
    if ( Networking.IsActive && Rpc.CallerId != Network.OwnerId ) return;
    var safe = NormalizePetLevelsDataForRuntime( data );   // sanitize on host
    MergeBackendPetLevelsDataCore( safe, true );
    RpcConfirmBackendPetLevelsData( safe );                // echo back to owner
}

[Rpc.Owner]
public void RpcConfirmBackendPetLevelsData( string data )
    => MergeBackendPetLevelsDataCore( NormalizePetLevelsDataForRuntime( data ), true );  // sanitize AGAIN
```
(`vault77.chop_the_forest`: Code/Player/PlayerProgression.cs:730-745)

### Layer 3 — tamper-resistant persistence

Saves live in `FileSystem.Data` as plain JSON — a user can edit them. On **save**: clamp/normalize every field, then write a signature. On **load**: verify the signature, re-sanitize anyway, version-migrate, and rewrite the cleaned file.

```csharp
// FNV-1a over a canonical, version-conditional payload (deterrent, NOT cryptographic)
private static string ComputeSignature( LumberSteamProgressSave s, bool inclReset, bool inclBatches )
{
    var canonical = BuildCanonicalPayload( s, inclReset, inclBatches );
    ulong hash = 14695981039346656037UL;
    foreach ( var ch in canonical ) { hash ^= ch; hash *= 1099511628211UL; }
    return hash.ToString( "x16" );
}
```
(`vault77.chop_the_forest`: Code/Player/LumberSteamProgressSave.cs:957-968) — the canonical payload appends fields **conditionally** (`if save.Version >= N`) so old signed saves still verify (:971+, gated by `includeResetRevision`/`includeSaleBatches` at :952-954). `ValidateAndSanitize` clamps every field regardless of signature — the signature is a tamper *deterrent*, the sanitize is the real guard.

## Variations seen across games

- **Per-field clamp + roll back, then flag** (the strongest): bounds-check → undo the claim → `FlagAsCheater` → optionally report to a Discord webhook. `vault77.chop_the_forest` (PlayerProgression.cs:3627-3633).
- **Optimistic client return, host reconciles**: `TakeAmmo` returns `GetAmmo(resource) >= count` *before* the host confirms, so a client can briefly "have" ammo a desync will correct. Cheap and responsive; not safe for contested PvP economy. `apl.sandboxwars` (AmmoInventory.cs:53-61).
- **Persist-an-"in-session"-flag + reconcile on load** (anti-alt-F4): ores mined in a timed "Rift" are saved as pending; if the player disconnects mid-session, the next load subtracts them back out so you can't quit to keep illegitimate loot. Still client-side, so editing the JSON bypasses it. `clearlyy.s_miner` (RiftSessionManager.cs + StatsManager.cs:1279-1308).
- **Flag cheat-obtained wins so scoring ignores them**: the puzzle marks `SolvedByCheat` and the achievement layer only fires on legitimate solves. `simalami.15_puzzle_master` (AchievementService.cs).
- **Anti-farm exclusion**: bots are excluded from competitive Steam stats / battle-pass but still counted in ungated metrics. `Blind` (Code/Bot/BotManager.cs).
- **Deliberate NO authority** (know when to skip): `playbtg.elevator` uses a public-set `[Sync] int Balance` and runs purchases entirely client-side (`RemoveCoins` is `[Rpc.Owner]`, no host price re-check) — only the death-drop has a sanity clamp (`value > 3 reject`). Fine for a casual party game; an exploit in a ranked one. `stepdev.xtrem_road` saves locally per Steam account with no server authority for the same reason.
- **Provably-fair RNG** for gambling outcomes: commit-reveal SHA256 with rejection sampling to kill modulo bias, hash-chained audit log — engine-agnostic POCO. `vault77.chop_the_forest` (Code/Gambling/ProvablyFairRng.cs:126,:193). Use this instead of `System.Random` whenever an RNG result has economic value.

## Gotchas

- **`[Rpc.Host]` is callable by *any* client.** Always re-check `Rpc.CallerId == Network.OwnerId` (or your own permission rule) inside it. Owning the GameObject does not gate who can invoke the RPC. (PlayerProgression.cs:733)
- **Re-clamp on the host even though the client already clamped.** The client clamp is for UX; the host clamp is the security boundary. Clamp again in the Confirm RPC too — both ends. (AmmoInventory.cs:77-84; PlayerProgression.cs:738-744)
- **`SyncFlags.FromHost` means clients *cannot* write the field at all.** Any client-initiated change MUST round-trip an `[Rpc.Host]`; there is no "just set it locally" escape hatch. (PlayerProgression.cs:37)
- **FNV-1a / any in-code signature is a deterrent, not crypto.** A reader of your shipped C# can recompute it. Treat signed local saves as "raises the bar," and put truly contested state on a backend / Live Service, not the client disk.
- **Optimistic returns can briefly lie.** `TakeAmmo` returning `true` pre-confirmation means a desync can let a client act on ammo it doesn't have. Acceptable for fire-rate feel; not for "did this expensive purchase succeed." (AmmoInventory.cs:56)
- **Idempotency-guard one-shot grants** (`HashSet.Add` returns false on dup) so a replayed RPC can't double-claim. Roll the guard back if validation fails. (PlayerProgression.cs:3624,:3632)
- **String-keyed pools/IDs orphan on rename.** `NetDictionary` keyed by `resource.ResourcePath` (or save data keyed by asset id) silently loses entries if you rename the `.ammo`/`.shopitem` asset. (AmmoInventory.cs:11)
- **Whitelist dev SteamIds out of the caps**, or your own testing trips `FlagAsCheater`. (PlayerProgression.cs:2630-2635)

## Seen in

- `vault77.chop_the_forest` — host-authoritative economy, request/confirm triad, bounds-check+flag, signed saves, provably-fair RNG (`Code/Player/PlayerProgression.cs`, `Code/Player/LumberSteamProgressSave.cs`, `Code/Gambling/`)
- `apl.sandboxwars` — host-authoritative shared ammo pool with re-clamping RPCs (`sandbox/Code/Game/Weapon/AmmoInventory.cs`)
- `clearlyy.s_miner` — timed-session anti-exploit rollback on reconnect (`RiftSessionManager.cs`, `StatsManager.cs`)
- `simalami.15_puzzle_master` — cheat-flagged solves excluded from achievements (`AchievementService.cs`)
- `Blind` — bots excluded from competitive stats (anti-farm) (`Code/Bot/BotManager.cs`)
- `playbtg.elevator`, `stepdev.xtrem_road` — deliberately client-trusting (casual/cosmetic; study as the counter-examples)

---
**Verify live:** the authority API can shift between SDK builds — confirm `SyncFlags.FromHost`, `Rpc.CallerId`, `Network.OwnerId`, and `Networking.IsHost` against the installed SDK with `describe_type Sandbox.SyncFlags` / `search_types Rpc` / `describe_type Sandbox.Connection` before relying on them; reflection is authoritative, not this doc.

**See also:** `sbox-api` (resolve exact signatures for `[Rpc.Host]`/`[Sync]`/`NetDictionary`) and `sbox-build-feature` (screenshot-driven loop to confirm replicated state behaves in play mode).

---

## Corpus refresh (2026): more reference implementations

Net-new techniques from the latest mined games. These extend the spine above — they don't replace it.

### Trust the simulated world, not the network message (physical reconciliation)

The single most novel idea in the corpus. When players manipulate physics objects directly (drag a chess piece, throw a body), the host **cannot** trust a `SubmitMove(e2e4)` RPC — it re-derives the truth from where things physically sit. Project every live piece's world position onto a grid into bitboards, XOR against the turn-start snapshot, and compare the diff against generated legal moves. **The cheat signature is a SUPERSET** (extra unrelated bits); a legitimate-but-incomplete move is a SUBSET.

```csharp
// EvaluateDisplacements: XOR physical-vs-snapshot for the side to move
var moverDiff = physWhite ^ snapWhite;                  // bits that changed
foreach ( var move in GenerateLegalMoves() )
    if ( moverDiff == move.ExpectedDisplacementMask )   // exact → resolved legal move
        return Resolved( move );
    else if ( (moverDiff & ~move.ExpectedDisplacementMask) == 0 )
        return PartialMatch();                          // strict subset → mid-flight, suppress
var illegal = moverDiff & ~coveredByAnyLegalMove;       // leftover bits → highlight RED (cheat)
```
(`fluffybagel.chess_otb`: Code/Game/Gameplay/ChessGameState.Displacement.cs, Code/Game/Components/ChessBoardComponent.Move.cs) — `BuildPhysicalBitboards()` skips graveyard/captured pieces and any piece in `Grabbed` state (snapped to the hand bone, square unreliable). It only clears a player's pending move when the diff is *not* a partial match, so it never destroys a legit in-progress move. The full legal-move generation is the hot path (~32 pieces × 60fps), so the result is cached by `Time.Now` + board identity (`EvalThisFrame`). Distinct from RPC-revalidation: there is no number to clamp — you reconcile observed state against the rules.

### Bots reuse the human anti-cheat path (no separate trust model)

A subtle authority win: implement an AI opponent as a **client-owned pawn that submits via the exact same client→host RPCs humans use**, not as host-side logic. The host can't tell a bot drove the input, so every validation path is reused for free.

```csharp
// SpawnBotPawn: clone the PLAYER prefab, strip owner-only comps, network-spawn owned by the human
var bot = PlayerPrefab.Clone();
bot.Components.Get<CameraComponent>()?.Destroy();       // strip camera/input/interaction
bot.Components.Get<ChessInteractionSystem>()?.Destroy();
bot.AddComponent<BotPlayController>();                   // runs Leorik search locally, drives IK
bot.NetworkSpawn( seatedHuman.Network.Owner );           // owned by the seated human's connection
// → bot calls SubmitMove / TryCommitMove — the SAME RPCs, so displacement anti-cheat applies unchanged
```
(`fluffybagel.chess_otb`: ChessOtbGameManager.SpawnBotPawn, Code/Bot/BotPlayController.cs) — contrast with running bot logic on the host, which would need a separate "trust the host bot" bypass. Make the bot a peer and you get validation parity.

### Targeted RPC with caller-id AND payload-invariant re-validation

The existing doc re-checks `Rpc.CallerId`. Two refinements: (1) `Services.Stats`/`SetValue` are **local to the Steam user**, so to persist a stat you must run the write *on the owning client* via `Rpc.FilterInclude(target)`; a `GameObjectSystem` can't host RPCs, so a tiny component carries it. (2) Re-validate not just the caller but the **payload's invariants** — here, that the W/L/D deltas sum to exactly 1.

```csharp
[Rpc.Broadcast]
public void RpcWriteMyEloStat( int newElo, int dW, int dL, int dD )
{
    if ( !Rpc.Caller.IsHost ) return;            // (a) only the host may direct this
    if ( dW + dL + dD != 1 ) return;             // (b) result-invariant: exactly one outcome
    Stats.SetValue( "Otb_elo_blitz", newElo );   // runs ONLY on the owning client (local to Steam user)
}
// host side: using ( Rpc.FilterInclude( target ) ) hub.RpcWriteMyEloStat( elo, 1, 0, 0 );
```
(`fluffybagel.chess_otb`: Code/Game/Networking/ChessOtbModeRpcs.cs, EloSystem.cs) — the payload-invariant check (`sum == 1`) catches a tampered client claiming a win AND a draw, which a per-field bounds check would miss.

### Pity timer / bad-luck protection as persisted weighted selection

When a *random* assignment has fairness stakes (who's the killer/imposter, who gets the rare drop), persist per-player tickets so the same player isn't picked twice in a row, with a floor so newcomers still have a chance.

```csharp
// MurdererTicketManager: SteamId→tickets in FileSystem.Data, weighted pick, Max(1,t) floor
foreach ( var p in nonChosen ) _tickets[p.SteamId] += 1;        // losers gain tickets
_tickets[chosen.SteamId] -= playerCount;                        // winner is heavily penalized
ulong Pick( IEnumerable<Player> pool ) => WeightedRandom(
    pool, p => Math.Max( 1, _tickets[p.SteamId] ) );            // floor keeps fresh players eligible
```
(`despawn.murder`: Systems/MurdererTickets/MurdererTicketManager.cs) — pluggable via `IMurdererSelectionStrategy` (`TicketSelectionStrategy` vs `RandomSelectionStrategy`), chosen by a ConVar. The corpus's first pity-timer; reuse it for any "this random pick has competitive weight" (gacha bad-luck protection, role draws). Note it's a host local file — editing the JSON resets your pity, which is harmless (it only helps the editor).

### Host re-validates *eligibility*, and price comes from a ConVar (not the item)

The existing shop pattern clamps the amount. Add: the host RPC re-checks the **whole eligibility chain** (feature enabled? key known? caller has a pawn? item-specific `CanPurchase`?), and reads the **price from a replicated ConVar at purchase time**, so a server owner re-tunes the economy live without touching assets — and a client can't profit from a stale client-side price.

```csharp
[Rpc.Host]
public void PurchaseHost( string itemKey )
{
    if ( !GameConVars.IsPowerupEnabled( itemKey ) ) return;     // feature gate
    if ( !_items.TryGetValue( itemKey, out var item ) ) return; // known key
    var caller = GetPawn( Rpc.CallerId );
    if ( caller is null || !item.CanPurchase( caller ) ) return;// caller + item-specific rule
    var price = GameConVars.GetPowerupPrice( itemKey, item.Fallback );  // PRICE FROM CONVAR, live-tunable
    if ( caller.CluesCollected < price ) return;
    item.OnPurchase( caller ); caller.CluesCollected -= price;  // host writes
}
```
(`despawn.murder`: Systems/EquipmentShop/EquipmentShopManager.cs, Systems/Game/GameConVars.cs) — ~60 `Server|Replicated|ChangeNotice` ConVars double as a live balance DSL (`"radar=1,silent=2,..."` parsed at runtime). `facepunch.fair` does the same `Assert.True(Networking.IsHost)`-on-every-mutator discipline and checks land ownership **twice** in `PathBuilder.PlacePath`; its cheats are gated behind `ConVarFlags.Cheat` cmds (`hc3.debug.addmoney`) so they can't fire on a shipped server. (`facepunch.fair`: Park/ParkManager.cs:16, Park/Paths/PathBuilder.cs, :67-68.)

### Owner-gated `[Sync]` setter + owner-gated shared-trigger consumption

A lighter-weight alternative to `SyncFlags.FromHost` for **self-owned** session values (your own score, your own pickups): keep a plain `[Sync]` field but **early-return in the setter unless you own it**, and on shared triggers that every player's body overlaps, resolve the entering body to its owning player and bail unless it's the local one.

```csharp
[Sync] public int Score
{
    get => _score;
    set { if ( !Network.Owner.IsLocal() ) return; _score = value; }   // only the owner writes its own score
}

void OnTriggerEnter( Collider other )                                  // shared zone, local-only effect
{
    var skate = other.GameObject.Root.GetComponent<PlayerRagdoll>()?.SkateOwner;
    if ( skate is null || !skate.Network.IsOwner ) return;             // not MY body → ignore
    skate.Score += 10;                                                 // collectible registers once, for me
}
```
(`barrelproto.ragroll`: Code/.../NetworkPlayer.cs:50, Collectible/MovementTrigger :51) — this is the "shared trigger, local-only effect" idiom most multiplayer pickup/zone code gets wrong (a naïve handler fires N times, once per overlapping body). It also try/catches reading `Network.Owner.DisplayName` and host-destroys objects whose `SteamId == default` (`Services.Stats.Increment("invalid_player")`) — a **corrupted-connection guard** against half-joined Steam clients. (RollMode.OnPlayerJoined, NetworkPlayer.OnStart :52.) Use owner-gated `[Sync]` only for values a player can't profit from inflating about *itself in isolation*; contested/economy values still need `FromHost` + host RPC.

### Mixed trust model: client-auth solo, host-auth for shared stakes

You don't have to pick one trust model for the whole game. Trust the client for its **own solo progression** (responsive, cheap) but make **shared-stakes modes host-authoritative** — the host re-validates identity/cost against *its own* database and refunds on any mismatch.

```csharp
[Rpc.Host]
public void RequestCreateBattle( string json )
{
    var battle = Deserialize( json );
    foreach ( var name in battle.CaseNames )
        if ( !_serverCases.ContainsKey( name ) ) { SafeRefund( Rpc.CallerId ); return; }  // host's OWN DB
    var cost = battle.CaseNames.Sum( n => _serverCases[n].Price );   // recompute cost host-side
    // ... double-join / bad-JSON guards also SafeRefund; FinalizeBattle rolls on the host
}
```
(`lavagame.multis_cases`: Code/Game/Gambling/CaseBattle.cs, GameManager.cs) — solo balance/inventory is owned by the player's own GameManager and pushed to cloud; only case-battles + jackpot round-trip the host. **Disconnected-winner handling**: winnings persist to `mc_jackpot_pending.json` and are kept until the client ACKs receipt (`TryGrantPendingJackpotWin`) so a winner who crashes still gets paid. RPC DTOs use 2–3-char `[JsonPropertyName]` keys (`"sid"/"tv"/"win"`) to shrink broadcast JSON.

### Server-legitimacy / anti-piracy via the un-spoofable host SteamId

Protect an official economy from private/pirate servers minting items: read `Connection.Host.SteamId` (Steam-authenticated, **not** server-spoofable), check it against a cloud whitelist, and if the host isn't official, disable saving and disconnect.

```csharp
// ServerVerifier: gate all economy writes on the HOST's verified Steam identity
var hostId = Connection.Host.SteamId;                         // can't be faked by a modded server
var whitelist = await Supabase.Get( "server_whitelist" );
if ( !whitelist.Contains( hostId ) )
{
    GameManager.BlockSaving = true;                           // every write checks this flag
    ShowKickCountdown(); Game.Disconnect();
}
```
(`lavagame.multis_cases`: Code/Game/Security/ServerVerifier.cs) — narrower than full anti-cheat: it doesn't stop in-server cheating, it stops *unofficial servers* from corrupting the shared economy. Distinct from anything else in the corpus.

### Engine-as-renderer: hold the ledger off the s&box host entirely

The strongest anti-cheat for a contested economy is to **not trust the s&box host with money at all**. Run all game math (currency, idle accrual, gacha, progression) on an external authoritative server keyed by Steam ID; s&box becomes a thin presentation client that displays a balance it was pushed and relays intent.

```csharp
// BalanceHud.razor: the client is a CORRECTED CACHE, never the ledger
void UpdateBalance( string cents ) => _displayCents = cents;   // balance is a decimal STRING in cents
// boot: seed from a cosmetic local cache, regex-validated, overwritten by the server's first `init`
if ( Regex.IsMatch( cached, @"^\d+$" ) ) UpdateBalance( cached );  // display-only, NOT authority
```
(`sino.s_sino`: Code/UI/BalanceHud.razor, WebSocketManager.cs) — Steam→backend trust via `Services.Auth.GetToken("casino-server")` mints a signed token the Node server verifies (the game ships no secret). Client-side unlock gates (`GameUnlockGate.cs`) are a **UX pre-check, not the security boundary** — the server re-checks every action. This sidesteps the entire `[Sync]`/`[Rpc.Host]` discipline by moving the trust boundary off the engine; the cost is you now run (and secure) a real backend. See `leaderboards-services` / a server-authoritative-backend note for the socket plumbing.

### Monotonic sequence numbers so out-of-order RPCs can't fight (CRDT-lite)

When two `[Rpc.Broadcast]` calls can race (sit/stand, claim/release, equip/unequip), tag each with a per-object sequence number and **drop any apply whose sequence is older than what you've seen**. Last-writer-wins without a host round-trip.

```csharp
[Sync] public int SeatSequence { get; set; }
[Rpc.Broadcast]
void NetworkSit( int sequence, /* ... */ )
{
    if ( sequence < SeatSequence ) return;   // stale → ignore (out-of-order delivery)
    SeatSequence = sequence;
    // ... apply the seat
}
```
(`sino.s_sino`: seat/station components) — also pairs with a **grace reservation** (`ReservedUntilTime = Time.Now + 30`) so a player who leaves a station briefly doesn't lose it to a hovering opportunist; the owner auto-clears stale reservations in `OnUpdate`. A clean "one player per station" primitive built only from `[Sync]` + `[Rpc.Broadcast]` + `Time.Now`.

### Host-migration-safe re-validation (don't lose the round when the host leaves)

If the host can change (drop-in/drop-out social hub), a `TimeUntil` stores an *absolute* time off the **old** host's clock and reads garbage after migration. Re-arm it against the new host's clock from the remaining seconds, and re-assert ownership of the single networked authority object.

```csharp
// ValidateStateAfterMigration: re-arm the round timer on the new host's clock
var remaining = State.TimeLeft.Relative;          // seconds left (relative is migration-safe)
State.TimeLeft = Math.Max( remaining, 0 );        // re-arm against THIS host's Time.Now
// (also: migrated mid-PostRound → skip to a fresh round; stale State ref → reset index to -1)
```
(`despawn.murder`: Systems/Rounds/RoundManager.cs ValidateStateAfterMigration; `barrelproto.ragroll`: GameController.InitializeMode `Network.SetOrphanedMode(NetworkOrphaned.ClearOwner)` re-asserted `OnBecameHost`; `fluffybagel.chess_otb`: ArenaState spawned `NetworkOrphaned.Host` + `OwnerTransfer.Fixed`, `HostEnsure()` idempotently inherits the orphan.) Not "anti-cheat" per se, but it's the same authority discipline — the new host must re-derive trusted state, not inherit a stale snapshot.

### More gotchas (corpus refresh)

- **Physical reconciliation must skip held/in-flight objects.** A grabbed piece is snapped to the hand bone — its grid square is meaningless. Skip `Grabbed` state and tolerate transient "Missing" squares as a replication gap while a client-owned object is mid-flight; only a **superset** (extra bits) is a cheat. (chess_otb: ChessBoardComponent.Move.cs)
- **`Services.Stats.SetValue` is local to the Steam user** — you can't write another player's stat from the host. Route it through `Rpc.FilterInclude(target)` to a small component (a `GameObjectSystem` can't host RPCs). (chess_otb: ChessOtbModeRpcs.cs)
- **Re-validate payload invariants, not just bounds.** "W/L/D sum to exactly 1" catches a tampered result a per-field clamp passes. (chess_otb: EloSystem)
- **Owner-gated `[Sync]` setters only protect self-isolated values.** `if (!Network.Owner.IsLocal()) return;` in a setter stops a client writing *another* player's score, but a client can still inflate *its own* — fine for session score, NOT for contested economy (use `FromHost` + host RPC). (ragroll: NetworkPlayer.cs)
- **Read the HOST's SteamId for server legitimacy, not the local player's.** `Connection.Host.SteamId` is Steam-authenticated and can't be spoofed by a modded server; whitelist it to block pirate servers minting items. (multis_cases: ServerVerifier.cs)
- **Client-side gates are UX, the server re-checks.** A client unlock/affordability pre-check that avoids a round-trip must be mirrored exactly by a server re-check, and commented as such so it can't silently drift into being the only check. (sino.s_sino: GameUnlockGate.cs)
- **Tag racing RPCs with a monotonic sequence and drop stale applies** — otherwise out-of-order sit/claim/equip broadcasts fight. (sino.s_sino: SeatSequence)
- **Gate debug/cheat cmds behind `ConVarFlags.Cheat`** so `addmoney`-style helpers physically can't fire on a shipped server. (facepunch.fair: hc3.debug.*)
- **Pity-ticket files are host-local and self-correcting** — editing the JSON only resets your own bad-luck protection, which can't be exploited (it only ever *helps*). Still, keep the authoritative draw on the host. (despawn.murder: MurdererTicketManager)
- **Send complex data over RPC via an explicit `RpcSafe` projection struct**, not the live networked object — a deliberate network-safe companion type avoids leaking unreplicatable fields. (facepunch.fair: TrackElement.RpcSafe)

### Read these games (anti-cheat / authority)

- **`fluffybagel.chess_otb`** — ★ the gold: physical-board → legal-move bitboard-diff reconciliation, bots reuse the human RPC path, targeted-RPC with caller-id + payload-invariant checks, host-migration via orphan inheritance (`Code/Game/Gameplay/ChessGameState.Displacement.cs`, `ChessBoardComponent.Move.cs`, `Networking/ChessOtbModeRpcs.cs`, `Systems/EloSystem.cs`).
- **`despawn.murder`** — pity-timer / bad-luck-protection ticket selection, host RPC eligibility re-validation + ConVar-priced economy DSL, host-migration-safe round timer re-arm (`Systems/MurdererTickets/`, `Systems/EquipmentShop/`, `Systems/Game/GameConVars.cs`, `Systems/Rounds/RoundManager.cs`).
- **`lavagame.multis_cases`** — explicit mixed trust model (client-auth solo / host-auth PvP), host re-validates against its own case DB + `SafeRefund`, pending-win-until-ACK, server-legitimacy whitelist via un-spoofable host SteamId (`Code/Game/Gambling/CaseBattle.cs`, `Code/Game/Security/ServerVerifier.cs`).
- **`sino.s_sino`** — engine-as-renderer (ledger entirely off the s&box host), client-cache-not-authority economy, client gates are UX-only, monotonic-sequence RPCs + grace reservation (`Code/UI/BalanceHud.razor`, `WebSocketManager.cs`, `GameUnlockGate.cs`).
- **`barrelproto.ragroll`** — owner-gated `[Sync]` setter + owner-gated shared-trigger consumption, corrupted-connection guard, orphan-clear host migration (`Code/mode/NetworkPlayer.cs`, `Code/mode/RollMode.cs`, `GameController.cs`).
- **`facepunch.fair`** — `Assert.True(Networking.IsHost)` on every mutator, double-checked land ownership, `ConVarFlags.Cheat`-gated debug cmds, `RpcSafe` projection structs (`Park/ParkManager.cs`, `Park/Paths/PathBuilder.cs`, `Park/Track/TrackBuilder.cs`).
