# Casino-Gambling Genre Recipe

How to build a server-authoritative casino / case-opening hub in modern s&box (GameObject/Component/Scene): walk up to a machine, place a stake, watch a provably-fair roll, win or lose currency you never controlled — case opening with weighted rarity, PvP case battles, a jackpot pool, a recent-wins feed, and a deliberate house edge. Distilled from shipped titles `sino.s_sino`, `lavagame.multis_cases`, plus `artisan.darkrpog` and `emg.everything_must_go`.

## What defines the genre

A casino-gambling game is **a money-sink loop where the player risks a currency on a weighted random outcome and the house keeps a slice every time.** The 3D world is a lobby of interactable *machines* (a case crate, a roulette wheel, a slot, a coinflip, a battle terminal); pressing one opens a Razor (or web) panel that is **pure presentation**. The actual bet → roll → payout never runs on the player's machine, because the player's machine is the adversary.

The defining architectural decision, and the one every other choice flows from: **the client never owns money or rolls — the server does.** There are two postures for "the server", and you must pick up front because it dictates the netcode:

- **External authoritative backend** (`sino.s_sino`) — s&box is a **thin presentation client**. All money math, all rolls, all idle accrual, all progression live on an external Node/WebSocket server (`wss://...`). The s&box code renders 3D tables/chips and relays intent over a socket; it ships **no game math for money at all**. This sidesteps the entire `[Sync]`/`[Rpc.Host]` re-validation discipline by *not trusting the s&box host with money*. Use when you want a real backend, web-app screens, and cross-server persistent profiles.
- **Host-authoritative in-engine** (`lavagame.multis_cases`) — a **mixed trust model**: the player's own `GameManager` owns their solo balance/inventory (client-authoritative, cloud-persisted), but the genuinely shared, staked modes (case battle, jackpot) are **host-authoritative** — the host re-validates every case name/cost against its own database and refunds on mismatch. Use when there's no external server and the s&box host can be trusted as the dealer for PvP.

The core loop, in one line:
> walk up to a machine → stake currency the server holds → server rolls a weighted/provably-fair outcome → reveal animation plays locally (cosmetic) → server pays out (house keeps its edge) → balance is pushed back to your client → a recent-wins feed shows everyone the big pulls (sino.s_sino + lavagame.multis_cases: summaries).

The single hardest discipline: **the roll must be server-side and the reveal must be local cosmetic that cannot change the odds.** Decide the winner first, animate second.

## The system stack to compose

Compose in roughly this order. Each maps to a deeper system reference where one exists.

1. **Trust boundary first** (`references/systems/networking-authority.md`) — external-backend vs host-authoritative. Everything below changes shape based on this.
2. **Currency as a server-held, client-mirrored balance** (`references/systems/economy-currency.md`) — cents-as-strings; the client is a *corrected cache*, never the ledger.
3. **Weighted loot roll + house-edge / EV tuning** (`references/systems/gacha-loot.md`) — the case open: weighted draw, rarity tiers, and the value normalization that sets the payout %.
4. **Provably-fair commit/reveal** (pattern below; no dedicated ref) — server-seed-hash + client-seed + nonce so a suspicious player can verify a roll wasn't rigged.
5. **Interaction station / vendor** (`references/systems/shop-vendor.md`) — `IPressable` machine → opens a panel; optional one-player occupancy + grace reservation.
6. **Reveal animation (decide-then-animate)** (`references/systems/gacha-loot.md`) — the CS:GO-style scroll strip; cosmetic, decoupled from the roll.
7. **PvP staked modes** (`references/systems/round-match.md`) — case battles + jackpot as host-authoritative phase machines with optimistic local deducts + refund-on-failure.
8. **Recent-wins social feed** (`references/systems/leaderboards-services.md` neighbors; pattern below) — `[Sync]` last-win fields or server-pushed events.
9. **Save / persistence** (`references/systems/save-persistence.md`) — server-authoritative profile; local file is a display cache or write-only safety net, never the load source for money.
10. **Anti-cheat / server legitimacy** (`references/systems/anti-cheat.md`) — Steam-host-id whitelist, no client money authority, server re-validation.
11. **Leaderboards** (`references/systems/leaderboards-services.md`) — biggest wins / volume gambled via `Sandbox.Services.Stats` or a backend board.

## Build order

Build the money pipe and one machine before any second game mode. Vertical-slice order:

**1. Pick the trust posture and stand up the balance pipe.** The client *displays* a balance it was pushed; it never computes one. Hold money as **cents in a string** so a 12-digit jackpot never touches `long`/`float`.

```csharp
// sino.s_sino: the client's ONLY balance mirror. Subscribes to every server
// message that may carry a fresh balance; never does money math itself.
_subs.Add( mgr.On( "init",    HandleInit ) );      // first push overwrites the cache
_subs.Add( mgr.On( "balance", HandleBalance ) );   // p.Balance is a cents STRING
_subs.Add( mgr.On( "levelUpdate", HandleLevelUpdate ) );
void HandleBalance( JsonElement p ) => UpdateBalance( p.GetProperty("balance").GetString() );
```
(sino.s_sino: Code/UI/BalanceHud.razor — subscribes a dozen messages, `UpdateBalance(string cents)`; `CompareCentsStrings` compares by length-then-ordinal so cents never parse to a number.) A validated local `balance_cache.txt` (`^\d+$`) seeds the HUD instantly on boot so the player doesn't see `$0` flash, but it is **cosmetic only** — the first server `init` overwrites it. Details in `references/systems/economy-currency.md`.

In the host-authoritative posture the same idea is **optimistic local deduct → host validate → refund**: the client deducts its own balance for zero perceived latency, sends a `[Rpc.Host]` with the *declared* cost, and the host re-checks against its own DB and `SafeRefund`s on any mismatch (lavagame.multis_cases: Code/Game/Gambling/CaseBattle.cs + GameManager battle RPCs).

**2. The weighted roll (server-side, decoupled from any animation).** A subtract-walk over integer weights is the canonical case pick. Roll on the authority (external server, or `[Rpc.Host]`), never on the requesting client.

```csharp
// lavagame.multis_cases: weighted pick by integer weight. Rarer = smaller weight.
public ItemDefinition RollWinner( System.Random rng )
{
    int total = PossibleDrops.Sum( i => i.Weight );
    int roll  = rng.Next( 0, total );
    foreach ( var item in PossibleDrops ) { roll -= item.Weight; if ( roll < 0 ) return item; }
    return PossibleDrops[^1];
}
// Rank-biased overload multiplies Restricted+ weights by a per-rank bonus before rolling.
```
(lavagame.multis_cases: Code/Game/Economy/ItemDefinition.cs:117 RollWinner; 7 rarity tiers Consumer→Covert+Gold with per-rarity base weights in Cs2CaseApiBuilder.GetWeightForRarity — Consumer 25000 … Covert 150 … Gold 40.) For ascending cumulative-threshold rarity (and a real shipped ordering bug to avoid), see `references/genres/gacha-crawler.md`. Full weighted-roll + pity treatment in `references/systems/gacha-loot.md`.

**3. Set the house edge — EV normalization (the money-sink math).** A casino is a money sink by design: the *expected value* of a case must be below its price. Set a target ratio (~0.75 = 25% house edge), scale all item values so the weighted EV lands exactly there, then redistribute anything clipped by a per-item value cap back onto the uncapped items so rarity ratios survive.

```csharp
// lavagame.multis_cases (paraphrased): make weighted EV == Price * targetRatio.
float ev = caseDef.PossibleDrops.Sum( i => i.Value * (i.Weight / (float)totalWeight) );
float scale = (caseDef.Price * targetRatio) / ev;          // e.g. targetRatio 0.75
foreach ( var i in caseDef.PossibleDrops ) i.Value *= scale;
// then 3-pass overflow: clamp anything over maxOverride, push the lost EV
// proportionally onto the uncapped items so the weighted EV still lands.
```
(lavagame.multis_cases: Code/Game/Economy/Cs2CaseApiBuilder.cs NormalizeCaseExpectedValue + RebalanceKnifeValue — the most sophisticated loot-economy math in the corpus; redistributes Gold-pool value onto other rarities so total case EV is unchanged.) This is THE reusable "set the payout %" routine for any gacha/casino. See `references/systems/gacha-loot.md` for the full treatment.

**4. The machine (interaction station → opens a panel).** Every gambling device is the same shape: a world prop + an interactable that just opens the matching panel; the real logic lives in the panel (and on the server).

```csharp
// lavagame.multis_cases: a vendor is a tiny Interactable that opens a HUD panel.
public class CaseShopStation : Interactable
{
    public override string PromptLabel => "Open Cases";
    public override void OnUse( Player user ) => HudManager.Open( HudPanel.CaseShop );
}
```
(lavagame.multis_cases: Code/Game/Stations/*.cs — every vendor sets a prompt + calls `HudManager.Open(...)`; detection is **client-side** in `PlayerInteractor`, reliable in MP because each player runs it locally. `artisan.darkrpog` and `emg.everything_must_go` both put their whole casino — poker, roulette, slots, coinflip — behind this exact "world-item + interactable + world-screen Razor panel + host-authoritative service" shape: artisan.darkrpog Items/Casino/; emg Items/Casino/ + Economy/AutoShop/.) For a **one-player-per-seat machine with a grace reservation** (blackjack/poker tables), use the occupancy primitive in `references/genres/social-hub.md` (sino.s_sino: `GamingTerminalStation.cs` — `[Sync] OccupiedBySteamId` + a 30s `ReservedUntilTime` so a leaving player isn't sniped). Station + panel mechanics in `references/systems/shop-vendor.md`.

**5. Decide-then-animate reveal.** The server has already chosen the winner. The strip is a cosmetic lie: generate ~100 filler items, drop the **real** result at a fixed index, and ease the scroll in `OnUpdate()`. A CSS tweak must never be able to change odds.

```csharp
// In OnUpdate(), only while IsOpening (sandbox-safe easing — no MathF):
SpinProgress = Math.Clamp( SpinProgress + Time.Delta / SpinDuration, 0f, 1f );
float t = SpinProgress;
float eased = 1f - (1f - t) * (1f - t) * (1f - t);          // ease-out cubic
float offsetPx = MathX.Lerp( 0f, TargetOffsetPx, eased );
int idx = (int)(offsetPx / SlotWidthPx);
if ( idx != _lastTickIndex ) { SoundManager.PlayTick(); _lastTickIndex = idx; }
if ( t >= 1f ) CompleteOpen();                               // reveal the (already-known) winner
```
(namicry.gacha_crawler: Code/GameManager.cs:2181 places the winner at index 85, :2307 eases the strip + plays a tick per index; LootboxPanel.razor reads `GetStripOffset()`.) The index, slot width and centre are hardcoded to the Razor strip CSS — **keep the roll fully decoupled.** Full reveal pattern + pity-gated skip in `references/genres/gacha-crawler.md`.

**6. Layer on staked PvP (case battles + jackpot).** These are the genuinely shared modes and MUST be host-authoritative (or backend-authoritative). The host rolls, re-validates, and refunds on any failure path; clients optimistically deduct and mirror a countdown with `Time.Delta`.

```csharp
// lavagame.multis_cases: every battle entry point is [Rpc.Host]; host re-validates
// case identity + cost against its OWN database and SafeRefunds on mismatch.
[Rpc.Host] void RequestCreateBattle( string casesJson ) {
    if ( !TryValidateCases( casesJson, out var cases, out long cost ) ) { SafeRefund( Rpc.Caller, 0 ); return; }
    var b = new CaseBattle { /* sid/tv/win/tid terse JSON keys to shrink the broadcast */ };
    BroadcastBattleState( b );      // [Rpc.Broadcast] flattened JSON
}
```
(lavagame.multis_cases: Code/Game/Gambling/CaseBattle.cs + battle RPCs in GameManager.cs — FFA or 2v2 auto-balanced; `FinalizeBattle` rolls on the host; `JackpotManager.cs` is a host-authoritative Waiting/Open/Drawing/Cooldown phase machine with a client `Time.Delta` countdown mirror. **Disconnected-winner safety:** winnings persist to `mc_jackpot_pending.json` and are kept until the client ACKs receipt — `TryGrantPendingJackpotWin`.) Phase-machine + round mechanics in `references/systems/round-match.md`.

**7. The recent-wins feed.** Cheap social pressure ("someone just pulled a knife"). Two ways depending on posture:

```csharp
// lavagame.multis_cases (host-authoritative): last-win as [Sync] fields on the manager.
[Sync] public string LastWinName  { get; set; }
[Sync] public string LastWinColor { get; set; }
[Sync] public long   LastWinValue { get; set; }
[Sync] public int    LastWinSeq   { get; set; }   // bump to trigger a client toast
// Every client renders other players' latest pulls straight off replicated GameManager.All.
```
(lavagame.multis_cases: GameManager `[Sync]` LastWin* + SyncBalance/SyncRankScore/SyncInventorySnapshot — no extra RPC; clients read proxy state.) In the external-backend posture the feed is just another server message the HUD subscribes to (sino.s_sino: `BalanceHud.razor` `On("...")` handlers). Note the `[Sync]` "last win" approach gives **no history to late-joiners** unless you also persist a list — see the gotcha below.

## Provably-fair rolls (the trust layer)

A casino lives or dies on players believing the rolls aren't rigged. The standard commit/reveal scheme makes a roll **verifiable after the fact** without the server revealing its seed in advance:

1. Server picks a `serverSeed`, shows the player `SHA256(serverSeed)` (the commit) **before** the bet.
2. Client supplies (or is assigned) a `clientSeed`; a `nonce` increments per bet.
3. Roll = a deterministic function of `HMAC_SHA256(serverSeed, $"{clientSeed}:{nonce}")` → a uniform float → the weighted pick.
4. After the round (or on seed rotation) the server reveals `serverSeed`; the player can re-hash and confirm it matched the commit and reproduces the outcome.

s&box's sandbox **does not expose `System.Security.Cryptography`** — so do the crypto where the authority is:
- **External-backend posture (sino.s_sino):** trivial — hashing/HMAC and the whole provably-fair ledger live in Node; s&box just displays the commit hash and a "verify" link. This is the clean way to ship real provably-fair.
- **Host-authoritative in-engine:** you can't HMAC-SHA256 in-sandbox. Use a lighter tamper-evident hash for *integrity* (lavagame.multis_cases ships an **FNV-1a checksum + XOR cipher** in `SaveCrypto.cs` precisely because the crypto lib is unavailable) and treat "provably fair" as a server-honesty claim rather than a cryptographic guarantee, OR proxy the roll to a tiny backend. Don't claim cryptographic fairness you can't compute in-sandbox.

The non-negotiable underneath the math: **roll with a server-controlled seed** (`new Random(serverSeed)` on the host), never `Game.Random` on the requesting client, and never let the reveal animation feed back into the outcome.

## Currency & money-sink design

- **Server holds the ledger; client is a corrected cache.** Cents-as-strings end-to-end; never float math on a bankroll (sino.s_sino: `BalanceHud.razor`, `CompareCentsStrings`). The first server message after connect overwrites any local cache.
- **The house edge IS the sink.** EV-normalize every case to `Price × targetRatio < Price` (step 3). Layer additional sinks the same way other genres do: an **upgrader** (double-or-nothing — stake an item for a `p` chance at `1/p × value`, EV slightly under 1.0), a **trade-up contract** (10 items of rarity N → 1 of rarity N+1, with the output EV below the inputs' total), and a **jackpot rake** (the pot pays out less than deposited, or a cut is skimmed). All four are present across the references (lavagame.multis_cases: upgrader + trade-up + jackpot; emg/artisan: coinflip + roulette + slots).
- **Earn loop feeds the sink.** Players need a faucet to gamble: an **obby/earn loop** + passive **workers** (lavagame.multis_cases: Code/Game/Obby/ + `PassiveIncome` over assigned high-value skins, sqrt-damped), or an idle **"Casino Floor" tycoon** that accrues revenue/sec (sino.s_sino: `Code/Core/FloorPanel/FloorPanelWebSocket.cs` — ~35 server message handlers; revenue/sec mirrored into the always-visible HUD). See `references/systems/idle-offline.md`.

## Anti-cheat & server legitimacy

Because the whole genre is "the client wants to mint money," the security posture is the product:

- **Never give the client money authority.** External-backend (sino.s_sino) is the strongest form — the s&box build literally contains no money math to exploit. Host-authoritative (lavagame.multis_cases) re-validates every staked action on the host and refunds mismatches.
- **Server re-rolls and re-prices.** The host/backend rolls with its own seed and re-computes cost/EV against its own case DB; a client-declared price is only ever a hint to validate (lavagame.multis_cases: battle RPCs re-validate + `SafeRefund`).
- **Steam-host-id server whitelist (anti-piracy).** To stop private/pirate servers from minting items into the official economy, read the un-spoofable `Connection.Host.SteamId`, check it against a cloud whitelist, and disable saving + kick if unlisted (lavagame.multis_cases: Code/Game/Security/ServerVerifier.cs sets `GameManager.BlockSaving = true` then `Game.Disconnect()`). See `references/systems/anti-cheat.md`.
- **Identity to the backend.** `Services.Auth.GetToken("your-server")` mints a signed token the backend verifies, so the socket `register` proves who the player is without shipping a secret in the build (sino.s_sino: `AuthTokenService.cs`).

## Save / persistence

- **Authoritative profile is server-side** (balance, level, inventory, floor state), keyed by Steam ID, surviving across servers. The client never writes money state it loads back (sino.s_sino: profile entirely server-side; lavagame.multis_cases: `SaveSystem` — local `.bin` is **always written but never read**, cloud is the **only** load source, with a `_saveReady` guard so an empty init can't clobber a good save).
- **Cloud save (host-authoritative posture):** Supabase REST upsert with `Prefer: resolution=merge-duplicates`, inventory as a compact JSONB array, secrets in a gitignored `FileSystem.Data` config file, with a retry circuit-breaker (lavagame.multis_cases: Code/Game/Save/SaveCloud.cs).
- **Versioned migration:** as the case/economy schema evolves, guard reads by version (`field = version >= N ? r.Read...() : default`) so old saves still load (lavagame.multis_cases: SaveSerializer.cs `SAVE_VERSION = 9`, reads v2–9). For the asset/resource variant (`[JsonUpgrader]` to rename/backfill fields without breaking old scenes) see facepunch.ss1: `SpriteResource.Upgraders.cs`. Full local-first/cloud-load policy + crypto in `references/systems/save-persistence.md`.

## Embedding web-app screens (external-backend posture)

If the real gambling UIs live on the web (sino.s_sino runs React minigames), embed them as in-world `WebPanel`s via **signed one-time ticket URLs**: press the machine → `await` a `panelTicket` from the server over the socket → build `origin?panelTicket=...&lang=xx#/route` → render `<WebPanel Url=@Url />` once it parses. The web app authenticates itself with the ticket.
(sino.s_sino: `WebPanelUrlBuilder.cs` builds the URL, `GamingTerminalScreenPanel.razor` renders the `WebPanel`, `WebSocketManager.RequestPanelSessionAsync` correlates a `requestId` → `TaskCompletionSource` to turn the pub/sub socket into an awaitable RPC.) This is the pattern for "the real product is a web app on a diegetic monitor." See `references/engine/web-panel-embedding.md` if present, else the `sbox-api` skill for `WebPanel`.

## Standout patterns worth copying

- **Engine-as-renderer, server-as-truth:** ship the s&box build with **zero money math** and relay intent over a resilient socket (Disconnected→Connecting→Authenticating→Ready state machine, exponential backoff + jitter, outbound queue flushed on `init`, `On(type,handler)→IDisposable` pub/sub, request/response by `requestId`). The cleanest "real backend" template in the corpus (sino.s_sino: WebSocketManager.cs).
- **Client is a corrected cache, never the ledger:** money is a server-pushed cents-string the HUD mirrors; a validated local file is cosmetic boot-display only (sino.s_sino: BalanceHud.razor).
- **Decide-then-animate roll:** server picks the winner, the strip drops it at a fixed index and eases cosmetically — odds can't be touched by CSS (namicry.gacha_crawler: GameManager.cs:2181/:2307).
- **EV-preserving house-edge normalization:** scale a weighted pool to a target payout ratio, then redistribute capped value back onto uncapped items so rarity ratios survive (lavagame.multis_cases: Cs2CaseApiBuilder.NormalizeCaseExpectedValue + RebalanceKnifeValue).
- **Mixed trust done explicitly:** client-authoritative for solo progression, host-authoritative for staked PvP, with re-validation + refund on every failure branch and pending-win-until-ACK for disconnects (lavagame.multis_cases: CaseBattle/JackpotManager).
- **One-player-per-machine with a grace reservation:** `[Sync]` occupant + a `Time.Now + 30` reservation so a leaving player isn't sniped, built only from `[Sync]` + `[Rpc.Broadcast]` + `Time.Now` (sino.s_sino: GamingTerminalStation.cs).
- **Compact RPC DTOs:** staked structs use 2–3 char `[JsonPropertyName]` keys (`sid`/`tv`/`win`/`tid`) to shrink broadcast JSON (lavagame.multis_cases: CaseBattle.cs).
- **`[Sync]` recent-wins feed:** last-win fields on the replicated manager render everyone's big pulls off proxy state with no extra RPC (lavagame.multis_cases: GameManager LastWin*).
- **Steam-host-id whitelist anti-piracy:** un-spoofable `Connection.Host.SteamId` vs a cloud whitelist disables saving + kicks on unofficial servers (lavagame.multis_cases: ServerVerifier.cs).
- **Live-content pipeline:** fetch a real catalog (CS2 skins), cache 7 days in `FileSystem.Data`, downsample/curate, re-price into your own economy, then IP-sanitize names/URLs at runtime — ship real-world data with your own balance and IP-safe names (lavagame.multis_cases: Cs2CaseApiBuilder + SanitizeCopyrightedContent).

## Things NOT to copy

- **Client-side money/roll authority.** This is the whole genre's one sin. A `[Sync]` balance the client can set, or a roll done with `Game.Random` on the requesting client, is instantly exploitable. Roll on the authority; mirror to the client.
- **Claiming cryptographic "provably fair" you can't compute.** The s&box sandbox has no `System.Security.Cryptography` — HMAC/SHA the roll on a backend (sino.s_sino posture) or be honest that the in-engine version (FNV-1a/XOR, lavagame.multis_cases: SaveCrypto.cs) is integrity, not cryptographic fairness.
- **CSS-coupled reveal feeding the outcome.** The strip index/centre are tuned to Razor CSS; if the odds read from the animation, a layout change changes the odds. Keep them decoupled (namicry.gacha_crawler).
- **A recent-wins feed with no history for late-joiners.** `[Sync]` last-win fields only show the *current* last win; players who join after a big pull see nothing. If history matters, persist a list server-side (the gacha-crawler's broadcast-only chat has the same gap — namicry: `Entries` not `[Sync]`).
- **Hardcoded backend secrets in the build.** Keep service keys / tokens in a gitignored `FileSystem.Data` file or mint them via `Services.Auth.GetToken` — never commit a Bearer token (namicry.gacha_crawler ships one; don't).
- **Trusting incremental patches blindly.** sino.s_sino re-requests a full state snapshot (`Send("floorGetState")`) after patches rather than trusting deltas — the safer reactive pattern for a money UI.

## Which games to read

- **`sino.s_sino`** — THE external-backend casino. Read for: thin-client architecture, the resilient `WebSocketManager` (state machine + backoff/jitter + pub/sub + awaitable request/response), cents-as-strings balance mirroring, embedding React minigames as `WebPanel`s via signed tickets, the idle "Casino Floor" tycoon, networked sit-in-chair, and 10-language i18n. Source: `s_sino/Code/{Core,Blackjack,Roulette,Interaction,Player,UI,Util}`.
- **`lavagame.multis_cases`** — THE host-authoritative case-opening/gambling sim. Read for: weighted `RollWinner`, EV-normalization house-edge math, case battles + jackpot (host-authoritative + pending-win ACK), the `[Sync]` recent-wins feed, cloud save + versioned migration + FNV-1a/XOR integrity, the Steam-host-id anti-piracy whitelist, and the live CS2-API content pipeline. Source: `multis_cases/Code/Game/**` + `Code/UI/*.razor`.
- **`namicry.gacha_crawler`** — for the **decide-then-animate reveal** (decoy strip, winner-at-index, cubic-eased scroll, tick sound) and pity-as-consumable-flags. Source: `Code/GameManager.cs` (:2181/:2307), `Code/Data/ItemGenerator.cs`. See `references/genres/gacha-crawler.md`.
- **`artisan.darkrpog`** — for the **casino-as-world-items** pattern: poker/roulette/slots/coinflip all built as "world-item + interactable + world-screen Razor panel + host-authoritative service" (`Items/Casino/`), sitting beside `CarDealer`/`LootboxDealer` on the same shape.
- **`emg.everything_must_go`** — same casino folder shape (`Items/Casino/`: poker, roulette, slots, coinflip), plus robust model-loading with `model.IsValid` checks and layered fallbacks for a slot-machine prop (`SlotMachine.ConfigureModel`), and a Cloud `SoundEvent` cha-ching pattern.
- **`facepunch.ss1`** — for `[JsonUpgrader]` versioned **resource/component migration** (`SpriteResource.Upgraders.cs`) if your case/item definitions are `GameResource` assets that need to evolve without breaking old data.

Related recipes: `references/genres/gacha-crawler.md` (the roll/reveal/pity heart + single-player posture), `references/genres/social-hub.md` (persistent lobby + machine occupancy + host-migration discipline), `references/systems/gacha-loot.md` (weighted roll + EV tuning), `references/systems/economy-currency.md` (server-held balance), `references/systems/round-match.md` (PvP phase machines), `references/systems/save-persistence.md`, `references/systems/anti-cheat.md`.

## Verify live

The installed SDK is authoritative — confirm signatures with `describe_type` / `search_types` reflection before relying on them, not this doc or training data: `Component`, `[Sync]` / `[Rpc.Host]` / `[Rpc.Broadcast]`, `Component.INetworkListener`, `Rpc.Caller` / `Rpc.CallerId`, `Connection.Host.SteamId`, `Sandbox.Services.Auth.GetToken` + `Sandbox.Services.Stats` / `Leaderboards.GetFromStat`, `WebPanel` / `WorldPanel`, `Http.RequestJsonAsync`, `FileSystem.Data`, `Game.Random` vs `new Random(seed)`, and `[JsonIgnore]` / `[JsonPropertyName]` / `[JsonUpgrader]` (System.Text.Json). Sandbox gotchas that hit this genre specifically: **`System.Security.Cryptography` is unavailable** (do HMAC/SHA on a backend; FNV-1a/XOR in-sandbox is integrity only), **`System.Net` (WebSocket/HttpListener/TcpListener) is unavailable** — sino.s_sino uses s&box's own socket API, not raw `System.Net`; and **`MathF` is restricted** — use `MathX`/`Math` and the `1-(1-t)^3` easing form. Money must be cents-as-strings, not `float`. Stop play mode before scene edits; screenshot UI changes and read the PNG.

Cross-links: see the **sbox-api** skill for authoritative type/method signatures, and the **sbox-build-feature** skill for the screenshot-driven build loop and the sandbox gotcha list.
