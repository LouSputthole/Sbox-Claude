# s&box + Claude Code MCP Integration

> Let non-coders build s&box games through conversation with Claude Code.

## Status: v1.20.0 -- 219 handlers / 228 tools (run `get_bridge_status` for the live tool/handler count)

**Last updated:** 2026-07-08 (v1.20.0)
**Bridge:** File-based IPC ✅ working on main thread
**Tools:** MCP `server.tool()` registrations across `sbox-mcp-server/src/tools/`
**Handlers:** C# command handlers compiled and registered — see the Status header above / `get_bridge_status` for the live count
**Why the difference:** several tools are **MCP-server-side** and need no editor handler — `read_log`, `get_compile_errors`, `execute_csharp`, `search_docs`, `get_doc_page`, `list_doc_categories`, `run_self_test`. They read the log file / fetch docs / hotload-eval directly, so they work even when the editor has crashed or stalled.

### What's new in v1.20.0

**+19 tools — "Director's Cut", the biggest wave since v1.4.0: `Sandbox.MovieMaker` support (it just reached the shipping build) plus a broad Tier-2 sweep across cinematics, networking, interaction/carry, loot/economy, and UI feedback. 219 handlers / 228 tools (was 200/209).** Six new handler families, all additive — no existing tool contract changed. Verify-gated live on Gravehold (all 15 codegen scaffolds generate → hotload → compile clean → TypeLibrary-load-confirmed; the 4 MovieMaker tools live-verified end to end).

- **MovieMaker / cutscene playback** (`list_movies`, `add_movie_player`, `play_movie`, `stop_movie`) — first bridge coverage of `Sandbox.MovieMaker`, which landed in the shipping build (verified via `search_types` 2026-07-08; absent 2026-07-02). Wire a `MoviePlayer` + `.movie` `MovieResource`, play/stop/seek; clips advance in play mode. **Movies are authored in the editor's Movie Maker dock** — the bridge wires and plays them, it doesn't author keyframes. New `MovieMakerHandlers.cs` + `moviemaker.ts`.
- **Cinematics & dialogue** (`create_cutscene_director`, `create_dialogue_system`) — the hand-authored, zero-asset cinematic path. Cutscene director: inspector-authored camera-shot list, smoothstep blends, `Scene.Camera` takeover in `OnPreRender` with exact-restore, skip action, input lock, optional letterbox, `OnCutsceneFinished`. Dialogue: typewriter Razor HUD, `"Speaker: text"` lines, `OnLineShown` (pair with `add_lipsync`) + `OnDialogueFinished`. New `CinematicsHandlers.cs` + `cinematics.ts`.
- **Networking primitives** (`create_host_rpc_action`, `add_targeted_rpc`, `create_local_player_resolver`, `add_host_migration_recovery`) — a validated + rate-limited `[Rpc.Host]` action (caller re-resolved, per-`SteamId` cooldown; folds `add_rate_limited_rpc`), `Rpc.FilterInclude` unicast, proxy-safe local-player resolution (online + offline), and a proxy→authority host-migration recovery hook. New `NetPrimitivesHandlers.cs` + `netprimitives.ts`.
- **Interaction + carry** (`add_interaction_prompt`, `create_hold_to_confirm`, `create_carry_system`) — an eye-traced "Press E" HUD for `IPressable` targets, a hold-to-fill confirm action (`OnConfirmed`), and first-person pickup/carry/throw with host-routed ownership (`Network.AssignOwnership`) + `MotionEnabled=false` while held. New `InteractionPackHandlers.cs` + `interactionpack.ts`.
- **Loot / economy** (`create_gacha_drop_table`, `create_currency_pickup`, `create_offline_progress`) — a host-auth gacha roller (rarity weights + pity + dup detection), a networked coin (optional magnet, one-line `Grant` seam into `EconomyWallet.AddMoney`), and offline/idle progress (`LastSeenUtc` delta, clamp, deterministic `SimulateOffline` replay). New `LootEconomyHandlers.cs` + `looteconomy.ts`.
- **UI / feedback** (`create_worldpanel_ui`, `create_proxy_nametag`, `create_combo_meter`) — a diegetic clickable `WorldPanel` UI (`WorldInput` prerequisite documented), a `TextRenderer` owner nametag shown only on proxies with distance fade, and a combo meter (static `Bump()`, decay, multiplier tiers) with a pulsing Razor HUD. New `UiFeedbackHandlers.cs` + `uifeedback.ts`.
- **The verify-gate caught a real SDK bug:** generated code called `.IsValid` on `Sandbox.Connection`, which has no `IsValid` member on this SDK (3 sites). Fixed to null checks, regenerated, all 15 scaffolds then compiled clean. See the API-differences list below.
- **Engine-watch:** MovieMaker is now SHIPPED + covered; the **loopback multi-instance socket is still absent** from the shipping build (queued for v1.21.0), as are record-to-clip (`MovieRecorder`) and offline lipsync generation. See `docs/TOOL_BACKLOG.md`.

### What's new in v1.19.0

**+3 tools — the Game Feel pack: camera shake, flickering lights, floating damage numbers. 200 handlers / 209 tools (was 197/206).** Additive — no existing tool contract changed. All three generate LOCAL/visual-only components (no `[Sync]`); wrap the calls in an `[Rpc.Broadcast]` for multiplayer juice.

- **`create_camera_shake`** — trauma-based shake (magnitude = `Trauma²`, smooth Perlin offsets, per-frame decay) applied in `OnPreRender` with an un-apply guard so it neither fights a controller-driven camera nor accumulates on a static one. Static API: `CameraShake.Shake(0.4f)` from any game code. New `GameFeelHandlers.cs` + `gamefeel.ts`.
- **`add_flicker_light`** — flicker animator for an existing light (pass `lightId`): Candle / Fluorescent / Faulty / Pulse / Lightning presets, modulates `LightColor` around the captured base color, restores it exactly on disable.
- **`create_floating_combat_text`** — rising/fading/billboarded damage popups via `TextRenderer` (no Razor, no panels). Static factory: `FloatingCombatText.Spawn(pos, "-25", Color.Red)`; self-destructs after `Lifetime`. Pairs with `create_health_system`.
- **Tier-2 of the mined backlog has begun** — next up (v1.20.0): the `Sandbox.MovieMaker` cutscene family, or the loopback multiplayer-test harness if it ships first. See `docs/TOOL_BACKLOG.md`.

### What's new in v1.18.0

**+5 tools — same-week LipSync support + the four remaining Tier-1 community scaffolds. 197 handlers / 206 tools (was 192/201).** Additive — no existing tool contract changed. All five live-verified.

- **`add_lipsync`** — wires `Sandbox.LipSync` (NEW in the 2026-07-01 engine update): one call adds LipSync to a citizen/model, wires the `SkinnedModelRenderer` + a `SoundPointComponent` bound to a `.sound` path (or an existing sound component), with `morphScale`/`morphSmoothTime` tuning. Facial morphs animate at runtime while the sound plays — verify via `capture_view` in play mode.
- **`create_round_state_machine`** (top corpus demand, 5×) — the full multi-state round system: manager singleton + abstract `RoundState` lifecycle base (Begin/Tick/OnTimeUp/Finish, per-state `[Sync]` `TimeUntil`) + named state stubs that auto-attach, `CanEnter()` skip, index-wrap, static-event + `[Rpc.Broadcast]` transition announce with late-joiner reconcile.
- **`add_interaction_station`** — one-occupant `IPressable` station: `[Sync(FromHost)]` Guid occupancy, `[Rpc.Host]`-routed claims, reservation grace window, optional level gate (`ResolveUserLevel` hook), `OnStationOpened` overlay event.
- **`create_event_director`** — generalized pacing/AI director: host-only interval roll, weighted pick over `EventPrefabs`+`Weights`, active-set dedupe, `MaxActive` cap, spawned events self-destruct via a generated `*TimedEvent` companion.
- **`create_save_slots`** — multi-slot saves over `FileSystem.Data`: `saveslots.json` manifest (slot-picker metadata) + per-slot payloads, versioned with delete-on-mismatch, optional GUID scene reconciliation.
- **Tier-1 of the mined backlog is now COMPLETE** — see `docs/TOOL_BACKLOG.md` for what's next (Tier-2 themes + engine-watch: the upstream loopback multi-instance socket and the MovieMaker system).

### What's new in v1.17.1

**Playtest-harness polish — `capture` step + `Displacement` assert read (no new tools; still 201 tools / 192 handlers).** Additive.

- **`capture` step** — `{ "capture": "label" }` screenshots the live player-POV camera mid-loop (`VisualHelpers.FindMainCamera` → `RenderToBitmap` → PNG in TEMP); the path lands in the transcript. Diagnostic, never pass/fail.
- **`Displacement` assert read** — `(WorldPosition − StartPos).Length` from job start: the clean, facing-independent movement proof, no longer leaning on `WorldPosition changed` (which also trips on gravity-settle). Dogfooded live on Gravehold.

### What's new in v1.17.0

**+2 gameplay-verification tools — `playtest` / `playtest_status`. Run a scripted gameplay loop in PLAY MODE and assert the result IN-FRAME. 192 handlers / 201 tools (was 190/199).** Additive — no existing tool contract changed.

- **`playtest`** — a scripted step list (`move` / `look` / `lookDelta` / `action` / `jump` / `set` / `wait` / `capture` / `assert`) run async inside an `[EditorEvent.Frame]` job, with assertions evaluated **in-frame** so transient state (a jump's airborne frame) is catchable — impossible via TS round-trips. Auto-disables `UseInputControls` for `move`, zeros `WishVelocity` between steps, releases held actions, restores everything on teardown.
- **`playtest_status`** — poll the running/finished job: live progress, then the full per-step pass/fail transcript.
- **Dogfooded live on Gravehold** (facepunch `PlayerController` + the `Keeper*` stack): walk → assert moved → jump → assert `IsAirborne` the next frame → land → assert `IsOnGround`, verdict PASS, re-runs clean. The "gameplay-verification frontier" — the bridge can now verify a *playable loop*, not just a static scene. New TS module `tools/playtest.ts`; C# handler `PlaytestHandler.cs`.

### What's new in v1.16.0

**Bug-fix & polish — no new tools (still 199 tools / 190 handlers), no contract changes.**

- **Vector params accept the `"x,y,z"` string form everywhere** — `ParseVector3` was object-only, so `raycast` / `physics_overlap` / `screenshot_from` / `capture_view` (and every other caller) threw `"requires … 'Object' … target … 'String'"` on the comma-string form their schemas advertise. Fixed centrally (non-objects route through the flexible parser); verified live.
- Docs corrected: the bridge frame loop is **static** (the dock does NOT need to be open — §2); `create_material` dict-key error marked resolved (§13).
- `run_tests` dropped as infeasible — s&box test projects build inside the editor's `net10` + editor-project-chain context; an external `dotnet test` (system has only .NET 8) can't reproduce it.

### What's new in v1.15.0

**+5 debug-draw tools ported from the Claude Bridge for Unity. 190 handlers / 199 tools (was 185/194).** Additive — no existing tool contract changed.

- **`debug_draw_line` / `debug_draw_ray` / `debug_draw_box` / `debug_draw_sphere` / `debug_clear`** — world-space debug primitives on a NotSaved `ClaudeDebugDraw` holder; render via `Gizmo.Draw` in the editor viewport and `Scene.DebugOverlay` in play (the latter capturable via `capture_view`). Visualize raycast hits / overlap volumes / trigger bounds / NPC sight ranges / patrol paths. Live-verified both render paths.
- **Limitation:** edit-mode gizmos show in the live editor but NOT in `take_screenshot`/`screenshot_from` (the gizmo pass isn't in that camera render) — use `capture_view` in play mode to see them through the bridge.

### What's new in v1.14.0

**+2 engine/workflow meta-tools ported from the Claude Bridge for Unity. 185 handlers / 194 tools (was 183/192).** Additive — no existing tool contract changed.

- **`set_time_scale`** — play-mode time control (`Scene.TimeScale`): `0` pause, `0.1` slow-mo, `2`+ fast-forward. Errors outside play mode. Returns applied + previous.
- **`get_profiler_stats`** — read-only `Sandbox.Diagnostics.PerformanceStats` dump: FPS, frame/GPU ms, allocations, memory, exceptions, per-category timings averaged over `frames`.
- Partial Unity carry-over wave; `debug_draw_*` and `run_tests` (plan: `docs/plans/2026-06-17-unity-carryover-meta-tools.md`) still pending.

### What's new in v1.13.0

**+4 tools (Razor leaderboard scaffold, slot inventory, stat modifier engine, placement-mode pair), review hardening from an Opus deep sweep, and atomic IPC response writes. 183 handlers / 192 tools (was 179/188).** Additive -- no existing tool contract changed.

- **`create_leaderboard_panel`** -- scaffold a Razor `PanelComponent` leaderboard bound to `Sandbox.Services.Leaderboards` (`Get`/`Refresh(CancellationToken)` -- `CancellationToken` required, verified live): fetch cooldown, `BuildHash` override, long->int rank cast. The first scaffold that emits both a `.razor` and a `.razor.scss`; passes `razor_lint` by construction. Compile-verified live.
- **`create_inventory`** -- slot-based inventory: parallel `ItemIds`/`Counts` lists, stack-first `TryAdd` with rollback, `TryRemove`/`CountOf`/`Move`/`Clear`, static `OnChanged`. The largest SYSTEMS table in the 51-game corpus (8 games) now has a scaffold. Compile-verified live.
- **`create_stat_modifier_system`** -- Set->Add->Mult stat engine: generated `{name}Stat` enum, modifiers keyed by source for clean removal, priority resolution, `OnStatChanged`. The substrate for the entire progression-upgrades corpus (8 games). Compile-verified live.
- **`create_placement_mode`** -- two-phase ghost->commit builder: client-local ghost (`NetworkMode.Never`, tinted), `camera.ScreenPixelToRay` mouse ray (API verified -- `GetMouseRay` does not exist on this SDK), grid snap, host-side re-validation + `NetworkSpawn` commit. Compile-verified live.
- **Review hardening (Opus deep sweep):** `create_networked_player` `moveSpeed` param now actually used (was silently ignored); atomic temp+rename IPC response writes (poller can never read a half-written response); `get_all_properties` unused `includeInherited` param removed from schema; stale MathX-only comments corrected in scaffold generators.
- **The verify-gate works:** four real API/codegen bugs caught before shipping -- `Board.Refresh` needs `CancellationToken`, `GetMouseRay` does not exist (`ScreenPixelToRay` is real), inventory empty-string escaping, leaderboard rank cast. All caught by generate->hotload->compile-check, not by review.

### What's new in v1.12.0

**+6 tools across two waves, a CI parity gate, a C# syntax gate, a semantic bridge-map rebuild, and a whitelist correction. 179 handlers / 188 tools (was 173/182).** Additive -- no existing tool contract changed.

- **`create_interactable`** -- `Component.IPressable` scaffold: `Press`/`Look`/`Hover`/`Blur`/`CanPress`/`GetTooltip` + `IsProxy` guard. The interaction primitive every genre recipe depends on. Compile-verified live.
- **`create_weighted_loot_table`** -- parallel `Names`/`Weights` lists, cumulative-weight `Roll()`, optional pity via `PityAfter`. The canonical weighted-pick that 7 corpus games hand-rolled independently. Compile-verified live.
- **`sandbox_lint`** -- pre-compile whitelist scan of `Code/*.cs`: flags `Array.Clone()` (still blocked), `System.Net.*`, other known-blocked BCL members with file+line + fix suggestion. Catches whitelist errors before hotload, with a line number.
- **`create_save_system`** -- versioned POCO + `FileSystem.Data.WriteJson`/`ReadJsonOrDefault`, dirty-flag autosave, clamp-on-load `Sanitize()`, delete-on-version-mismatch, `IsProxy` guard. The #1 corpus demand (7x). Compile-verified live.
- **`razor_lint`** -- static scan of `.razor`/`.razor.scss` for Razor transpiler footguns: switch-expressions in `@code`, non-ASCII in `@code`, `PanelComponent` missing `BuildHash`, root type-selector SCSS. The "valid code, opaque crash" bug class.
- **`copy_asset_with_dependencies`** -- copies an asset + full dependency closure (`Editor.Asset.GetReferences(deep:true)`), shadow-guards both dependency paths and destination against core trees (`models/citizen`, `models/dev`, `materials/dev`, `materials/default`). Kills gotchas #4 and #5 (ERROR mesh + endless recompile from shadowing).
- **CI gate:** `scripts/audit-parity.mjs` (TS<->C# parity + 4-way version lock) + `.github/workflows/ci.yml` (runs on push/PR to main).
- **Syntax gate:** `scripts/check-csharp-syntax.py` (tree-sitter pre-sync parse of all `.cs` addon files; known FP on `CreateSaveSystemHandler` `$@`-template region -- treat advisory).
- **Whitelist correction:** `System.Math` and `System.MathF` NOW COMPILE in s&box game code on the current SDK -- the old "MathX only" rule was stale. `Array.Clone()` is still blocked. `CLAUDE.md` and `docs/BRIDGE_GOTCHAS.md` corrected; `sandbox_lint` tuned accordingly.
- **Bridge map:** full semantic rebuild without an API key (3548 nodes / 4473 edges / 257 communities, 50 human-named). Previous graph was code/AST only.

### What's new in v1.11.0

**+2 "game director" scaffolds → 173 handlers (was 171), and the cookbook fully re-mined across all 51 games.** Additive — no existing tool contract changed.

- **`create_round_phase_machine`** — scaffold a host-authoritative `[Sync(SyncFlags.FromHost)]` phase machine: a `CurrentPhase` enum cycled on a per-phase `TimeUntil` timer (host-only), per-phase duration `[Property]`s, a `Loop` toggle, a `StartPhase(Phase)` host-jump, and a static `OnPhaseChanged` event that fires uniformly on host + proxies. Round/match flow, day-night gates, match phases. Generated code compile-verified live.
- **`create_day_night_clock`** — scaffold a host-authoritative time-of-day clock: `[Sync(SyncFlags.FromHost)]` `TimeOfDay` (0–24) + `Day` advancing by `Time.Delta`, `IsDay`/`IsNight` from sunrise/sunset hours, and static `OnNewDay` / `OnDayNightChanged` events. Generated code compile-verified live.
- With v1.10.0's `create_economy_wallet`, these form a **"game director" trio** (currency + round-flow + time). The remaining ~180 mined tool ideas stay queued in `docs/TOOL_BACKLOG.md`.
- **Cookbook fully re-mined:** the v1.10.0 release corpus-refreshed 18 references; v1.11.0 finishes the job — **all 41 existing references** (engine + systems + genres) now carry a "Corpus refresh (2026)" section grounded in the 51-game findings, alongside the 8 new references + `CORPUS-INDEX.md`.

### What's new in v1.10.0

**+5 tools → 171 handlers (was 166), 8 authoring-tool fixes, 2 newly auto-detected editor gotchas, a known-issues doc, and a big cookbook expansion (51 games).** Additive — no existing tool contract changed. Functionally verified live via raw IPC.

- **`invoke_method`** — call a component method by name **with arguments** (reflection + coercion, matched by name + arg-count); the args-capable companion to `invoke_button`.
- **`ensure_input_action`** — add a custom input action to the project `.sbproj` (`Metadata.InputSettings.Actions[]`) so `Input.Pressed("X")` resolves (restart to take effect in play mode).
- **`drive_player` / `drive_player_status`** (EXPERIMENTAL) — drive the active `PlayerController` directly across play-mode frames (sets `EyeAngles` + analog wish state by reflection + holds a named action down so `Input.Pressed` catches an edge). A *partial* answer to "the bridge can't synthesize gameplay input" — still no substitute for a human playtest (see `docs/BRIDGE_GOTCHAS.md` #1).
- **`create_economy_wallet`** — scaffold a host-authoritative `[Sync(SyncFlags.FromHost)]` currency component (`AddMoney`/`TrySpend`/`SetMoney`/`CanAfford` + `OnMoneyChanged`). The first of the mining-surfaced scaffolds; generated code compile-verified live. The v1.11.0 queue of ~180 more mined tool ideas is in `docs/TOOL_BACKLOG.md`.
- **8 fixes:** `set_transform` flexible scale (number / `"x,y,z"` / object); `create_gameobject` `parentId`; `duplicate_gameobject` + `grid_duplicate` in edit mode; `place_along_path` deterministic yaw; `execute_csharp` stale-file sweep + multi-line bodies; `spawn_model` bad-path → `warning` (not false success); widened vector/color coercion; `get_compile_errors` cascade filter.
- **2 auto-detected gotchas:** "Default Surface not found" on `raycast`/`raycast_terrain` → a clear `{recoverable, recovery:"restart_editor"}` hint; `install_asset`/`trigger_hotload` warn on a new `PackageReference`. New **`docs/BRIDGE_GOTCHAS.md`** documents the engine limits that aren't code-fixable.
- **Cookbook expansion:** the `sbox-cookbook` brain was re-mined across **51** open-source games (was 47) — **+6 genres** (social-deduction, survivor-roguelite, coop-kitchen, board-game, casino-gambling, physics-sports) + **2 systems** (ai-director, services-backend), high-traffic references enriched with a "Corpus refresh" pass, and a new **`references/CORPUS-INDEX.md`** cross-reference so recipes compose across games. Per-game mining lives in the local `sbox-lessons/mining-v2/`.

### What's new in v1.9.0

**+6 inspection & validation tools → 166 handlers (was 160).** Verified live against the SDK. New TS module `tools/inspection.ts`; C# handlers are **"Batch 37"** in the addon. Additive — no existing tool contract changed.

- **`inspect_networked_object`** — dump a single object's `Network.*` state plus **every component's `[Sync]` fields** (flags + live values), so you can see exactly what replicates.
- **`networking_lint`** — static scan for multiplayer footguns: unguarded `[Sync]` mutators, money/health/score as plain `[Sync]`, `List`/`Dictionary` as `[Sync]`, and `[Rpc.Host]` methods that never re-check `Rpc.Caller`.
- **`scene_validate`** — flags scene-setup footguns: no camera, stray root `Rigidbody`s, `IsTrigger`-vs-trace mismatches.
- **`save_inspect`** — list / read / diff the project's `FileSystem.Data` save files.
- **`services_query`** — read `Sandbox.Services` stats + leaderboards.
- **`simulate_input`** — drive named input actions in play mode.

**New skill — `sbox-cookbook`.** A master **router** skill indexing code-grounded recipes mined from **27 current (2026) open-source s&box games** plus the modern engine repos. Its `references/` hold **11 engine** + **15 systems** + **14 genre** recipes; it routes "how do I build a tycoon / an inventory / a save system?" to a grounded how-to. Full bundled skill set: `sbox-api`, `sbox-build-feature`, `sbox-setup`, `sbox-scaffold-game`, `sbox-cookbook`.

**License — relicensed to a source-available, no-redistribution license** (s&box Claude Bridge Source-Available License 1.0; LICENSE + all `license` fields). Earlier releases were GPL-3.0 then AGPL-3.0-or-later and remain available under AGPL; this and later versions are source-available only. Use + local modification to build your own games is allowed; redistribution/forking/repackaging/re-hosting is not. "s&box Claude Bridge" / "sboxskins.gg" name and branding are trademarks, not licensed for reuse (see `NOTICE`).

### What's new in v1.5.0

**+16 tools** — self-diagnosis, aimed screenshots, navmesh + spatial queries, real `.vpcf` particles, console/C# execution, and live docs search — plus a **security & correctness hardening pass** from an external code audit. Grouped:

- **Diagnostics (MCP-server-side):** `read_log` (tail/filter `sbox-dev.log`, auto-located via Steam `libraryfolders.vdf` or `SBOX_LOG_PATH`), `get_compile_errors` (surface the latest C# compile failures from the log — Claude can finally see its own errors instead of guessing).
- **Camera:** `screenshot_from` (**aim a screenshot at any object/point** — see the gotcha below), `frame_camera` (move the editor viewport to focus an object/point).
- **Navigation:** `bake_navmesh` (enable + bake `NavMesh.BakeNavMesh`, async), `get_navmesh_path` (query a walkable route via `GetSimplePath`; returns the path or `reachable:false`).
- **Spatial:** `physics_overlap` (sphere/box volume query — the volume counterpart to `raycast`).
- **Reflections:** `bake_reflections` (`EnvmapProbe.BakeAll` — a placed probe captures nothing until baked).
- **Particles:** `spawn_vpcf` (play a compiled `.vpcf` via `LegacyParticleSystem` — the supported particle path; the Batch 18 runtime `ParticleEffect` tools do not render through the bridge).
- **Console/Exec:** `console_run` (`ConsoleSystem.Run`), `execute_csharp` *(experimental)* (compile + run a C# snippet in the unsandboxed editor context via a temp `[ConCmd]` → hotload → run → read result from the log → clean up).
- **Object utilities:** `remove_component` (counterpart to `add_component_with_properties`), `get_tags` (counterpart to `set_tags`).
- **Docs search (MCP-server-side):** `search_docs`, `get_doc_page`, `list_doc_categories` (search the official `Facepunch/sbox-docs` guides — git-tree cached + raw Markdown).

**Security & correctness hardening (external audit):**
- **Handler errors now report `success=false`.** The dispatch hardcoded `success=true`, so a handler returning `{ error }` (e.g. "Path traversal denied") surfaced as a *successful* call — `write_file` even printed "File written successfully". Fixed; `write_file` no longer claims false success.
- **Path-traversal hardening** — all file/asset handlers resolve user paths through one separator-safe `TryResolveProjectPath` helper (canonicalize + containment), 25 call sites. Previously `list_project_files`/`create_script`/`create_scene` had no containment check.
- **Generated C# identifiers are sanitized** (`SanitizeIdentifier`) — a `name` with spaces/punctuation/keywords no longer emits an uncompilable `class` declaration.
- **Atomic IPC** — the MCP server writes request files to a temp path then atomically renames, so the editor can't consume a half-written large payload.
- **Honest networking schemas** — `add_sync_property` / `add_rpc_method` params + descriptions now reflect what the addon actually does (annotate an existing property with `[Sync]`; generate an empty RPC stub).

New TS modules this release: `tools/diagnostics.ts`, `tools/docs.ts`, `tools/navigation.ts`. Full notes in `CHANGELOG.md` [1.5.0]. **No breaking changes** to existing tool contracts.

### What's new in v1.4.0 (previous)

**+32 authoring tools across 7 batches** — the bridge goes from one-object-at-a-time to scene composition:
- **Visual & Atmosphere (Batch 17):** `add_light`, `set_fog`, `add_post_process`, `set_skybox`, `add_envmap_probe`, `apply_atmosphere`, `apply_post_fx_look`.
- **Characters & Models (Batches 19–20):** `spawn_model`, `spawn_citizen`, `dress_citizen`, `set_bodygroup`, `pose_citizen`, `equip_model`, `set_look_at`, `add_ragdoll`, `set_expression`.
- **Scene & Level (Batch 21):** `snap_to_ground`, `align_objects`, `distribute_objects`, `grid_duplicate`, `measure_distance`.
- **Environment (Batch 22):** `scatter_props`, `randomize_transforms`, `group_objects`.
- **Object Utilities (Batch 23):** `find_objects`, `set_tint`, `replace_model`, `set_tags`.
- **Experimental — VFX/Particles (Batch 18):** `spawn_particle`, `create_particle_effect`, `add_trail`, `add_beam` — compile + build the component graph, but runtime rendering is **unverified through the bridge** (use `spawn_vpcf` for visible particles). New TS modules: `tools/{visuals,characters,leveltools,objecttools}.ts`.

---

## Architecture

```
Claude Code → (stdio) → MCP Server → (file IPC) → Bridge Addon → s&box Editor
                          Node.js        %TEMP%/sbox-bridge-ipc/     C# in Editor
```

**NOT WebSocket.** s&box's sandboxed C# environment does not allow `System.Net` (HttpListener, WebSocket, TcpListener). Communication uses **file-based IPC**:

1. MCP Server writes `req_<id>.json` to the temp directory
2. Bridge addon polls for request files via `System.Threading.Timer` (50ms)
3. Requests are queued and processed on the **main editor thread** (required for scene APIs)
4. Bridge writes `res_<id>.json` back
5. MCP Server polls for response files

IPC directory: `%TEMP%/sbox-bridge-ipc/` (typically `C:\Users\<user>\AppData\Local\Temp\sbox-bridge-ipc\`)

Two components:
1. **MCP Server** (`sbox-mcp-server/`) — TypeScript/Node.js, stdio transport, talks to Claude Code
2. **Bridge Addon** — C# editor library, lives in the s&box **project's Libraries folder**

### Bridge map (knowledge graph)

A graphify knowledge graph of the bridge lives at **`docs/graph/`** — every tool maps to its C#
`IBridgeHandler` and to the docs (`IBridgeHandler` is the spine). **Consult `docs/graph/graph.json`
or `docs/graph/graph.html` to see what connects to what before adding or changing a tool.** It CAN
GO STALE (check the date in `GRAPH_REPORT.md`). **Maintainers: regenerate it as part of every
release** — `scripts/regen-graph.ps1` for the deterministic code/AST refresh, or re-run `/graphify`
for the full doc-inclusive graph. See `docs/graph/README.md`.

---

## Critical Lessons Learned

### Addon Location
- **DO NOT** put addons in the global `sbox/addons/` folder — those are built-in only and won't compile custom code
- **DO** put addons in the project's `Libraries/` folder (e.g., `bigfoot/Libraries/claudebridge/`)
- s&box auto-scaffolds `Editor/`, `Code/`, `UnitTests/` with proper `.csproj` files when you create a library through the editor

### Compilation
- s&box compiles addons silently — if there are errors, **no log output appears** unless you check the full log
- Check `logs/sbox-dev.log` or the console for `Compile of 'local.X.editor' Failed:` messages
- The `.csproj` file is required and must reference s&box DLLs with absolute paths
- s&box generates the `.csproj` automatically when you create a library through the Library Manager

### Main Thread Requirement
- All scene manipulation APIs (`CreateObject`, `AddComponent`, `Destroy`, etc.) **must run on the main editor thread**
- `System.Threading.Timer` callbacks run on thread pool threads — NOT safe for scene APIs
- Solution: Timer reads files from disk (thread-safe), queues them, and a `[EditorEvent.Frame]` handler on a `[Dock]` widget processes them on the main thread

### Class Discovery
- s&box discovers classes via attributes like `[Menu]`, `[Dock]`, `[EditorEvent.Frame]`
- Static constructors fire when the type scanner discovers the class
- `[Event("editor.created")]` fires BEFORE custom addons load — don't rely on it
- `[Event("editor.loaded")]` does NOT exist

### s&box API Key Differences (vs. what was originally coded)
- `SceneEditorSession.Active.Scene` — the editor scene (NOT `Game.ActiveScene` which is for play mode)
- `go.AddComponent<T>()` — add component (NOT `go.Components.Create<T>()`, though ComponentList.Create also exists)
- `go.GetOrAddComponent<T>()` — get existing or add
- `go.GetComponent<T>()` — get existing
- `SceneEditorSession.Active.Selection` — editor selection (NOT `EditorScene.Selection`)
- `SceneEditorSession.Active.SetPlaying(scene)` / `.StopPlaying()` — play mode
- `SceneEditorSession.Active.FrameTo(bbox)` — focus camera on object
- `SceneEditorSession.Active.Save()` — save scene
- `Game.TypeLibrary.GetType("name")` — find types
- `Game.TypeLibrary.GetTypes<Component>()` — list all component types
- `MeshCollider` does NOT exist — use `HullCollider` instead
- `Rotation.Pitch()`, `.Yaw()`, `.Roll()` are methods, not properties
- **`Sandbox.Connection` has NO `IsValid` member on this SDK** — null-check a `Connection` (`caller is null` / `?.`), do NOT call `.IsValid` on it. The v1.20.0 verify-gate caught generated code doing exactly this at 3 sites (host-rpc-action ×2, targeted-rpc, gacha); regenerated with null checks and all 15 scaffolds then compiled clean. `GameObject`/`Component` `.IsValid()` is a different, valid API — the confusion is easy, so reflect (`describe_type "Connection"`) before assuming.

### Math & Events (s&box sandbox specifics)
- **UPDATE (verified live 2026-06-09):** `System.Math` and `System.MathF` now COMPILE in game code on the current SDK — the old "MathX only" rule is stale. `MathX` remains fine/preferred for s&box helpers. `Array.Clone()` is STILL whitelist-blocked ("System.Array.Clone() is not allowed when whitelist is enabled", confirmed live) — use `.ToArray()`. `GameObject.Clone()` is unrelated and fine. `sandbox_lint` reflects this.
- `IGameEvent` / `GameObject.Dispatch()` / `Scene.Dispatch()` are from `facepunch.libevents` package, NOT base s&box
- `Networking.MaxPlayers` is **read-only** — set via lobby config, not direct assignment
- `Networking.IsHost` may throw if networking is not active — guard with try/catch or check `Networking.IsActive` first

### UTF-8 BOM (Critical IPC Bug — Fixed)
- C#'s `Encoding.UTF8` writes a BOM prefix (`EF BB BF`) at the start of files
- Node.js `JSON.parse` rejects the BOM: `Unexpected token '﻿'` — but the `catch` block in the polling loop swallowed this silently, causing every response to time out
- **Bridge fix**: Use `new UTF8Encoding(false)` for all IPC file writes (status.json, res_*.json)
- **MCP server fix**: Strip BOM with `.replace(/^\uFEFF/, "")` before `JSON.parse` as a safety net
- Both fixes are applied — belt and suspenders

### Bridge Behavior Notes
- Bridge **drains the full request queue every editor frame** (`ProcessPendingOnMainThread` while-loop) — NOT one-per-frame. A single very slow handler still blocks that frame until it finishes (see the optional per-frame time-budget TODO).
- If game code fails to compile, the editor code (bridge) also fails (`Broken Reference: package.local.X`)
- Bridge Status menu item always works even when frame processing is broken (it's a sync call)
- The bridge's `[EditorEvent.Frame]` is a **static** handler (moved off the dock widget — GitHub issue #2), so the request queue + heartbeat process **whether or not the dock is open**. (Frames may still throttle when the editor window is minimized/unfocused — OS-level; unverified.)
- `Org` in `.sbproj` must be `"local"` for local development — only set to your org name when publishing

### Visual Verification & Other v1.5.0 Gotchas
- **`take_screenshot` renders from the scene's Main Camera — ONE fixed angle.** This is the #1 reason visual changes "can't be verified": the Main Camera may not be pointed at the thing you changed. Use **`screenshot_from`** to move the Main Camera to frame a target object/point, capture, and restore it. `screenshot_from` is the tool that makes the authoring layer screenshot-verifiable. (`frame_camera` only moves the editor *viewport*, which the screenshot doesn't use.)
- **Particles:** the runtime `ParticleEffect` tools (`spawn_particle` / `create_particle_effect` / `add_trail` / `add_beam`) are **experimental and do not render through the bridge**. Use **`spawn_vpcf`** (a compiled `.vpcf` via `LegacyParticleSystem`) for visible particles. See `CHANGELOG.md` [1.5.0] Known Issues re: source `.vpcf` assets.
- **`is_playing.sessionPlaying` can read stale** (it reports `true` in edit mode after a restart). Trust the `gameFlag` field instead.
- **Self-diagnosis works even when the editor is down:** `read_log` and `get_compile_errors` read `sbox-dev.log` directly (MCP-server-side), so Claude can diagnose a crashed/stalled editor without a live round-trip.

### API Schema
- The full s&box type schema can be downloaded as JSON from `sbox.game/api`
- It contains all types, methods, properties, and fields
- Use this as the source of truth, NOT reverse engineering from the tools addon
- Key types verified from schema: `MathX.Clamp`, `SceneEditorSession`, `NetworkHelper`, `Package.FetchAsync`, `AssetSystem.InstallAsync`, `UndoSystem.Undo/Redo`

---

## Project Structure

```
sbox-claude/
├── CLAUDE.md                          ← YOU ARE HERE
├── README.md                          ← User-facing docs
├── INSTALL.md                         ← Installation guide
├── LICENSE                            ← Source-Available License 1.0 (no redistribution)
├── NOTICE                             ← license summary + name/branding (trademark) note
├── install.ps1 / install.sh           ← Legacy installers (need updating)
│
├── sbox-mcp-server/                   # MCP Server (TypeScript)
│   ├── package.json
│   ├── tsconfig.json
│   ├── src/
│   │   ├── index.ts                   # Entry point — registers all the tools
│   │   ├── transport/
│   │   │   └── bridge-client.ts       # File-based IPC client
│   │   └── tools/
│   │       ├── project.ts             # get_project_info, list_project_files, read_file, write_file
│   │       ├── scripts.ts             # create_script, edit_script, delete_script, trigger_hotload
│   │       ├── scenes.ts              # list_scenes, load_scene, save_scene, create_scene
│   │       ├── gameobjects.ts         # CRUD, hierarchy, selection
│   │       ├── components.ts          # get/set properties, list components, add/remove components
│   │       ├── assets.ts              # search, list, install, info
│   │       ├── materials.ts           # assign_model, create_material, assign_material, set_material_property
│   │       ├── audio.ts               # list_sounds, create_sound_event, assign_sound, play_sound_preview
│   │       ├── playmode.ts            # play/stop, is_playing, runtime properties, screenshot, undo/redo
│   │       ├── prefabs.ts             # create/instantiate/list/info
│   │       ├── physics.ts             # add_physics, add_collider, add_joint, raycast, physics_overlap
│   │       ├── ui.ts                  # create_razor_ui, screen/world panels
│   │       ├── templates.ts           # player/npc/game_manager/trigger templates
│   │       ├── networking.ts          # network helpers, spawn, sync, RPC, templates
│   │       ├── publishing.ts          # project config, validate, thumbnail, package details
│   │       ├── world.ts               # invoke_button, terrain/cave/forest editing, sculpt, place_along_path
│   │       ├── discovery.ts           # describe_type, search_types, get_method_signature, find_in_project
│   │       ├── visuals.ts             # lights, fog, post-fx, skybox, envmaps, spawn_vpcf, bake_reflections
│   │       ├── characters.ts          # spawn/dress/pose citizens, equip, look_at, ragdoll, expression
│   │       ├── leveltools.ts          # snap_to_ground, align/distribute, grid_duplicate, scatter, group
│   │       ├── objecttools.ts         # find_objects, set_tint, replace_model, set/get_tags, remove_component
│   │       ├── navigation.ts          # bake_navmesh, get_navmesh_path
│   │       ├── diagnostics.ts         # read_log, get_compile_errors, screenshot_from, frame_camera, console_run, execute_csharp
│   │       ├── docs.ts                # search_docs, get_doc_page, list_doc_categories (MCP-server-side)
│   │       ├── inspection.ts          # inspect_networked_object, networking_lint, scene_validate, save_inspect, services_query, simulate_input (Batch 37)
│   │       ├── playtest.ts            # playtest, playtest_status (gameplay-verification harness)
│   │       ├── gamefeel.ts            # create_camera_shake, add_flicker_light, create_floating_combat_text (v1.19.0)
│   │       ├── moviemaker.ts          # list_movies, add_movie_player, play_movie, stop_movie (v1.20.0)
│   │       ├── cinematics.ts          # create_cutscene_director, create_dialogue_system (v1.20.0)
│   │       ├── netprimitives.ts       # create_host_rpc_action, add_targeted_rpc, create_local_player_resolver, add_host_migration_recovery (v1.20.0)
│   │       ├── interactionpack.ts     # add_interaction_prompt, create_hold_to_confirm, create_carry_system (v1.20.0)
│   │       ├── looteconomy.ts         # create_gacha_drop_table, create_currency_pickup, create_offline_progress (v1.20.0)
│   │       ├── uifeedback.ts          # create_worldpanel_ui, create_proxy_nametag, create_combo_meter (v1.20.0)
│   │       └── status.ts              # get_bridge_status
│   └── dist/                          # Compiled JS
│
└── sbox-bridge-addon/                 # Legacy location (DO NOT USE)
    └── ...                            # Old WebSocket-based addon (non-functional)

# ACTUAL working addon location (per-project):
<s&box project>/Libraries/claudebridge/
├── claudebridge.sbproj               # Auto-generated by s&box
├── Editor/
│   ├── claudebridge.editor.csproj    # Auto-generated by s&box
│   └── MyEditorMenu.cs               # ALL bridge code — server + handlers
├── Code/
│   └── claudebridge.csproj           # Auto-generated
└── UnitTests/
    └── claudebridge.unittest.csproj  # Auto-generated
```

---

## How to Install (Current Working Method)

### Prerequisites
- s&box installed via Steam
- Node.js 18+ installed
- Claude Code installed

### Step 1: Create the Library in s&box
1. Open s&box with your project
2. Go to Library Manager
3. Create a new library called "claudebridge"
4. s&box will scaffold the folder structure

### Step 2: Copy the Bridge Code
Copy `MyEditorMenu.cs` into the `Editor/` folder of the library.

### Step 3: Build the MCP Server
```bash
cd sbox-mcp-server
npm install
npm run build
```

### Step 4: Register with Claude Code
```bash
claude mcp add sbox -- node /path/to/sbox-mcp-server/dist/index.js
```

### Step 5: Restart s&box
- Open the "Claude Bridge" dock from View menu
- Check status: Editor → Claude Bridge → Status

---

## Verified s&box APIs (from schema + testing)

### Scene Access
```csharp
var scene = SceneEditorSession.Active?.Scene;  // Editor scene
var scene = Game.ActiveScene;                   // Play mode scene
```

### GameObject
```csharp
var go = scene.CreateObject(true);
go.Name = "My Object";
go.WorldPosition = new Vector3(x, y, z);
go.WorldRotation = Rotation.From(pitch, yaw, roll);
go.WorldScale = new Vector3(sx, sy, sz);
go.SetParent(parent, keepWorldPosition: true);
go.Enabled = false;
go.Destroy();
var clone = go.Clone();
scene.Directory.FindByGuid(guid);
scene.Directory.FindByName("name");
```

### Components
```csharp
go.AddComponent<ModelRenderer>();
go.GetComponent<ModelRenderer>();
go.GetOrAddComponent<ModelRenderer>();
go.Components.GetAll();
go.Components.Create(typeDescription);  // Dynamic type
```

### Models & Materials
```csharp
var renderer = go.GetOrAddComponent<ModelRenderer>();
renderer.Model = Model.Load("models/dev/box.vmdl");
renderer.MaterialOverride = Material.Load("path.vmat");
renderer.Tint = Color.Red;
```

### Physics
```csharp
go.AddComponent<Rigidbody>();       // Has: Gravity, MassOverride, LinearDamping, etc.
go.AddComponent<BoxCollider>();      // Has: Scale, Center, IsTrigger
go.AddComponent<SphereCollider>();   // Has: Radius, Center, IsTrigger
go.AddComponent<CapsuleCollider>(); // Has: Radius, Start, End, IsTrigger
go.AddComponent<HullCollider>();     // (NOT MeshCollider — doesn't exist)
```

### Play Mode
```csharp
Game.IsPlaying   // bool
Game.IsPaused    // bool
SceneEditorSession.Active.SetPlaying(scene);
SceneEditorSession.Active.StopPlaying();
```

### Editor Selection
```csharp
SceneEditorSession.Active.Selection.Set(go);
SceneEditorSession.Active.Selection.Add(go);
SceneEditorSession.Active.Selection.Clear();
SceneEditorSession.Active.FrameTo(go.GetBounds());  // Focus camera
```

### TypeLibrary
```csharp
Game.TypeLibrary.GetType("ModelRenderer");          // TypeDescription
Game.TypeLibrary.GetTypes<Component>();              // All component types
// TypeDescription: .Name, .Title, .Description, .Properties, .IsAbstract, .FullName
```

### Project
```csharp
Project.Current.GetRootPath();
Project.Current.GetAssetsPath();
Project.Current.Config.Title / .Org / .Ident / .Type
```

---

## Known Issues / TODO

- [x] ~~Parameter name alignment~~ — Fixed, handlers use correct MCP param names
- [x] ~~get_scene_hierarchy empty~~ — Fixed, removed erroneous Parent==null filter
- [x] ~~Old WebSocket code~~ — Removed, ws dependency dropped
- [x] ~~`start_play` triggers but `is_playing` returns false~~ — Fixed via `EditorScene.Play` + manual `PlayState` tracking
- [x] ~~Handler `{ error }` masked as `success=true`~~ — Fixed in v1.5.0; dispatch now reports `success=false` on handler-level errors
- [x] ~~`get_compile_errors` not implementable~~ — Implemented in v1.5.0 **MCP-server-side** (reads `sbox-dev.log` directly, no editor API needed); same for `read_log`
- [ ] `add_sync_property` only annotates an existing property with `[Sync]`; `add_rpc_method` generates an empty stub (schemas now reflect this honestly as of v1.5.0)
- [ ] `set_material_property` requires MaterialOverride to be set first
- [x] ~~`create_material` dictionary-key bug~~ — Fixed; reads `path` (or legacy `name`) and writes a KV1 `.vmat`.
- [ ] **Runtime `ParticleEffect` tools** (`spawn_particle` etc.) do not render through the bridge — use `spawn_vpcf`. No flame `.vpcf` ships in a bridge-loadable form yet (under investigation).
- [ ] `is_playing.sessionPlaying` can read stale after a restart — trust `gameFlag`.
- [ ] `take_screenshot` is fixed to the Main Camera — use `screenshot_from` to aim at a target.
- [ ] Bridge addon is project-specific (lives in each project's `Libraries/`) — also published to the s&box Asset Library.
- [ ] Tools with no s&box editor API and therefore not implemented: `pause_play`, `resume_play`, `get_console_output`, `clear_console`, `build_project`, `get_build_status`, `clean_build`, `export_project`, `prepare_publish` (removed from the MCP surface in v1.3.0).
- [ ] Map-edit tools assume the project has `MapBuilder`/`CaveBuilder`/`ForestGenerator`-shaped components. `invoke_button` works on any project; the named convenience tools require those components or compatible ones.

---

## Development

```bash
# Build MCP Server
cd sbox-mcp-server && npm install && npm run build

# The Bridge Addon is compiled automatically by s&box
# Just edit MyEditorMenu.cs and restart s&box

# Test IPC manually:
echo '{"id":"test","command":"get_project_info","params":{}}' > %TEMP%/sbox-bridge-ipc/req_test.json
# Check response:
cat %TEMP%/sbox-bridge-ipc/res_test.json
```
