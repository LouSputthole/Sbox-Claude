# s&box Claude Bridge — Reliability + Polish / Trust (Design Spec)

**Date:** 2026-06-04
**Status:** DESIGN ONLY — not implemented. No code edits made.
**Direction:** #4 — make the bridge feel solid and trustworthy.
**Scope:** (1) a self-cleaning `run_self_test` health-check battery; (2) a prioritized fix punch-list for known rough edges; (3) a richer `get_bridge_status`.

> All file/line references are repository-relative as read on 2026-06-04. The C# addon is `sbox-bridge-addon\Editor\MyEditorMenu.cs`; the MCP server is `sbox-mcp-server\src\`. Anything marked **[CONFIRM LIVE]** must be verified against a running bridge during implementation (per the constraint that we must not call the live `mcp__sbox__*` tools here).

---

## Goal

Give a user (or Claude) **one command** they can run right after install that proves the bridge actually works end-to-end, and that catches regressions before a release ships — plus close the handful of small, documented rough edges that make the bridge feel flaky or confusing. Success = "I ran `run_self_test`, it said `8/8 PASS`, and I trust the install," and the top punch-list items (`create_material`, stale `is_playing`, dock readout, error-message quality) are no longer footguns.

## Why

The public reviews carried "it was broken/confusing for me" notes. The bridge has 150+ tools but no single, safe smoke test — so a partially-broken install (wrong IPC dir, addon didn't fully compile, version skew, a regressed handler) is only discovered tool-by-tool, mid-task, as confusing timeouts or wrong-looking results. The existing diagnostics (`get_bridge_status`, `get_compile_errors`, `read_log`) answer "is the bridge *reachable*?" but not "does the bridge actually *do the work* correctly?" A round-trip battery answers the second question, and is exactly the kind of artifact that catches regressions in CI-of-the-mind before each release. The punch-list items are small individually but each one has burned a real user (they're already documented in `CLAUDE.md` Known Issues + `TROUBLESHOOTING.md`).

---

## `run_self_test` design

### Orchestration approach — decision

**MCP-server-side orchestration (a new TS tool), NOT a single C# handler.** Same pattern as the existing `execute_csharp` and `screenshot_orbit` tools in `sbox-mcp-server/src/tools/diagnostics.ts`: the tool body calls `bridge.send("<command>", params)` for each existing bridge command in sequence, inspects each `BridgeResponse` (`{ id, success, data?, error? }`, see `transport/bridge-client.ts:121`), and assembles a per-check report.

Rationale:

- **It tests the real path users hit** — request file → IPC dir → poller → main-thread dispatch → handler → response file → parse. A single C# handler that called handlers in-process would skip the IPC/serialization layer, which is where ~half the historical bugs lived (BOM, temp-dir split, half-written payloads, `success` masking). Testing through `bridge.send` exercises all of it.
- **It needs no addon change to ship** (lower risk, no version-skew dependency — works against any addon ≥ the version that has the commands it calls). A C# handler would require republishing the addon and would re-introduce a skew window.
- **It composes existing, individually-reviewed handlers** rather than adding a new privileged code path.
- It can degrade gracefully and still report (e.g. screenshot folder not locatable → that sub-check is `SKIP`, not a hard fail).

Cost: a self-test is ~10–14 sequential `bridge.send` round-trips (+ one `trigger_hotload`/recompile, the slow step). That is acceptable for an on-demand command. It is **never** run automatically.

Tool registration: add `registerSelfTestTools(server, bridge)` in a new module `sbox-mcp-server/src/tools/selftest.ts`, wired in `src/index.ts` alongside the others. (Could also live in `diagnostics.ts`, but a dedicated module keeps the orchestration — which is the longest single tool in the codebase — readable.)

### Pre-flight (before any mutation)

1. `get_bridge_status`/connection check via `bridge.isConnected()` + one `get_project_info` round-trip. If the bridge is not connected or the round-trip fails, **abort immediately** with a clear message ("bridge not responding — run get_bridge_status; nothing was created") and do **not** proceed to any mutating step. This guarantees we never half-create objects against a dead editor.
2. Refuse to run while **play mode** is active: call `is_playing` first; if `gameFlag` is true, abort with "stop play first (stop_play) — self-test mutates the scene." (Scene-mutating commands are refused during play anyway — `MyEditorMenu.cs:581` — but checking up front avoids a confusing mid-battery failure and a possibly-orphaned object.)
3. Record a **provenance tag/name prefix** for everything we create: name every created GameObject `__selftest_<runId>` where `runId = Date.now().toString(36)`. This makes orphans identifiable and lets the final sweep find/delete anything left behind (see Cleanup guarantee).

### Check list (exact battery)

Each check calls existing bridge commands and records `{ name, status: PASS|FAIL|SKIP, detail }`. Checks run in order; later checks reuse the temp GameObject created in Check 1. A `FAIL` in one check does **not** abort the battery (we still want a full report) **except** Check 1 (no temp object → nothing else can run) and the pre-flight connection check.

| # | Check | Commands exercised | How it is verified (pass condition) |
|---|---|---|---|
| 0 | **Connectivity** (pre-flight) | `get_project_info` | `res.success === true` and a project path is returned. Fail/abort otherwise. |
| 1 | **Create GameObject** | `create_gameobject` `{ name: "__selftest_<runId>", position: {x:0,y:0,z:0} }` | `res.success`, `res.data.gameObject.id` is a parseable GUID. Capture `tempId`. Abort battery if this fails. |
| 2 | **Add component** | `add_component_with_properties` `{ id: tempId, component: "ModelRenderer" }` | `res.success` and `res.data.added === true`. |
| 3 | **Property round-trip (write→read)** | `set_property` then `get_property` | See "Round-trip detail" below. Pass = the value read back equals the value written. |
| 4 | **Assign a model + bounds** | `assign_model` `{ id: tempId, model: "models/dev/box.vmdl" }`, then `get_bounds` `{ id: tempId }` | `assign_model.success`; `get_bounds.success` and `data.empty === false` with a non-zero `size` (proves the renderer + model produced real geometry). If `models/dev/box.vmdl` does not resolve **[CONFIRM LIVE]**, fall back to `get_bounds` on the bare object and downgrade to checking `data` shape only (SKIP the geometry assertion). |
| 5 | **Remove component** | `remove_component` `{ id: tempId, component: "ModelRenderer" }` | `res.success` and `data.removed >= 1`. (Round-trips against Check 2/4 — proves add and remove are symmetric.) |
| 6 | **Screenshot (aimed capture)** | `screenshot_from` `{ id: tempId }`, then locate the newest PNG in the screenshots dir | `screenshot_from.success`; and a new `.png` (mtime newer than a timestamp taken just before the call) appears in the screenshots folder within ~4s. If the folder can't be located (no `SBOX_LOG_PATH`/`SBOX_SCREENSHOTS_DIR` and auto-detect fails), mark **SKIP** with a note, not FAIL. Reuses the `locateScreenshotsDir()` + `newestPng()` helpers already in `diagnostics.ts`. |
| 7 | **Recompile a tiny temp asset** | `write_file` a minimal `.vmat` (KV1) to `__selftest_<runId>.vmat`, then `recompile_asset` on it | `write_file.success` (and no `data.error` — see the WriteFile masking note in `project.ts:117`), `recompile_asset.success`. This is the slow check (asset compile). The temp `.vmat` is deleted in cleanup. **[CONFIRM LIVE]** that `recompile_asset` on a trivial KV1 `.vmat` returns success on the target SDK. |
| 8 | **Cleanup verification** | (cleanup runs, then) `get_scene_hierarchy` filtered to the temp name OR `delete_gameobject` results | Pass = every created GameObject is gone and every temp file is deleted (see Cleanup guarantee). Reported as the final check so the user sees "left nothing behind: yes." |

**Round-trip detail (Check 3) — and its one open API question.**

The task asks specifically for "set + read back a property (round-trip)." The robust, low-uncertainty way to do that through the exact `set_property`/`get_property` code path:

- `set_property` and `get_property` (`MyEditorMenu.cs:1536` / `:1379`) resolve the property via `Game.TypeLibrary.GetType(component.Name).Properties`, and `SetPropertyHandler` only type-converts `Single/Double/Int32/Boolean/String` (everything else falls through to a raw string, which throws for complex types like `Color`). So the round-trip value **must be a scalar/bool/string property** that TypeLibrary exposes on the chosen component.
- **Primary plan:** set a boolean that is reliably present and settable. Candidate: `ModelRenderer.Enabled` (or the component's own bool such as `ModelRenderer.Tint`'s alpha is a Color — avoid). Write `"false"`, read back, expect `"False"`/`false`; then restore. **[CONFIRM LIVE]** that `Enabled` (a base-`Component` property) appears in `TypeLibrary.GetType("ModelRenderer").Properties` — if base properties are not enumerated there, pick a `ModelRenderer`-declared scalar/bool confirmed via `describe_type "ModelRenderer"` at implementation time.
- **Fallback plan (zero API uncertainty), if no safe component scalar is found:** round-trip the **GameObject name** instead — `rename_gameobject { id: tempId, name: "__selftest_<runId>_rt" }` then read it back via `get_scene_hierarchy { rootId: tempId, maxDepth: 0 }` (or re-create-and-inspect). This is still a genuine write→read-back of a value through the bridge; it just isn't the component-property path. The implementer picks primary if the live check passes, fallback otherwise. The report labels which path ran.

This is the only check whose exact field choice can't be finalized without the live editor; everything else uses already-verified handler contracts.

### Output format

Return a single text block: a human-scannable header line + a per-check table + the raw JSON for machine/Claude parsing. Example:

```
s&box Bridge self-test — 7/8 PASS, 1 SKIP   (runId: l4k2p9, 11.3s)

[PASS] 0 connectivity        project "bigfoot" reachable
[PASS] 1 create_gameobject   __selftest_l4k2p9  (guid 7f3a…)
[PASS] 2 add_component        ModelRenderer added
[PASS] 3 property_roundtrip   set Enabled=false, read back false  (path: component-property)
[PASS] 4 assign_model+bounds  box.vmdl → size 16×16×16
[PASS] 5 remove_component     removed 1 ModelRenderer
[SKIP] 6 screenshot           screenshots dir not located (set SBOX_SCREENSHOTS_DIR)
[PASS] 7 recompile_asset      __selftest_l4k2p9.vmat → compiled
[PASS] 8 cleanup              0 objects / 0 files left behind

Overall: HEALTHY (no failures). Skips are environmental, not bugs.

{ "runId": "...", "passed": 7, "failed": 0, "skipped": 1, "durationMs": 11342, "checks": [ ... ] }
```

Rules:

- **Top line is the verdict** — `N/M PASS` plus `HEALTHY` (no FAIL), `DEGRADED` (only SKIPs), or `BROKEN` (≥1 FAIL). A user should be able to read just the first line.
- Each FAIL line includes the real `res.error` and a one-line hint ("→ see TROUBLESHOOTING §13" for `create_material`-class failures, etc.).
- The trailing JSON is stable-shaped so Claude or a future CI harness can assert on it.
- A `verbose:false` default keeps it to the table; `verbose:true` appends each command's raw `data`.

### Cleanup guarantee (even on partial failure)

This is the load-bearing requirement — a "self-cleaning" test that orphans objects is worse than no test.

1. **Track-as-you-go.** Maintain an in-memory `createdGoIds: string[]` and `createdFiles: string[]`. Every successful create pushes its id/path immediately, *before* the next check runs. So even if Check 4 throws, Checks 1–3's artifacts are already tracked.
2. **`try { battery } finally { cleanup() }`.** The entire battery body runs inside a `try`; cleanup runs in `finally`, so it executes on success, on a thrown exception, and on an assertion failure alike. (TS `finally` guarantees this; no reliance on each check succeeding.)
3. **Cleanup is best-effort and idempotent.** For each `createdGoId`: `bridge.send("delete_gameobject", { id })`, ignoring errors (already-deleted is fine — `DeleteGameObjectHandler` returns an error string for a missing GUID, which we swallow). For each `createdFile`: `bridge.send("delete_script", { path })` (the generic file-delete command; **[CONFIRM LIVE]** that `delete_script` removes a non-`.cs` file like a `.vmat`, otherwise add the path to a "manual cleanup" note in the report — do not leave it silently). Then `trigger_hotload` once to drop the temp asset reference, mirroring how `execute_csharp` cleans up (`diagnostics.ts:361`).
4. **Belt-and-suspenders sweep.** After the per-id deletes, run `get_scene_hierarchy` and check for any remaining GameObject whose name starts with `__selftest_`. Any survivor is reported in Check 8 as a cleanup FAIL **with its GUID** so the user can delete it — the test is honest about its own mess rather than claiming success.
5. **Naming guards collisions.** The `__selftest_<runId>` prefix (runId from `Date.now()`) means two runs never clash and the sweep can't delete unrelated user objects (it only matches the prefix).
6. **No play-mode mutation risk.** Because pre-flight aborts if `gameFlag` is true, cleanup never runs during play (where deletes could desync the serializer).

Edge case — **connection dies mid-battery:** subsequent `bridge.send` calls return `{ success:false }` quickly (the client re-attempts connect and fails fast — `bridge-client.ts:215`), so the battery finishes fast with FAILs, then `finally` attempts cleanup (which also fails fast). The report states "bridge went unresponsive mid-run; created objects [guids] may remain — re-run after reconnecting to auto-sweep them." The next run's sweep clears them (prefix match). Acceptable and honest.

---

## Fix punch-list (prioritized)

Mined from `CLAUDE.md` "Known Issues / TODO" and `TROUBLESHOOTING.md`. Ordered by trust impact ÷ risk.

### P1 — `create_material` "dictionary-key" bug = TS↔C# param-name mismatch

- **Symptom (documented):** `create_material` errors with a dictionary-key error; current workaround is to write the `.vmat` via `write_file` (`TROUBLESHOOTING.md §13`, `CLAUDE.md`).
- **Root cause (found in code):** the TS tool and the C# handler disagree on the parameter contract.
  - TS `create_material` (`materials.ts:37`) sends **`path`**, `shader`, **`properties`** (a `z.record`).
  - C# `CreateMaterialHandler` (`MyEditorMenu.cs:1873`) reads **`p.GetProperty("name")`** and **`p.TryGetProperty("directory", …)`**, and ignores `properties` entirely.
  - `JsonElement.GetProperty("name")` on a payload that has no `name` key throws `KeyNotFoundException: The given key was not present in the dictionary.` — **that is the "dictionary-key" error.** It's caught by the dispatch's per-handler `try/catch` (`MyEditorMenu.cs:653`) and surfaced as `Handler error: The given key was not present in the dictionary.`
- **Proposed fix (align the contract, server-side-first):** make the TS wrapper and the handler agree. Lowest-risk option that needs **no addon republish**: change `materials.ts` to send what the handler already reads — derive `name`+`directory` from `path` (split on the last `/`) and keep `shader`. That makes `create_material` work against the *currently shipped* addon immediately. Then, as a follow-up addon change, have `CreateMaterialHandler` (a) read `path` directly (preferred, matches the rest of the API which is path-based), (b) use `TryGetProperty` with a clear error instead of `GetProperty` so a future mismatch returns a friendly message, and (c) honor `properties` for at least `Color`/`Roughness`/`Metalness` (or explicitly document that it doesn't and they should use `set_material_property`). Update `TROUBLESHOOTING.md §13` to drop the workaround once shipped.
- **Risk:** Low for the TS-only alignment (pure mapping, no behavior change to other tools). Medium if we also rewrite the handler — must keep the KV1 output valid (the handler already emits KV1, good) and confirm the generated `.vmat` compiles via `recompile_asset`. The self-test's Check 7 covers exactly this path, so this fix and the self-test validate each other.

### P2 — `is_playing.sessionPlaying` reads stale (false "playing" in edit mode)

- **Symptom (documented):** `sessionPlaying` reports `true` in edit mode after a restart; guidance is "trust `gameFlag`" (`CLAUDE.md`, `TROUBLESHOOTING.md §5` side note).
- **Root cause (found in code):** `IsPlayingHandler` (`MyEditorMenu.cs:1743`) computes `sessionPlaying = Game.ActiveScene != session.Scene` and folds it into `isPlaying = tracked || gameFlag || sessionPlaying`. After a restart, `Game.ActiveScene` and `SceneEditorSession.Active.Scene` can legitimately differ in edit mode, so `sessionPlaying` is `true` and poisons the top-level `isPlaying`.
- **Proposed fix:** stop letting the unreliable signal drive the authoritative answer. Make `isPlaying = gameFlag || tracked` (drop `sessionPlaying` from the OR), and keep `sessionPlaying` in the payload **only as a diagnostic field** (clearly the least-trustworthy of the three). Optionally rename it in docs to make its advisory status obvious. `gameFlag` (`Game.IsPlaying`) is the engine's own truth; `tracked` (`PlayState.IsPlaying`, set by start/stop handlers) covers the brief window before `Game.IsPlaying` flips. This makes the top-level field trustworthy without removing the diagnostic.
- **Risk:** Low. Narrows when `isPlaying` is `true`; the play-mode *guard* in dispatch keys off `Game.IsPlaying` directly (`MyEditorMenu.cs:581`), not this handler, so the safety guard is unaffected. Requires an addon republish (C# change). Update the three doc mentions.

### P3 — Dock "Handlers: N" readout / "handlers 0" cosmetic + the stale "dock must be open" docs

- **Symptom (documented):** the dock "Handlers: N" readout and a "handlers 0" cosmetic; separately, `CLAUDE.md`/`TROUBLESHOOTING.md §2` still say the dock **must be visible** for the frame loop to run.
- **Root cause (found in code):**
  - The dock label is built **once** in the `BridgePoller` constructor: `new Label($"Handlers: {ClaudeBridge.HandlerCount} | IPC Active")` (`MyEditorMenu.cs:3572`). Handlers register on the **first editor frame** (`OnEditorFrame` → `RegisterHandlers`, `MyEditorMenu.cs:507`+`:516`), not in the static ctor (which is deliberately empty, PR #6). If the dock is constructed before that first frame, `HandlerCount` is `0` → permanent "Handlers: 0", and it never refreshes even after registration completes.
  - The "dock must be open" claim is **stale**: the frame handler is now a `static [EditorEvent.Frame] OnEditorFrame()` on `ClaudeBridge` itself (`MyEditorMenu.cs:507`), explicitly moved off the `BridgePoller` widget so the queue drains "whether or not the user opens the dock panel" (its own comment, `:504`; this was GitHub issue #2's fix). The heartbeat is also driven from that static frame loop (`:530`). So the dock being open is **not** required for processing or heartbeat on current code — but the docs (and the `README`/`INSTALL` "keep the dock open" rule) still insist it is.
- **Proposed fix:**
  - **Dock label:** make it live. Either give `BridgePoller` a small repaint tick that re-reads `ClaudeBridge.HandlerCount` (and shows running/heartbeat state), or at minimum rebuild the label text on the first paint after `_initialized` is true. Show `Handlers: N | v1.6.0 | IPC live` so a "0" can't linger past startup. The authoritative count already exists (`ClaudeBridge.HandlerCount`, `MyEditorMenu.cs:366`).
  - **Docs:** this needs a decision (see Open Questions). **[CONFIRM LIVE]** whether `[EditorEvent.Frame]` on a static method genuinely ticks while the dock is closed/minimized on the current SDK. If yes, correct `CLAUDE.md` (lines ~127, ~511 region) and `TROUBLESHOOTING.md §2` + `README` to say the dock is a *status display, not required for operation* (keeping the "keep the editor window non-minimized" caveat, since OS frame-throttling on minimize is a separate real effect). If the live check shows frames actually stop when the dock is closed, then instead make that dependency explicit and keep the dock-required wording — but the code comments currently claim independence, so the docs are at best internally contradictory today.
- **Risk:** Low for the label (cosmetic, isolated to `BridgePoller`). Docs change is zero-code but must be backed by the live check so we don't swap one wrong claim for another.

### P4 — Friendlier / structured error messages from handlers

- **Symptom (documented):** "friendlier/structured error messages from handlers" is called out as a rough edge.
- **Root cause (found in code):** error reporting is *correct* post-audit (handler `{ error }` now yields `success=false`, `MyEditorMenu.cs:649`+`665`), but messages are inconsistent and sometimes leak raw exception text. Examples: `create_material` surfaces `Handler error: The given key was not present in the dictionary.` (P1); several handlers return bare `"No active scene"` / `"Invalid GUID"` with no hint of *which* tool or *what to do*; `GetProperty`/`SetProperty` return `"Property not found: X"` without listing what *is* available (compare `play_animation`, which returns the valid list on a miss — `CHANGELOG [1.6.0]`).
- **Proposed fix (small, targeted — not a framework):**
  - In the dispatch's catch (`MyEditorMenu.cs:653`), prefix handler-thrown errors with the command name: `"<command> failed: <message>"` so every error self-identifies. One-line change, covers all 149 handlers at once.
  - For the highest-traffic "not found" errors (`Property not found`, `Component not found`), append the available options (the data is already in hand — `typeDesc.Properties` / `go.Components.GetAll()`), matching the `play_animation` precedent. Limit to those two for YAGNI.
  - Leave the rest as-is. Do **not** build a structured error-code enum now (gold-plating).
- **Risk:** Low. The dispatch-prefix change touches one method and only changes error *text* (no success-path behavior). The "list valid options" additions are local to two handlers. Requires addon republish.

### P5 — `set_material_property` requires `MaterialOverride` first (confusing precondition)

- **Symptom (documented):** `set_material_property` requires `MaterialOverride` to be set first (`CLAUDE.md` Known Issues).
- **Root cause (found in code):** `SetMaterialPropertyHandler` (`MyEditorMenu.cs:3094`) returns `"No MaterialOverride set — assign a material first via assign_material"` when `renderer.MaterialOverride == null` (`:3122`). That's a real engine constraint (you can't set a property on a material that isn't there), and the message already names the fix — so this is the *least* broken item. The "rough edge" is purely that it's a surprising two-step.
- **Proposed fix (minimal):** keep the guard (auto-creating a material override silently would be surprising in the other direction and could clobber a model's default material). Improvement = make the message even more actionable ("set_material_property needs a material on the renderer first: call assign_material with a .vmat, or create_material then assign_material"), and document the two-step in the README materials row. Optionally add an `autoCreate:true` opt-in later that creates a blank override — but **defer** (YAGNI; not requested, and has surprising side-effects).
- **Risk:** Very low (message-only). Mostly a documentation clarification.

### Punch-list priority summary

1. **P1 `create_material` param mismatch** — highest: it's an outright-broken advertised tool, the fix is mostly server-side (ships without addon skew), and the self-test Check 7 guards it.
2. **P2 `is_playing` stale field** — corrects a wrong answer Claude/users act on; low risk; small C# change.
3. **P3 dock readout + stale "dock must be open" docs** — cosmetic code + a doc correction that removes a contradiction users trip over; pair with a live check.
4. **P4 error-message quality** — one-line dispatch win + two targeted "list valid options" handlers.
5. **P5 `set_material_property` precondition** — message/docs clarification only.

---

## `get_bridge_status` enhancement

Currently the TS `get_bridge_status` (`status.ts:14`) reports connection info (`connected`, `ipcDir`, `heartbeatAgeMs`, `roundTripOk`, `latencyMs`, `host`/`port`) and *attempts* `editorVersion` — but it reads `editorVersion` from a `get_project_info` round-trip, and `GetProjectInfoHandler` (`MyEditorMenu.cs:797`) **never returns `editorVersion`**, so that field is always `null`. Meanwhile the addon already has a richer built-in: the C# `get_bridge_status` command (handled inline at `MyEditorMenu.cs:564`) returns `{ connected, running, handlerCount, registeredCommands, version }` — and the TS tool never calls it.

**Proposed enhancement (low risk, mostly wiring up data that already exists):** in `status.ts`, when connected, send `get_bridge_status` over the bridge (in addition to / instead of the `get_project_info` round-trip) and surface:

- **`bridgeVersion`** — from the C# `version` field (`BridgeVersion = "1.6.0"`, `MyEditorMenu.cs:35`). Today's `editorVersion` is dead; replace it with this real value. Makes addon-vs-MCP-server skew visible at a glance (the exact failure in `TROUBLESHOOTING §11`).
- **`handlerCount`** — from the C# field; compare against the MCP server's known expected count and flag if it's well below (the "addon didn't fully compile / some handlers failed to register" signal, `TROUBLESHOOTING §11`).
- **`mcpServerVersion`** — from `getVersion()` (package.json), so the two halves' versions sit side by side. Add a `versionsAligned: boolean` (major.minor match) computed in TS.
- **`health` summary string** — a one-line verdict derived from what's already gathered: `"healthy"` (connected + round-trip ok + versions aligned + handlerCount in range), `"degraded: version skew"`, `"degraded: low handler count"`, `"stalled: heartbeat live, requests not draining"`, or `"disconnected"`. This is the "quick health summary" the direction asks for, and it's pure derivation from existing signals — no new bridge calls beyond the one `get_bridge_status` round-trip.

Keep `host`/`port` flagged cosmetic (already are). Optionally add a one-liner pointer: when `health` is not `"healthy"`, append "→ run `run_self_test` for an end-to-end check" so the two trust tools reference each other.

This is server-side-only (uses the already-shipped C# command), so it works against current addons and needs no republish. **[CONFIRM LIVE]** the exact field names returned by the C# `get_bridge_status` envelope (`data.version`, `data.handlerCount`, `data.registeredCommands`) — they're visible in source (`MyEditorMenu.cs:564-576`) but confirm the JSON shape over the wire.

---

## Risks & unknowns

- **Live-API confirmations (cannot run the bridge here):** all items tagged **[CONFIRM LIVE]** — chiefly (a) which component scalar/bool is safely round-trippable via `set_property`/`get_property` for self-test Check 3; (b) that `models/dev/box.vmdl` resolves for Check 4; (c) that `recompile_asset` succeeds on a trivial KV1 `.vmat` for Check 7; (d) that `delete_script` removes a non-`.cs` file for cleanup; (e) whether `[EditorEvent.Frame]` truly ticks with the dock closed (P3 docs). Each has a defined fallback in this spec, so none blocks the design.
- **Version/count drift across files** (not in scope to fix here, but noted): `CLAUDE.md` header says v1.5.2 while `CHANGELOG` top is 1.6.0 (157/149); `index.ts --help` still says "150 total / 142" and "v1.5.0"; `README` says v1.5.2 / "150+". The new tools (`run_self_test`) and any doc edits should land with a single reconciliation pass so we don't add a 158th tool against stale counts. (This is the same class of issue the v1.5.0 audit already reconciled once.)
- **Screenshot timing flake:** s&box names screenshots at 1-second granularity (`diagnostics.ts:457` comment) and `take_screenshot`/`screenshot_from` ignore the `path` arg (`TROUBLESHOOTING §10`). Check 6 must take a "before" mtime immediately before the call and poll for a newer PNG (the `screenshot_orbit` code already does exactly this — reuse it), and must SKIP-not-FAIL if the folder can't be located, to avoid a flaky check.
- **Self-test runs against the user's real open scene.** It creates objects in the active editor scene, then deletes them. Mitigations: unique `__selftest_<runId>` prefix, `finally` cleanup, post-run sweep, play-mode refusal. It does **not** save the scene, so even a missed cleanup leaves no on-disk change unless the user saves. Worth stating in the tool description ("creates and deletes a few temp objects in your current scene; does not save").
- **`delete_script` semantics for assets:** if it refuses non-`.cs` paths, the temp `.vmat` (and its `.vmat_c`) could be orphaned. Fallback already specified: report the leftover path explicitly rather than claim clean. Consider writing the temp `.vmat` under a clearly-temp subdir to make manual cleanup trivial.

## Phasing

- **Phase 1 (highest trust-per-effort, server-side-only — no addon republish, ship first):**
  1. `run_self_test` (new `selftest.ts`, orchestrated via `bridge.send`). Implement with the fallback choices for any unconfirmed step, then tighten after the live confirmations.
  2. **P1** `create_material` — the TS-side param alignment (immediate fix against the shipped addon).
  3. `get_bridge_status` enhancement (wire up the existing C# `get_bridge_status` envelope: `bridgeVersion`, `handlerCount`, version alignment, `health` line).
  These three all land in the MCP server, can ship as one npm release, and immediately make the bridge *feel* more trustworthy. The self-test then becomes the pre-release regression gate.
- **Phase 2 (addon changes — one republish, kept in lockstep with an npm bump per `TROUBLESHOOTING §11`):**
  4. **P2** `is_playing` (`isPlaying = gameFlag || tracked`).
  5. **P4** error-message quality (dispatch command-name prefix + two "list valid options" handlers).
  6. **P1 follow-up** `CreateMaterialHandler` rewrite (read `path`, `TryGetProperty` with friendly error, optional `properties`).
  7. **P3** dock label made live.
- **Phase 3 (docs/cosmetic):**
  8. **P3** correct the "dock must be open" wording (after the live frame-tick confirmation) across `CLAUDE.md`/`TROUBLESHOOTING`/`README`.
  9. **P5** `set_material_property` message + README materials note.
  10. Reconcile tool counts / version strings across `CLAUDE.md`, `README`, `index.ts --help`, `CHANGELOG`.

## Open questions for the user

1. **Self-test default verbosity / safety:** OK for `run_self_test` to create+delete a handful of temp objects in the **currently open scene** (never saving it)? Or should it require an empty/throwaway scene, or auto-create+discard a temp scene? (Temp-scene isolation is safer but heavier and adds `create_scene`/`load_scene` to the battery.)
2. **P3 dock dependency — source of truth:** do you want the docs corrected to "dock optional" (if the live check confirms the static frame loop ticks with the dock closed), or do you prefer to *keep* requiring the dock open as a conservative rule even though the code no longer depends on it? This decides whether P3 is a doc fix or a behavior statement.
3. **`create_material` contract direction (P1 follow-up):** prefer the handler move to a **`path`-based** contract (consistent with the rest of the API) — accepting a one-release window where old addon + new server must both update — or keep `name`+`directory` and only fix the TS mapping (works against the shipped addon, but leaves the API inconsistent)?
4. **Does `create_material` need to honor `properties`** (Color/Roughness/Metalness in the generated `.vmat`), or is "create blank, then `set_material_property`" the intended workflow? (Affects how much P1 touches.)
5. **Should `run_self_test` be invoked automatically by the `sbox-setup` wizard** on first connect (one extra ~10s call but instant confidence), or stay strictly on-demand?
