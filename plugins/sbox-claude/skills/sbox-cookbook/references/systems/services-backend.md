
# Services Backend (real REST/WS server, accounts, optimistic store)

<!-- reference-toc:start -->
## Contents

- [What this IS and when you need it](#what-this-is-and-when-you-need-it)
- [The whitelist rule that gates everything](#the-whitelist-rule-that-gates-everything)
- [Canonical modern approach](#canonical-modern-approach)
  - [Build order](#build-order)
  - [1. Auth: Steam token → session JWT (REST)](#1-auth-steam-token--session-jwt-rest)
  - [2. Resilient call: backoff + re-auth-on-401 + ApiResult](#2-resilient-call-backoff--re-auth-on-401--apiresult)
  - [3. Optimistic store: mutate-local → reconcile → debounce-flush](#3-optimistic-store-mutate-local--reconcile--debounce-flush)
  - [4. Fire-and-forget telemetry](#4-fire-and-forget-telemetry)
- [How the real games do it (composable variations)](#how-the-real-games-do-it-composable-variations)
  - [despawn.murder — REST + JWT + optimistic store (the reference stack)](#despawnmurder--rest--jwt--optimistic-store-the-reference-stack)
  - [fluffybagel.chessotb — token-per-call REST + dispute reconciliation](#fluffybagelchessotb--token-per-call-rest--dispute-reconciliation)
  - [sino.ssino — WebSocket, server-as-truth, client-is-a-cache](#sinossino--websocket-server-as-truth-client-is-a-cache)
  - [vault77.choptheforest — non-host clients have no internet: host-proxy + idempotency](#vault77choptheforest--non-host-clients-have-no-internet-host-proxy--idempotency)
  - [lavagame.multiscases — Supabase REST, secret-in-data-file, circuit breaker](#lavagamemultiscases--supabase-rest-secret-in-data-file-circuit-breaker)
  - [namicry.gachacrawler — async-PvP writes to other players' rows](#namicrygachacrawler--async-pvp-writes-to-other-players-rows)
  - [aethercore.versus / facepunch.fair — dual-sink + session cache (when Stats is enough)](#aethercoreversus--facepunchfair--dual-sink--session-cache-when-stats-is-enough)
- [Gotchas](#gotchas)
- [Which games to read](#which-games-to-read)
<!-- reference-toc:end -->

How to stand up a **real authoritative backend** for an s&box game — `Services.Auth.GetToken` → JWT exchange, a resilient `Sandbox.Http` client (cache + backoff + re-auth-on-401), a typed `ApiResult<T>`, an **optimistic local store that reconciles + debounce-flushes**, and **fire-and-forget telemetry**. This is the "I outgrew `Sandbox.Services.Stats`" topic.

## What this IS and when you need it

`Sandbox.Services` (see `leaderboards-services.md`) is great for vanity boards and per-account counters, but it's Steam-keyed, write-only-to-your-own-account, eventually-consistent (minutes), and editable-via-`Increment`. The moment you need **server-authoritative money**, **cross-device profiles**, **shared world state**, **a real ledger**, **anti-cheat-grade validation**, or **live-tunable config / kill-switches**, you stand up your own HTTP/WebSocket server and treat s&box as a **thin client**.

The defining mental model across all three primary games: **the s&box host is not the source of truth for anything that matters — the backend is.** The client mutates a local mirror *optimistically* so the UI is instant, then the next server message *corrects* it. Steam identity is proven to your server by a signed token, not a shipped secret.

Three flavors, same spine:
- **REST + JWT exchange** (despawn.murder) — token cache, backoff retry, `ApiResult<T>`, optimistic store with reconcile + debounce.
- **REST, token-per-call, dispute reconciliation** (fluffybagel.chess_otb) — no token cache, eventual-consistency polling, 409 self-heal, every peer submits.
- **WebSocket, server-as-truth** (sino.s_sino) — connect→auth→ready state machine, pub/sub, reconnect token, request/response correlation, client-is-a-cache economy.

## The whitelist rule that gates everything

**`System.Net.HttpClient` / `WebSocket` are blocked by the s&box sandbox whitelist.** You MUST use `Sandbox.Http` (REST) or `Sandbox.WebSocket`. Every networking call in every game below routes through `Sandbox.Http.RequestAsync/RequestStringAsync`. If your web request "works in a unit test but fails in a published build," this is almost always why (vault77 calls this out as the #1 backend gotcha).

```csharp
using Sandbox;  // Http, WebSocket live here — NOT System.Net
var http = new Http( new Uri( "https://api.example.com/v1/profile" ) );
var resp = await Http.RequestAsync( url, "POST", content: jsonContent, headers: hdrs );
```

## Canonical modern approach

### Build order

1. **Prove identity to your server.** `Services.Auth.GetToken(scope)` mints a short-lived signed token your backend verifies (you ship *no* secret). Exchange it for a session JWT, or send it raw on a WS `register`.
2. **Resilient client.** One `SingletonComponent` wrapping `Sandbox.Http`: token cache + refresh buffer, exp-backoff retry on 5xx/429, auto re-auth on 401, all calls return a typed `ApiResult<T>`.
3. **Optimistic store.** A second `SingletonComponent` holds the local mirror. Mutators apply locally + fire a change event + `MarkDirty()`; a debounced timer flushes to the server; server responses reconcile + `CancelDirty()`.
4. **Telemetry fire-and-forget.** Non-critical writes (round reports, analytics) are `_ = SendAsync(...)` and only attempted `if (store.IsAvailable)` so a dead backend never blocks gameplay.
5. **Idempotency + dispute safety.** Stamp economy writes with an `Idempotency-Key`; in MP let every peer report so the server reconciler can detect disputes.

### 1. Auth: Steam token → session JWT (REST)

`Services.Auth.GetToken("yourscope")` returns a token your server validates against Steam — the bridge from Steam identity to your backend with no shipped secret. despawn.murder exchanges it once for a session JWT and caches that (`API/ApiClient.cs`):

```text
FUNCTION ensureSessionToken()
  IF a cached session token exists
     AND current UTC time is before its expiry minus the refresh buffer:
    RETURN the cached token

  IF no token exchange is currently running:
    start one exchange and retain its shared future

  TRY:
    RETURN the result of awaiting that shared future
  FINALLY:
    clear the retained future after the exchange completes

FUNCTION exchangeIdentityForSession()
  request a signed identity token for the configured service scope
  POST that identity token to the backend authentication endpoint
  parse the returned session token and lifetime
  cache both the session token and its UTC expiry
  RETURN the session token
```

The **single shared in-flight task** (`_inflight`) is the key trick: if ten panels all need the token at boot, only one exchange fires; the rest await the same `Task`.

### 2. Resilient call: backoff + re-auth-on-401 + `ApiResult<T>`

Every endpoint goes through one send helper (despawn.murder `API/ApiClient.cs`). Retries 5xx/429 with `2^attempt × 500ms`; a 401 invalidates the cached token and re-auths **once** so an expired JWT self-heals:

```text
RESULT RECORD ApiResult
  succeeded
  decoded value
  HTTP status
  error text

FUNCTION sendBackendRequest(path, method, body, maximumRetries = 3)
  attempt = 0
  reauthenticated = false

  LOOP:
    sessionToken = ensureSessionToken()
    response = send request with a bearer authorization header

    IF response status is 401 AND reauthenticated is false:
      invalidate the cached session token
      reauthenticated = true
      CONTINUE

    transientFailure = response status is 429 OR at least 500
    IF transientFailure AND attempt is less than maximumRetries:
      wait according to an exponential delay table
      increment attempt
      CONTINUE

    IF response status is successful:
      RETURN a successful ApiResult containing the decoded body

    RETURN a failed ApiResult containing status and response text
```

`ApiResult<T>` (a struct, no exceptions in the hot path) lets every caller branch on `result.Ok` without try/catch noise. When implementing the delay, note that `MathF` is **not in the sandbox** — use a small helper or a precomputed delay table, plus sandbox-safe helpers such as `MathX.Clamp`.

### 3. Optimistic store: mutate-local → reconcile → debounce-flush

The cleanest reference in the corpus is despawn.murder's `Systems/Inventory/MurderDataStore.cs` (a `SingletonComponent`). The UI must never wait on the network, so every mutation is applied locally first:

```text
CLIENT-LOCAL PROFILE STORE
  state:
    profile mirror
    profile-changed event
    dirty flag
    flush deadline

  PROCEDURE equipSkin(skinId)
    update the mirrored equipped-skin value immediately
    notify local UI subscribers
    mark the store dirty
    set the flush deadline to 5 seconds from now

  PROCEDURE update()
    IF the store is dirty AND its flush deadline has elapsed:
      notify the backend service that a debounced push is needed

  PROCEDURE reconcileProfile(authoritativeProfile)
    replace the entire local profile with authoritativeProfile
    clear the dirty flag because the server state wins
    notify local UI subscribers
```

Two more details that make it production-grade:
- **`IDisposable` watchers for Razor.** `WatchProfile(Action)` adds the handler and returns a subscription whose `Dispose()` removes it — panels subscribe in `OnEnabled`, dispose in `OnDisabled`, no leaks.
- **Compute derived state locally** so the bar moves before the round-trip. despawn.murder runs XP level-up math client-side (`LevelCalculator.AddExperience(...)`) in `ApplyRoundReportOptimistic` so the level bar animates instantly; the server's authoritative profile reconciles it later.

### 4. Fire-and-forget telemetry

Non-critical writes never block and never throw into gameplay (despawn.murder reports the round summary this way):

```text
WHEN a round ends:
  IF the backend store is unavailable:
    STOP without affecting gameplay

  launch the round-report operation without awaiting it

ASYNC PROCEDURE reportRound(report)
  TRY:
    POST report to the round-report endpoint
  CATCH any failure:
    log a warning and do not propagate the failure into gameplay
```

## How the real games do it (composable variations)

### despawn.murder — REST + JWT + optimistic store (the reference stack)
`API/ApiClient.cs` + `API/ApiClient.Endpoints.cs` + `Systems/Inventory/MurderDataStore.cs` demonstrate the full canonical approach above: `Services.Auth.GetToken("murder")` → `POST /v1/auth/token`, 5-min token cache w/ refresh buffer, **single shared in-flight exchange** for concurrent callers, exp-backoff on 5xx/429, auto re-auth + token invalidation on 401, and `ApiResult<T>`. The store does optimistic equip/skin/round-report → `OnXChanged` event → `MarkDirty()` (5s) → `OnDebouncedRefreshNeeded` → push; `ApplyServer*` reconciles and `CancelDirty()`; `WatchProfile/WatchLoadout` return `IDisposable`. Round reports are fire-and-forget and only `if (store.IsAvailable)`. Use these files as an architecture reference and adapt the contracts to your backend.

### fluffybagel.chess_otb — token-per-call REST + dispute reconciliation
`Code/Game/Services/ChessOtbApi.cs` + `Code/Game/Services/RankedMatchmaking.cs`. A deliberately *different* trade-off: **mints a fresh `Auth.GetToken(scope)` per call (no caching)** and wraps non-2xx in a typed `ChessOtbApiException`. The matchmaking flow is a 9-state singleton (survives `Scene.Load`) with two important backend-reality patterns:
- **Eventual-consistency tolerance** — after N null polls of `/queue/status` it falls back to the strongly-consistent `GET /match/:id` (`IdleGraceRetries`). Don't assume your KV store is read-your-writes.
- **409 self-heal** — a `409 already_in_match` triggers auto-abandon-and-retry rather than erroring out.
- **Dispute-by-redundancy** — *every peer* submits the finalized game independently so the backend reconciler can detect cheating/disagreement. Use when no single client is trusted.

```text
ASYNC PROCEDURE submitGame(game)
  request a fresh signed identity token for this call
  POST the game payload with that token as bearer authorization

  IF the backend reports an already-in-match conflict:
    abandon the stale match state
    retry through a bounded recovery path
  ELSE IF the response is unsuccessful:
    raise a typed API error containing the response status
```

### sino.s_sino — WebSocket, server-as-truth, client-is-a-cache
`Code/Core/WebSocketManager.cs` + `Code/UI/BalanceHud.razor` + `Code/Core/AuthTokenService.cs`. The whole game is a thin renderer over `wss://…fly.dev`; **no money math lives on the s&box host at all** — which sidesteps the entire `[Sync]`/`[Rpc.Host]` re-validation discipline by simply not trusting the host. The socket client is the spine:
- **State machine** `Disconnected → Connecting → Authenticating → Ready`; outbound messages before `Ready` are **queued (cap 50)** and flushed on `init`.
- **Auth** connect → wait for `Game.SteamId` → `Services.Auth.GetToken("casino-server")` → `register{token, steamId, displayName}` → server `init` carries a `reconnectToken`.
- **Reconnect** exp backoff `1000 × 2^(n-1)` capped 30s, **±30% jitter**, floor 500ms; stores `reconnectToken` to resume the same session; close code `4000` (auth-rejected) clears it to force a fresh handshake.
- **Pub/sub** `On(type, handler)` returns an `IDisposable`; **request/response over pub/sub** via a `requestId` ↔ `TaskCompletionSource` map (`RequestPanelSessionAsync`) turns a fire-and-forget socket into an awaitable RPC.
- **Client-is-a-cache economy** — balance lives server-side as **cents-as-decimal-strings** (never floats/`long`); the HUD just calls `UpdateBalance(string cents)` on a dozen server messages; a regex-validated `balance_cache.txt` seeds the HUD on boot so it doesn't flash `$0`, but the first `init` overwrites it. The UI gate (`GameUnlockGate.CanPlay`) is a client mirror of server `LEVEL_UNLOCKS` for a fast pre-check — *not* the security boundary; the server re-checks.

```csharp
// sino: pub/sub sub returns IDisposable; balance is a corrected cache, never authoritative
_subs.Add( mgr.On( "balance", m => UpdateBalance( m.GetProperty( "cents" ).GetString() ) ) );
_subs.Add( mgr.On( "init",    HandleInit ) ); // carries reconnectToken + full snapshot
// request/response correlation:
var ticket = await mgr.RequestPanelSessionAsync( gameKey );  // awaits a requestId match
```

### vault77.chop_the_forest — non-host clients have no internet: host-proxy + idempotency
`Code/game/BackendClient/HttpBackendTransport.cs` + `RemoteConfig/RemoteConfigService.cs`. The critical MP gotcha: **only the host reliably has outbound internet** in a listen-server; non-host clients relay backend calls **through the host over RPC** (`CanUseHostProxy`, 96KB body cap, requestId→`TaskCompletionSource` correlation). `SboxAuthTokenProvider` wraps `Services.Auth.GetToken(service)` with **4 retries + escalating per-attempt timeouts**. Every economy write carries an **`Idempotency-Key`** (`"wheel:{nonce}:{steamId}:{action}"`) so a retried mutation is safe. Plus a `RemoteConfig` service pulls server-driven balance + feature flags (`server_gambling`) so you can tune/kill-switch economy without a client patch. Use host proxying and idempotency keys for any multiplayer economy with this network shape.

### lavagame.multis_cases — Supabase REST, secret-in-data-file, circuit breaker
`Code/Game/Save/SaveCloud.cs` + `SaveSystem.cs`. A concrete **Supabase** recipe: `POST /rest/v1/players` **upsert** (`Prefer: resolution=merge-duplicates`, `on_conflict=steam_id`), inventory as a compact JSONB array (short keys `uid/def/wear/val`). URL + `service_role` key read from a 2-line gitignored `FileSystem.Data/mc_database.txt` so **secrets stay out of the build**. A `RETRY_COOLDOWN` circuit-breaker (`MarkDown`/`CanTry`) stops a dead cloud from spamming. Policy: local `.bin` is **always written, never read** (safety net); **load is cloud-only** (authoritative); a `_saveReady` guard blocks writes until the cloud load completes so an empty init can't clobber a good save. Disconnected-winner rewards persist to a pending file and are kept **until the client ACKs** (at-least-once + dedupe).

### namicry.gacha_crawler — async-PvP writes to other players' rows
`Code/GameManager.cs`. Server-authoritative async PvP: a battle is built from a **snapshot of another player's saved character** pulled via REST; on win the opponent's DB row is rewritten via REST and, if they're online, an `[Rpc.Broadcast] UpdatePlayerArenaDefend` applies the loss locally. The pattern for "fight offline players" — the backend row is the truth, the live RPC is a cosmetic shortcut.

### aethercore.versus / facepunch.fair — dual-sink + session cache (when Stats is enough)
`versus/Code/Data/PlayerStats.cs` and `fair/Stats.cs`. If you don't need a full backend, the **dual-write** pattern bridges the gap: every `Increment/Set` updates a local `Dictionary`/`NetDictionary` (instant UI, save-persisted) **and** `Sandbox.Services.Stats` (global board) in one call site. aethercore force-`Flush()`es every write (UI instant; comment warns leaderboard propagation is otherwise minutes). This is the cheap "optimistic mirror" without standing up a server — graduate to the REST/WS stack only when you need authority.

## Gotchas

- **`System.Net.HttpClient`/`WebSocket` are whitelist-blocked.** Use `Sandbox.Http` / `Sandbox.WebSocket` exclusively. This is the #1 "works locally, fails in published build" bug (vault77).
- **`MathF` and `System.Math` are not in the sandbox** — use `MathX.Clamp` and other sandbox-safe helpers. Implement exponential backoff with a small helper or a precomputed delay table.
- **Non-host clients may have no outbound internet.** In a listen-server, relay backend calls through the host over RPC (vault77 `HttpBackendTransport`), or your clients silently fail their HTTP.
- **Dedupe concurrent token requests.** Without a single shared in-flight task, a boot storm fires N identical `GetToken`→exchange round-trips (despawn.murder `_inflight`).
- **Re-auth on 401 exactly once, then give up.** A retry loop that re-auths forever on a permanently-bad token spins. Invalidate the cached token, retry once, then surface the failure.
- **Never ship a backend secret in the build.** Read URL + `service_role`/API keys from a gitignored `FileSystem.Data` file (lavagame). A committed Bearer/service key is the anti-pattern (namicry's committed token is called out as wrong in the corpus). It's only ever acceptable because the *host* is trusted — never on an untrusted client.
- **The client mirror is a cache, not the ledger.** Hold money/level server-side; the client displays what it was pushed and the next message corrects it. Don't let the UI's optimistic value become the source of truth (sino: cents-as-strings, first `init` overwrites the cache).
- **Optimistic + reconcile means the server can disagree.** Always have an `ApplyServer*` path that *overwrites* local state and `CancelDirty()`s your pending write — don't merge, the server wins.
- **Eventual consistency: your write may not read back immediately.** Poll a strongly-consistent endpoint as a fallback after N null reads (chess_otb), or you'll show a player "not in queue" right after they queued.
- **Idempotency keys for economy writes.** A network retry must not double-debit/double-grant — stamp a stable `Idempotency-Key` per action (vault77) so the server collapses duplicates.
- **Telemetry must never block or throw into gameplay.** `_ = SendAsync(...)`, wrap in try/catch, and gate on `IsAvailable` so a dead backend doesn't stall a round (despawn.murder).
- **Singletons that must survive a scene reload** (matchmaking, socket client) need to be `static`-backed or `DontDestroyOnLoad`-style — chess_otb keeps `RankedMatchmaking` and a requeue latch as statics because they outlive `Scene.Load`.
- **WS messages sent before `Ready` are lost** unless you queue + flush on connect (sino, cap the queue so a long outage doesn't OOM).

## Which games to read

- **despawn.murder** — `API/ApiClient.cs`, `API/ApiClient.Endpoints.cs` (JWT exchange + cache + shared-inflight + backoff + 401 re-auth + `ApiResult<T>`), `Systems/Inventory/MurderDataStore.cs` (optimistic store + reconcile + debounce + `IDisposable` watchers + local XP math), fire-and-forget round reports. **The reference stack — start here.**
- **fluffybagel.chess_otb** — `Code/Game/Services/ChessOtbApi.cs` (token-per-call, typed exception), `Code/Game/Services/RankedMatchmaking.cs` (9-state flow, eventual-consistency fallback, 409 self-heal, every-peer-submits dispute reconciliation), `Code/Game/Networking/PlayerIdentity.cs` (synced rating mirror).
- **sino.s_sino** — `Code/Core/WebSocketManager.cs` (state machine, backoff+jitter, reconnect token, pub/sub `On->IDisposable`, request/response correlation), `Code/Core/AuthTokenService.cs` (`Auth.GetToken` bridge), `Code/UI/BalanceHud.razor` (cents-as-strings client cache), `Code/Core/FloorPanel/FloorPanelWebSocket.cs` (handler-per-message-type remote-game client).
- **vault77.chop_the_forest** — `Code/game/BackendClient/HttpBackendTransport.cs` (host-proxy fallback, idempotency keys), `RemoteConfig/RemoteConfigService.cs` (server-driven config + feature flags). For MP economies.
- **lavagame.multis_cases** — `Code/Game/Save/SaveCloud.cs` (Supabase upsert + JSONB + secret-in-data-file + circuit breaker), `SaveSystem.cs` (always-write-local / load-cloud-only policy, pending-reward ACK).
- **namicry.gacha_crawler** — `Code/GameManager.cs` (async-PvP REST writes to offline players' rows + online-mirror RPC), `Code/Services/LeaderboardService.cs`.
- **aethercore.versus** / **facepunch.fair** — `Code/Data/PlayerStats.cs` / `Stats.cs` (dual-sink local-cache + Stats with force-flush — the cheap optimistic mirror before you build a server).

Use [`SOURCE-PROVENANCE.md`](../SOURCE-PROVENANCE.md) to locate the public package/source entry for each cited game.

---

**Verify live:** API drifts between SDK versions — confirm signatures against the installed SDK before relying on a member: `describe_type Sandbox.Http`, `describe_type Sandbox.WebSocket`, `describe_type Sandbox.Services.Auth`, `search_types Http`. Reflection is authoritative, not this doc. (`Http.RequestAsync` overloads and `Auth.GetToken` signatures in particular have shifted.)

**See also:** `leaderboards-services.md` (the `Sandbox.Services.Stats`/`Leaderboards` cloud — reach for that first for vanity boards), `save-persistence.md` (local `FileSystem.Data` + versioning/migration, the "cosmetic local cache" tier), `anti-cheat.md` (host-authoritative `[Rpc.Host]` re-validation — the in-engine authority model this topic *replaces* with a real server), and the **sbox-api** + **sbox-build-feature** skills.
