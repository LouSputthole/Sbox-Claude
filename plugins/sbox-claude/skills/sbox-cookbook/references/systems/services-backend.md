# Services Backend (real REST/WS server, accounts, optimistic store)

How to stand up a **real authoritative backend** for an s&box game â€” `Services.Auth.GetToken` â†’ JWT exchange, a resilient `Sandbox.Http` client (cache + backoff + re-auth-on-401), a typed `ApiResult<T>`, an **optimistic local store that reconciles + debounce-flushes**, and **fire-and-forget telemetry**. This is the "I outgrew `Sandbox.Services.Stats`" topic.

## What this IS and when you need it

`Sandbox.Services` (see `leaderboards-services.md`) is great for vanity boards and per-account counters, but it's Steam-keyed, write-only-to-your-own-account, eventually-consistent (minutes), and editable-via-`Increment`. The moment you need **server-authoritative money**, **cross-device profiles**, **shared world state**, **a real ledger**, **anti-cheat-grade validation**, or **live-tunable config / kill-switches**, you stand up your own HTTP/WebSocket server and treat s&box as a **thin client**.

The defining mental model across all three primary games: **the s&box host is not the source of truth for anything that matters â€” the backend is.** The client mutates a local mirror *optimistically* so the UI is instant, then the next server message *corrects* it. Steam identity is proven to your server by a signed token, not a shipped secret.

Three flavors, same spine:
- **REST + JWT exchange** (despawn.murder) â€” token cache, backoff retry, `ApiResult<T>`, optimistic store with reconcile + debounce.
- **REST, token-per-call, dispute reconciliation** (fluffybagel.chess_otb) â€” no token cache, eventual-consistency polling, 409 self-heal, every peer submits.
- **WebSocket, server-as-truth** (sino.s_sino) â€” connectâ†’authâ†’ready state machine, pub/sub, reconnect token, request/response correlation, client-is-a-cache economy.

## The whitelist rule that gates everything

**`System.Net.HttpClient` / `WebSocket` are blocked by the s&box sandbox whitelist.** You MUST use `Sandbox.Http` (REST) or `Sandbox.WebSocket`. Every networking call in every game below routes through `Sandbox.Http.RequestAsync/RequestStringAsync`. If your web request "works in a unit test but fails in a published build," this is almost always why (vault77 calls this out as the #1 backend gotcha).

```csharp
using Sandbox;  // Http, WebSocket live here â€” NOT System.Net
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

### 1. Auth: Steam token â†’ session JWT (REST)

`Services.Auth.GetToken("yourscope")` returns a token your server validates against Steam â€” the bridge from Steam identity to your backend with no shipped secret. despawn.murder exchanges it once for a session JWT and caches that (`API/ApiClient.cs`):

```csharp
// Services.Auth.GetToken(scope) -> POST /v1/auth/token -> { jwt, expiresIn }
async Task<string> EnsureTokenAsync()
{
    if ( _jwt != null && DateTime.UtcNow < _jwtExpiry - RefreshBuffer ) return _jwt;
    _inflight ??= ExchangeAsync();          // single shared task: concurrent callers dedupe
    try { return await _inflight; } finally { _inflight = null; }
}
async Task<string> ExchangeAsync()
{
    var steamToken = await Sandbox.Services.Auth.GetToken( "murder" );
    var r = await Http.RequestStringAsync( ApiUrl( "/v1/auth/token" ), "POST", Json.Body( new { steamToken } ) );
    var tok = Json.Deserialize<TokenResp>( r );
    _jwt = tok.Jwt; _jwtExpiry = DateTime.UtcNow.AddSeconds( tok.ExpiresIn );
    return _jwt;
}
```

The **single shared in-flight task** (`_inflight`) is the key trick: if ten panels all need the token at boot, only one exchange fires; the rest await the same `Task`.

### 2. Resilient call: backoff + re-auth-on-401 + `ApiResult<T>`

Every endpoint goes through one send helper (despawn.murder `API/ApiClient.cs`). Retries 5xx/429 with `2^attempt Ã— 500ms`; a 401 invalidates the cached token and re-auths **once** so an expired JWT self-heals:

```csharp
public readonly record struct ApiResult<T>( bool Ok, T Value, int Status, string Error )
{
    public static ApiResult<T> Success( T v ) => new( true, v, 200, null );
    public static ApiResult<T> Fail( int s, string e ) => new( false, default, s, e );
}

async Task<ApiResult<T>> SendAsync<T>( string path, string verb, object body = null, int maxRetries = 3 )
{
    for ( var attempt = 0; ; attempt++ )
    {
        var jwt = await EnsureTokenAsync();
        var hdrs = new Dictionary<string, string> { ["Authorization"] = $"Bearer {jwt}" };
        var resp = await Http.RequestAsync( ApiUrl( path ), verb, Json.Body( body ), headers: hdrs );
        var code = (int)resp.StatusCode;
        if ( code == 401 ) { _jwt = null; if ( attempt == 0 ) continue; }          // re-auth once
        if ( (code >= 500 || code == 429) && attempt < maxRetries )
            { await Task.DelayAsync( (int)(MathF.Pow( 2, attempt ) * 500) ); continue; }
        if ( code is >= 200 and < 300 )
            return ApiResult<T>.Success( Json.Deserialize<T>( await resp.GetStringAsync() ) );
        return ApiResult<T>.Fail( code, await resp.GetStringAsync() );
    }
}
```

`ApiResult<T>` (a struct, no exceptions in the hot path) lets every caller branch on `result.Ok` without try/catch noise. Note `MathF` is **not in the sandbox** â€” use `MathX.Clamp` and write the pow as a small helper or a lookup; the line above is illustrative.

### 3. Optimistic store: mutate-local â†’ reconcile â†’ debounce-flush

The cleanest reference in the corpus is despawn.murder's `Systems/Inventory/MurderDataStore.cs` (a `SingletonComponent`). The UI must never wait on the network, so every mutation is applied locally first:

```csharp
[Sync] // not needed â€” this is a CLIENT-LOCAL mirror, not networked state
public sealed class DataStore : SingletonComponent<DataStore>
{
    public Profile Profile { get; private set; }
    public Action OnProfileChanged;
    TimeUntil _flushDue; bool _dirty;

    public void EquipSkin( string id )                  // OPTIMISTIC
    {
        Profile.EquippedSkin = id;                      // 1. mutate local now
        OnProfileChanged?.Invoke();                     // 2. UI updates this frame
        MarkDirty();                                    // 3. schedule a push
    }
    void MarkDirty() { _dirty = true; _flushDue = 5f; } // 5s debounce
    void CancelDirty() => _dirty = false;

    protected override void OnUpdate()
    {
        if ( _dirty && _flushDue ) OnDebouncedRefreshNeeded?.Invoke(); // service pushes to backend
    }
    public void ApplyServerProfile( Profile authoritative )            // RECONCILE
    {
        Profile = authoritative;                        // server wins
        CancelDirty();                                  // our pending write is now stale
        OnProfileChanged?.Invoke();
    }
}
```

Two more details that make it production-grade:
- **`IDisposable` watchers for Razor.** `WatchProfile(Action)` adds the handler and returns a subscription whose `Dispose()` removes it â€” panels subscribe in `OnEnabled`, dispose in `OnDisabled`, no leaks.
- **Compute derived state locally** so the bar moves before the round-trip. despawn.murder runs XP level-up math client-side (`LevelCalculator.AddExperience(...)`) in `ApplyRoundReportOptimistic` so the level bar animates instantly; the server's authoritative profile reconciles it later.

### 4. Fire-and-forget telemetry

Non-critical writes never block and never throw into gameplay (despawn.murder reports the round summary this way):

```csharp
void OnRoundEnded( RoundReport report )
{
    if ( !DataStore.Current.IsAvailable ) return;        // dead backend? skip silently
    _ = ReportRoundAsync( report );                      // discard the Task â€” fire and forget
}
async Task ReportRoundAsync( RoundReport r )
{
    try { await Api.SendAsync<object>( "/v1/rounds", "POST", r ); }
    catch ( Exception e ) { Log.Warning( $"telemetry drop: {e.Message}" ); } // swallow
}
```

## How the real games do it (composable variations)

### despawn.murder â€” REST + JWT + optimistic store (the reference stack)
`API/ApiClient.cs` + `API/ApiClient.Endpoints.cs` + `Systems/Inventory/MurderDataStore.cs`. The full canonical approach above is lifted from here: `Services.Auth.GetToken("murder")` â†’ `POST /v1/auth/token`, 5-min token cache w/ refresh buffer, **single shared in-flight exchange** for concurrent callers, exp-backoff on 5xx/429, auto re-auth + token invalidation on 401, `ApiResult<T>`. The store does optimistic equip/skin/round-report â†’ `OnXChanged` event â†’ `MarkDirty()` (5s) â†’ `OnDebouncedRefreshNeeded` â†’ push; `ApplyServer*` reconciles and `CancelDirty()`; `WatchProfile/WatchLoadout` return `IDisposable`. Round reports are fire-and-forget and only `if (store.IsAvailable)`. **Lift this whole stack as your starting point.**

### fluffybagel.chess_otb â€” token-per-call REST + dispute reconciliation
`Code/Game/Services/ChessOtbApi.cs` + `Code/Game/Services/RankedMatchmaking.cs`. A deliberately *different* trade-off: **mints a fresh `Auth.GetToken(scope)` per call (no caching)** and wraps non-2xx in a typed `ChessOtbApiException`. The matchmaking flow is a 9-state singleton (survives `Scene.Load`) with two backend-reality patterns worth copying:
- **Eventual-consistency tolerance** â€” after N null polls of `/queue/status` it falls back to the strongly-consistent `GET /match/:id` (`IdleGraceRetries`). Don't assume your KV store is read-your-writes.
- **409 self-heal** â€” a `409 already_in_match` triggers auto-abandon-and-retry rather than erroring out.
- **Dispute-by-redundancy** â€” *every peer* submits the finalized game independently so the backend reconciler can detect cheating/disagreement. Use when no single client is trusted.

```csharp
// chess_otb: no token cache â€” fresh token, typed exception, every call
var token = await Sandbox.Services.Auth.GetToken( "chess-otb" );
var resp = await Http.RequestAsync( Url( "/v1/games" ), "POST", Json.Body( game ), Bearer( token ) );
if ( (int)resp.StatusCode is 409 ) { await AbandonAsync(); /* retry */ }
else if ( !resp.IsSuccessStatusCode ) throw new ChessOtbApiException( resp.StatusCode, ... );
```

### sino.s_sino â€” WebSocket, server-as-truth, client-is-a-cache
`Code/Core/WebSocketManager.cs` + `Code/UI/BalanceHud.razor` + `Code/Core/AuthTokenService.cs`. The whole game is a thin renderer over `wss://â€¦fly.dev`; **no money math lives on the s&box host at all** â€” which sidesteps the entire `[Sync]`/`[Rpc.Host]` re-validation discipline by simply not trusting the host. The socket client is the spine:
- **State machine** `Disconnected â†’ Connecting â†’ Authenticating â†’ Ready`; outbound messages before `Ready` are **queued (cap 50)** and flushed on `init`.
- **Auth** connect â†’ wait for `Game.SteamId` â†’ `Services.Auth.GetToken("casino-server")` â†’ `register{token, steamId, displayName}` â†’ server `init` carries a `reconnectToken`.
- **Reconnect** exp backoff `1000 Ã— 2^(n-1)` capped 30s, **Â±30% jitter**, floor 500ms; stores `reconnectToken` to resume the same session; close code `4000` (auth-rejected) clears it to force a fresh handshake.
- **Pub/sub** `On(type, handler)` returns an `IDisposable`; **request/response over pub/sub** via a `requestId` â†” `TaskCompletionSource` map (`RequestPanelSessionAsync`) turns a fire-and-forget socket into an awaitable RPC.
- **Client-is-a-cache economy** â€” balance lives server-side as **cents-as-decimal-strings** (never floats/`long`); the HUD just calls `UpdateBalance(string cents)` on a dozen server messages; a regex-validated `balance_cache.txt` seeds the HUD on boot so it doesn't flash `$0`, but the first `init` overwrites it. The UI gate (`GameUnlockGate.CanPlay`) is a client mirror of server `LEVEL_UNLOCKS` for a fast pre-check â€” *not* the security boundary; the server re-checks.

```csharp
// sino: pub/sub sub returns IDisposable; balance is a corrected cache, never authoritative
_subs.Add( mgr.On( "balance", m => UpdateBalance( m.GetProperty( "cents" ).GetString() ) ) );
_subs.Add( mgr.On( "init",    HandleInit ) ); // carries reconnectToken + full snapshot
// request/response correlation:
var ticket = await mgr.RequestPanelSessionAsync( gameKey );  // awaits a requestId match
```

### vault77.chop_the_forest â€” non-host clients have no internet: host-proxy + idempotency
`Code/game/BackendClient/HttpBackendTransport.cs` + `RemoteConfig/RemoteConfigService.cs`. The critical MP gotcha: **only the host reliably has outbound internet** in a listen-server; non-host clients relay backend calls **through the host over RPC** (`CanUseHostProxy`, 96KB body cap, requestIdâ†’`TaskCompletionSource` correlation). `SboxAuthTokenProvider` wraps `Services.Auth.GetToken(service)` with **4 retries + escalating per-attempt timeouts**. Every economy write carries an **`Idempotency-Key`** (`"wheel:{nonce}:{steamId}:{action}"`) so a retried mutation is safe. Plus a `RemoteConfig` service pulls server-driven balance + feature flags (`server_gambling`) so you can tune/kill-switch economy without a client patch. Lift the host-proxy + idempotency-key pieces for any MP economy.

### lavagame.multis_cases â€” Supabase REST, secret-in-data-file, circuit breaker
`Code/Game/Save/SaveCloud.cs` + `SaveSystem.cs`. A concrete **Supabase** recipe: `POST /rest/v1/players` **upsert** (`Prefer: resolution=merge-duplicates`, `on_conflict=steam_id`), inventory as a compact JSONB array (short keys `uid/def/wear/val`). URL + `service_role` key read from a 2-line gitignored `FileSystem.Data/mc_database.txt` so **secrets stay out of the build**. A `RETRY_COOLDOWN` circuit-breaker (`MarkDown`/`CanTry`) stops a dead cloud from spamming. Policy: local `.bin` is **always written, never read** (safety net); **load is cloud-only** (authoritative); a `_saveReady` guard blocks writes until the cloud load completes so an empty init can't clobber a good save. Disconnected-winner rewards persist to a pending file and are kept **until the client ACKs** (at-least-once + dedupe).

### namicry.gacha_crawler â€” async-PvP writes to other players' rows
`Code/GameManager.cs`. Server-authoritative async PvP: a battle is built from a **snapshot of another player's saved character** pulled via REST; on win the opponent's DB row is rewritten via REST and, if they're online, an `[Rpc.Broadcast] UpdatePlayerArenaDefend` applies the loss locally. The pattern for "fight offline players" â€” the backend row is the truth, the live RPC is a cosmetic shortcut.

### aethercore.versus / facepunch.fair â€” dual-sink + session cache (when Stats is enough)
`versus/Code/Data/PlayerStats.cs` and `fair/Stats.cs`. If you don't need a full backend, the **dual-write** pattern bridges the gap: every `Increment/Set` updates a local `Dictionary`/`NetDictionary` (instant UI, save-persisted) **and** `Sandbox.Services.Stats` (global board) in one call site. aethercore force-`Flush()`es every write (UI instant; comment warns leaderboard propagation is otherwise minutes). This is the cheap "optimistic mirror" without standing up a server â€” graduate to the REST/WS stack only when you need authority.

## Gotchas

- **`System.Net.HttpClient`/`WebSocket` are whitelist-blocked.** Use `Sandbox.Http` / `Sandbox.WebSocket` exclusively. This is the #1 "works locally, fails in published build" bug (vault77).
- **`MathF` and `System.Math` are not in the sandbox** â€” use `MathX.Clamp` etc. The `MathF.Pow` in the backoff sketch above is illustrative; replace with a helper or a delay table.
- **Non-host clients may have no outbound internet.** In a listen-server, relay backend calls through the host over RPC (vault77 `HttpBackendTransport`), or your clients silently fail their HTTP.
- **Dedupe concurrent token requests.** Without a single shared in-flight task, a boot storm fires N identical `GetToken`â†’exchange round-trips (despawn.murder `_inflight`).
- **Re-auth on 401 exactly once, then give up.** A retry loop that re-auths forever on a permanently-bad token spins. Invalidate the cached token, retry once, then surface the failure.
- **Never ship a backend secret in the build.** Read URL + `service_role`/API keys from a gitignored `FileSystem.Data` file (lavagame). A committed Bearer/service key is the anti-pattern (namicry's committed token is called out as wrong in the corpus). It's only ever acceptable because the *host* is trusted â€” never on an untrusted client.
- **The client mirror is a cache, not the ledger.** Hold money/level server-side; the client displays what it was pushed and the next message corrects it. Don't let the UI's optimistic value become the source of truth (sino: cents-as-strings, first `init` overwrites the cache).
- **Optimistic + reconcile means the server can disagree.** Always have an `ApplyServer*` path that *overwrites* local state and `CancelDirty()`s your pending write â€” don't merge, the server wins.
- **Eventual consistency: your write may not read back immediately.** Poll a strongly-consistent endpoint as a fallback after N null reads (chess_otb), or you'll show a player "not in queue" right after they queued.
- **Idempotency keys for economy writes.** A network retry must not double-debit/double-grant â€” stamp a stable `Idempotency-Key` per action (vault77) so the server collapses duplicates.
- **Telemetry must never block or throw into gameplay.** `_ = SendAsync(...)`, wrap in try/catch, and gate on `IsAvailable` so a dead backend doesn't stall a round (despawn.murder).
- **Singletons that must survive a scene reload** (matchmaking, socket client) need to be `static`-backed or `DontDestroyOnLoad`-style â€” chess_otb keeps `RankedMatchmaking` and a requeue latch as statics because they outlive `Scene.Load`.
- **WS messages sent before `Ready` are lost** unless you queue + flush on connect (sino, cap the queue so a long outage doesn't OOM).

## Which games to read

- **despawn.murder** â€” `API/ApiClient.cs`, `API/ApiClient.Endpoints.cs` (JWT exchange + cache + shared-inflight + backoff + 401 re-auth + `ApiResult<T>`), `Systems/Inventory/MurderDataStore.cs` (optimistic store + reconcile + debounce + `IDisposable` watchers + local XP math), fire-and-forget round reports. **The reference stack â€” start here.**
- **fluffybagel.chess_otb** â€” `Code/Game/Services/ChessOtbApi.cs` (token-per-call, typed exception), `Code/Game/Services/RankedMatchmaking.cs` (9-state flow, eventual-consistency fallback, 409 self-heal, every-peer-submits dispute reconciliation), `Code/Game/Networking/PlayerIdentity.cs` (synced rating mirror).
- **sino.s_sino** â€” `Code/Core/WebSocketManager.cs` (state machine, backoff+jitter, reconnect token, pub/sub `On->IDisposable`, request/response correlation), `Code/Core/AuthTokenService.cs` (`Auth.GetToken` bridge), `Code/UI/BalanceHud.razor` (cents-as-strings client cache), `Code/Core/FloorPanel/FloorPanelWebSocket.cs` (handler-per-message-type remote-game client).
- **vault77.chop_the_forest** â€” `Code/game/BackendClient/HttpBackendTransport.cs` (host-proxy fallback, idempotency keys), `RemoteConfig/RemoteConfigService.cs` (server-driven config + feature flags). For MP economies.
- **lavagame.multis_cases** â€” `Code/Game/Save/SaveCloud.cs` (Supabase upsert + JSONB + secret-in-data-file + circuit breaker), `SaveSystem.cs` (always-write-local / load-cloud-only policy, pending-reward ACK).
- **namicry.gacha_crawler** â€” `Code/GameManager.cs` (async-PvP REST writes to offline players' rows + online-mirror RPC), `Code/Services/LeaderboardService.cs`.
- **aethercore.versus** / **facepunch.fair** â€” `Code/Data/PlayerStats.cs` / `Stats.cs` (dual-sink local-cache + Stats with force-flush â€” the cheap optimistic mirror before you build a server).

Open the cited file under `D:/sbox-lessons/zips-code/<game>/`.

---

**Verify live:** API drifts between SDK versions â€” confirm signatures against the installed SDK before relying on a member: `describe_type Sandbox.Http`, `describe_type Sandbox.WebSocket`, `describe_type Sandbox.Services.Auth`, `search_types Http`. Reflection is authoritative, not this doc. (`Http.RequestAsync` overloads and `Auth.GetToken` signatures in particular have shifted.)

**See also:** `leaderboards-services.md` (the `Sandbox.Services.Stats`/`Leaderboards` cloud â€” reach for that first for vanity boards), `save-persistence.md` (local `FileSystem.Data` + versioning/migration, the "cosmetic local cache" tier), `anti-cheat.md` (host-authoritative `[Rpc.Host]` re-validation â€” the in-engine authority model this topic *replaces* with a real server), and the **sbox-api** + **sbox-build-feature** skills.
