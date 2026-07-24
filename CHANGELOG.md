# Changelog

All notable changes to the s&box Claude Bridge. Also online: [sboxskins.gg/claudebridge/changelog](https://sboxskins.gg/claudebridge/changelog).

## [Unreleased]

### Added

- Deterministic `dryRun:true` placement plans for `place_along_path`, `grid_duplicate`, and `scatter_props`, plus `commit_placement_plan` for exact, rollback-protected creation with slot-to-GUID receipts.
- `inspect_model_geometry` for model-local render/physics bounds, footprint, height, and pivot-to-ground offsets before placement.
- Provenance-rich `get_bounds` aggregates for render, all physics, and non-trigger physics, plus play-aware `find_objects_near`.
- Session camera bookmarks, ordered multi-view captures with previous-capture RGBA metrics and occlusion diagnostics, and deterministic orthographic `capture_topdown`.
- `start_multiplayer_test`, `multiplayer_test_status`, and `stop_multiplayer_test` for a bounded local multi-instance harness: create a private, hidden host lobby in play mode, launch up to two real `sbox.exe -joinlocal` clients, inspect host-side joins, and clean up tracked processes.

### Changed

- `set_transform` now pre-parses every supplied value, supports explicit world/local space, returns before/after receipts, and rolls back if apply or receipt construction fails.
- `batch_set_property` dry runs now perform the same coercion/reference resolution as apply, distinguish changes from no-ops, and avoid needless setters.
- Scatter placement now rejects error models and invalid ranges, preserves fixed non-unit scale, and grounds model bottoms using traced end positions plus model-local pivot offsets.
- Path placement now rejects invalid models/ranges, preflights a 2,048-object safety cap before immediate creation, and uses a stable fallback up vector for vertical aligned segments.
- Camera captures now use strict pose parsing, runtime-safe GameObject resolution, disabled non-main temporary cameras, dynamic far planes, deterministic cleanup, transactional comparison baselines, and a bounded 64 MiB session cache.
- Nearby searches now require exactly one finite center source, exclude the Scene root, and report radius clamping explicitly.
- Parity auditing now discovers every concrete `IBridgeHandler` class recursively and fails when a handler is omitted from `RegisterHandlers`.
- Bridge feature guidance now treats saved scene/prefab `[Property]` values as authoritative and directs project-specific tuning through project-owned helpers rather than code initializers.

### Fixed

- Rotation arguments now semantically unwrap native-stringified JSON objects/arrays before parsing, preserving `pitch`/`yaw`/`roll` keys regardless of property order across shared, strict `set_transform`, camera-bookmark, character/equipment, capture, and `drive_player` paths.
- The proven multiplayer-test handlers from the installed library are now present in canonical source, registered, exposed through TypeScript/native MCP wrappers, and covered by discovery/status smoke checks. Starts now reject overlap, validate host-plus-client capacity, count joins from a pre-spawn connection baseline, create private/hidden lobbies, and roll back newly created lobbies when every spawn fails; failed client kills remain tracked for truthful retryable cleanup.

> Source and offline gates are verified; live editor smoke coverage remains pending and is tracked in `TESTING.md`.

## [2.2.0] -- 2026-07-24

### Fixed
- Rotation/vector arguments arriving through the native MCP gate as stringified JSON
  objects/arrays are now unwrapped before parsing (shared + strict paths). Comma strings,
  arrays, and objects all parse; `add_component_to_new_object`/`set_transform` rotation
  works end-to-end (live-verified in the s&box editor).
- `start_multiplayer_test`, `multiplayer_test_status`, and `stop_multiplayer_test` are now
  actually registered in `RegisterHandlers` (they shipped unregistered in 2.1.0) and are
  exposed through both the TypeScript and native MCP surfaces with parity coverage.

### Changed
- Multiplayer test harness hardened: private/hidden lobbies, capacity validation,
  overlapping-run prevention, false-positive join guards, failed-start rollback, and
  truthful tracking of dead clients and failed cleanup (live-verified: real second
  client joined, was tracked, and its death was reported honestly).
- Prefab `[Property]` values documented as authoritative over code defaults; tuning flows
  through project helpers (see sbox-build-feature gotchas).
- Docs corrected: legacy file-IPC retirement announced for v2.1.0 did NOT happen and is
  deferred pending a compatibility decision (CLAUDE.md / V2-MIGRATION now say so).

### Codex
- The Codex distribution prepared in 2.1.0 ships publicly with this release: `plugins/sbox-codex-bridge`
  (marketplace, native editor MCP, optional lifeline diagnostics, API brain, cookbook,
  and workflow skills). 286 tools / 278 handlers / zero orphans.

## Codex distribution [2.1.0] -- 2026-07-16

- Prepared the first Codex distribution: the GitHub-backed marketplace, s&box Codex Bridge plugin 2.1.0, native editor MCP, optional lifeline diagnostics, API brain, cookbook, and workflow skills.
- When published, this distribution should use the separate immutable tag `codex-v2.1.0`. The existing `v2.1.0` tag predates the Codex package and remains unchanged.

## [2.1.0] -- 2026-07-13 "Action!"

**+30 tools — Tier-2 completion + gameplay recording + the cinematic wave. 275 tools / 267 handlers / 262 native across 28 toolsets / 7 lifeline (was 245/237/232). Seven new families, all additive — no existing tool contract changed. All 30 live-verified on Gravehold (the Tier-2/recording 25 on 2026-07-12, the 5 cinematic tools e2e 2026-07-13): every handler executed for real, every generated scaffold hotload-compiled clean + TypeLibrary-load-confirmed, and the scene + project left pristine.**

### Added — Economy & Save (Batch 55; bridge_scaffold_gameplay)

- **`create_currency_account`** — the audited ledger the corpus keeps hand-rolling: a `[Sync(FromHost)]` balance, host-guarded `Deposit` / `Withdraw` / `TryTransfer`, and a transaction ring buffer so every mutation leaves a record.
- **`create_idle_economy`** — the geometric bulk-buy engine: closed-form `CostOf` / `MaxAffordable` / `BuyMax` (no per-purchase loops), auto-wiring a sibling wallet via TypeLibrary reflection (zero compile-time coupling, same convention as `create_idle_income`).
- **`create_signed_save`** — an FNV-1a tamper-EVIDENT save envelope: clamp-on-load `Sanitize`, forced reset on signature mismatch, and an `OnTampered` hook. Honest by design: tamper-*evident*, not tamper-proof.
- **`create_meta_progression`** — roguelite meta-currency + unlock flags persisted across runs, with a `BankRun` seam for end-of-run deposits.
- **`add_steam_stat_currency`** — currency over `Sandbox.Services.Stats`, with the cloud semantics documented honestly (batched backend flush — not a transactional store).
- **`create_loot_table_resource`** — data-asset loot tables: an `[AssetType]` GameResource (`.loot` files) + a resolver component, nested tables with a depth cap. Designers edit assets; code rolls them.

### Added — Stats & Achievements (Batch 56; bridge_scaffold_gameplay)

- **`add_leaderboard_stat`** — the write-side partner to `create_leaderboard_panel`: batched 12 s flush, baseline-delta idempotency, and chunking for large increments.
- **`create_achievement_set`** — achievement definitions + persisted progress + a static `OnAchievementUnlocked` + a Razor toast HUD.
- **`add_achievement_trigger`** — a trigger-zone GameObject firing `Progress`/`Unlock` on entry, with a once-only latch.
- **`create_speedrun_leaderboard`** — a run timer that submits only on improvement via `Stats.SetValue`, plus a `Board2` min-aggregation panel with a friends filter and a local-best row.
- **`create_elo_rating_system`** — `[Sync(FromHost)]` NetDictionary ratings, `ReportMatch` / `ReportTeamMatch`, `Math.Pow` expected score, host-side persistence.

### Added — Round-flow & UI (Batch 57)

- **`create_round_timer_hud`** (bridge_scaffold_polish) — a Razor round-timer HUD that binds by reflection to *either* shipped round machine (phase or state), with a 1 Hz adaptive `BuildHash`.
- **`scaffold_map_vote_flow`** (bridge_scaffold_gameplay) — end-of-round map voting: `[Rpc.Host]` votes with `Rpc.Caller` re-validation, `[Sync(FromHost)]` NetList tallies, an LCG tie-break, winner → `Scene.LoadFromFile`, plus a Razor vote panel.
- **`add_panel_buildhash`** (bridge_ui) — a FILE-EDIT tool: patches an *existing* `.razor` to add a `BuildHash` override — `razor_lint`'s companion fixer. The `alreadyPresent` no-op path is verified.

### Added — World & Render (Batch 58)

- **`add_water_body`** (bridge_world) — a `WaterVolume` physics volume + trigger BoxCollider footprint + an optional tinted surface child. Honest limit: swimmable water *physics*, not a water shader.
- **`add_render_target_camera`** (bridge_visuals) — a secondary camera rendering to a texture (`TextureBuilder`) + a generated display component wiring `camera.RenderTarget` onto a Material — the CCTV / mirror / portal-screen path.
- **`add_daynight_sun`** (bridge_visuals) — a DirectionalLight arc + a 5-key color gradient + optional SkyBox2D tint, driven by `create_day_night_clock`'s `TimeOfDay`; refuses generation if the clock class is unknown rather than guessing.

### Added — AI & Systems (Batch 59)

- **`create_needs_system`** (bridge_scaffold_gameplay) — decaying needs rolled into a weighted-mean `[Sync(FromHost)]` `Happiness`, with an edge-triggered `OnNeedCritical`.
- **`create_utility_ai`** (bridge_npc) — an abstract self-scoring `Action` base + a brain with hysteresis: emergent behaviour where the FSM (`create_npc_brain`) is scripted — the two pair.
- **`create_npc_schedule_brain`** (bridge_npc) — a daily schedule with midnight wrap, binding **by capability** to any float-`TimeOfDay` clock (with an honest internal fallback clock when none exists), optional NavMeshAgent movement.
- **`create_event_bus`** (bridge_scaffold_gameplay) — a typed local pub/sub static class; the must-`Unsubscribe`-in-`OnDestroy` discipline is documented in the generated code.
- **`add_tts_voice`** (bridge_audio) — `Sandbox.Speech.Synthesizer` via its fluent API: `Say()` from game code. Audio-only **by design** — `LipSync` consumes a `BaseSoundComponent`, not a `SoundHandle`; `enableVisemeData` exposes `Handle.LipSync.Visemes` for driving mouths yourself.

### Added — Gameplay Recording (Batch 60; bridge_moviemaker) — the engine-watch payoff

**`Sandbox.MovieMaker.MovieRecorder` reached the shipping build** (verified 2026-07-12 — it was absent 2026-07-08), closing engine-watch item #2. The bridge now records real gameplay to `.movie` clips:

- **`record_gameplay_clip`** — start a play-mode recording via a monitor-only frame job. `MovieRecorder.Start()` AUTO-advances and auto-captures with game time — manually pumping `Advance`/`Capture` too DOUBLE-COUNTS (verified: 5.55 s wall → 10.80 s clip). Pass `ids` for targeted `WithCaptureGameObject` tracks, or omit for whole-scene capture.
- **`stop_gameplay_recording`** — `ToClip` → `MovieResource` JSON → `Assets/<folder>/<name>.movie` → `AssetSystem.RegisterFile` + compile, so the clip is immediately loadable by `list_movies` / `play_movie`; optional MoviePlayer wiring.
- **`gameplay_recording_status`** — poll the running recording.
- **E2E-proven on Gravehold:** recorded 6.54 s of live gameplay and replayed it through `play_movie`.

### Added — The cinematic wave (Batches 61–63) — dialogue that speaks, cameras that kick, cutscenes without the dock

**All five e2e-verified live on Gravehold 2026-07-13.**

**Batch 61 (bridge_scaffold_polish):**

- **`generate_lipsync_dialogue`** — NPCs speak their lines with MOVING MOUTHS: dialogue lines + positional TTS + data-driven mouth animation. The live `SoundHandle.LipSync.Visemes` stream (the engine's 15-viseme Oculus set) is multiplied through the model's own baked viseme→morph matrix via `Model.GetVisemeMorph` (Citizen ships the data — no hand-guessed morph map), smoothed, with a loose TypeLibrary bind to a `create_dialogue_system` HUD, plus `OnLineStarted` / `OnDialogueFinished` / `Skip()`. Honest by design: the API surface and mapping data are live-verified, but the runtime viseme stream still needs one human playtest (the generated `LogVisemes()` helper confirms it in seconds).
- **`create_camera_effects`** — `CameraFx` statics (`Shake` / `ShakeAt` / `Punch` / `PunchAngles` / `Tilt` + `HitPunch` / `ExplosionShake` / `LandingTilt` presets) over the SDK's NEW built-in `CameraComponent.AddShake` / `AddPunch` / `AddTilt` (whitelist-callable from game code — probe-verified); `ShakeAt` uses `BaseEffect.Epicenter` + `Radius` distance falloff. Composes with `create_camera_shake`: the built-ins are ONE-SHOT engine effects, the trauma model is CONTINUOUS — don't fire both for the same event.

**Batch 62 (bridge_moviemaker):**

- **`author_movie_clip`** — data-driven cutscene AUTHORING with no play mode and no Movie Maker dock: a manual `Advance`/`Capture` bake (the doc-official in-editor idiom — movie-maker/recording-api) of a temp or borrowed camera through a declarative shot list (hold/blend, smoothstep/linear, FOV via an explicit `WithCaptureComponent` — live-verified decoding FOV exactly). Live: a 3-shot clip baked in 6 ms, saved as a dock-loadable `.movie`, replayed to full duration via `play_movie`; the temp camera destroyed, the borrowed camera restored byte-exact. `lookAt` GUIDs resolve to static bake-time positions — follow-cams are `record_gameplay_clip`'s job.

**Batch 63 (bridge_moviemaker):**

- **`record_playtest`** — one call = a scripted playtest + a gameplay recording of the SAME run — pure composition of the shipped playtest + recorder stacks; returns the verdict AND the saved clip (live: a 3-step PASS + a 1.36 s clip), so a failing playtest arrives with replayable footage of exactly what happened. Poll chain: `playtest_status` → `gameplay_recording_status`. NOT scene-mutating (the play-mode precedent).
- **`create_killcam`** — a REAL MovieMaker killcam scaffold (no fallback needed — sandboxed game code CAN construct and drive `MovieRecorder` + `MoviePlayer`, probe-verified): `BufferDuration` is a TRUE rolling buffer (8.72 s recorded → exactly a 3.00 s clip, re-based to 0), `StartWatching` / `TriggerReplay` / `OnReplayFinished`, chase-cam takeover with byte-exact restore, `ReplayTimeScale`. The live-target fight is documented: disable the controller before replay — dead-target is the working path.

### Docs audit (official-docs cross-check, 2026-07-13)

A cross-check of the shipped gotchas and docs against the official Facepunch docs (sbox.game/dev/doc), live re-verified via `get_method_signature`'s `hasDefault` where noted:

- **Gotcha §11's "required param" claims were overstated:** `Stats.SetValue(name, amount, context = null, data = null)` — 2-arg calls are legal (defaults, not a missing overload); `Leaderboards.GetFromStat` has BOTH `(statName)` and `(packageIdent, statName)` overloads; `Board2.Refresh(CancellationToken cancellation = default)` — bare `Refresh()` is legal. The official docs also document `Stats.LocalPlayer` (a static — reflection-invisible, the §10 blind spot); the "no LocalPlayer" claim is corrected.
- **§15:** `SoundHandle.Stop(float fadeTime = 0)` — parameterless calls are valid; plus the verified viseme surface (15-viseme Oculus order, `Model.GetVisemeMorph` arg order, `Synthesizer.OnVisemeReached` = `Action<int, TimeSpan>` confirmed).
- **§13's "`WithCaptureAll<T>()` is inert" was a misdiagnosis** — bare `new MovieRecorderOptions()` captures nothing BY DESIGN; `MovieRecorderOptions.Default` (a static) captures the whole scene. Rewritten with the verified MovieRecorder/MoviePlayer surface, plus a new gotcha for the transient hotload MovieRecorder corruption.
- **New meta-gotcha (folded into §10):** read `hasDefault` on `get_method_signature` output before declaring a param "required" — three shipped gotchas overstated requirements this way.
- **Skill + checklist fixes:** `BuildHash()` is the PRIMARY Razor re-render fix (`@ref` + `OnUpdate` is the fallback); the ENGINE's built-in `PlayerController` is Rigidbody-based — the CharacterController note applies to the `facepunch.playercontroller` LIBRARY; the Razor namespace guidance reconciled to folder-derived (`Sandbox.<Folder>.<Class>`); `docs/ADDING-A-TOOL.md` now requires a `search_docs` pass and a `hasDefault` check.
- **Counts updated everywhere:** **275 total / 262 native / 267 handlers.**

### Fixed

- **Latent quote-escaping bug in the shipped `create_npc_brain` codegen** — the `TargetTag` literal is now verbatim-escaped, so a tag containing quotes can no longer break the generated code.

### Deliberately skipped from the Tier-2 backlog (reasons verified live)

`add_network_visible_cull` (`INetworkVisible` is absent from this SDK), `create_minigame_mode` (no game-side Gamemode type to subclass), `create_daynight_cycle` (duplicate — the clock shipped in v1.11.0; the visual half is `add_daynight_sun`), `create_grass_streamer` (GPU-instancing risk), `create_genetics_system` (niche), `create_primitive_builder` (MeshComponent procedural materials are broken); plus the variants folded by shipped tools — six save variants, `create_economy_balance`, `create_stat_tracker` / `wire_services_stats`, the interaction router/interface/modal trio, and `create_loot_table_system`. Full reasons in `docs/TOOL_BACKLOG.md`.

### Roadmap / engine-watch

- **`MovieRecorder` — DONE** (Batch 60 above). **Live lipsync — addressed** (Batch 61): `generate_lipsync_dialogue` drives mouths from the live viseme stream through each model's baked viseme→morph data; a bake-visemes-to-asset path still needs a public API.
- **Multiplayer test harness — likely SHIPPED** (docs-audit finding 2026-07-13): `networking/testing-multiplayer.md` now documents "Join via new instance" (the header network icon) plus `connect local` / `reconnect` console commands — the earlier "still absent" verdict keyed off `search_types "loopback"` returning 0, which misses console-command surface. Needs one live verification session; promoted to the TOP next-wave candidate. See `docs/TOOL_BACKLOG.md`.

## [2.0.0] -- 2026-07-09 "Native"

> Handler/tool counts below are as-of-each-wave snapshots; the FINAL release totals are
> **245 tools / 237 handlers / 232 native across 28 toolsets / 7 lifeline tools**.

**The bridge migrates onto s&box's NATIVE editor MCP server (`http://127.0.0.1:7269/mcp`, shipped in the editor since 2026-07-06). The full tool surface becomes `[McpTool]` static methods discovered by the engine's ToolRegistry — streamable HTTP instead of 50 ms file polling, inline PNG screenshots instead of temp-file paths, XML docs as the schema, `[McpTool.ReadOnly]` permission hints, and hotload = live tool re-registration. All 219 handlers stay; a generated wrapper layer (`Editor/Mcp/`) delegates to the existing dispatch through one gate. Plan + Phase 0/1 live-verification results: `docs/plans/2026-07-08-native-mcp-migration.md`.**

### Added (so far)

- **Codegen pipeline**: `scripts/extract-manifest.mjs` (TS zod schemas → `tools-manifest.json`, 228 tools) + `scripts/emit-mcp-wrappers.mjs` (manifest → 24 `[McpToolset]` classes, 211 tools, 47 read-only + generated `docs/TOOLSETS.md`). Deterministic output, freshness-gated in CI.
- **`Editor/Mcp/McpGate.cs`** — the single hand-written entry point: play-mode guard, handler lookup, error-object → exception conversion (native throw-to-report semantics).
- **`Editor/Mcp/BridgeScreenshotTools.cs`** — `take_screenshot` / `capture_view` / `screenshot_from` / `screenshot_orbit` return **inline PNG image blocks** (`McpResult.Image`); orbit is now C#-native and returns every angle in one response.
- **25 `bridge_*` toolsets** (24 generated + screenshots) — clean, described, collision-free groups browsable via the native `list_toolsets` / `describe_toolset`.
- **`--lifeline` server mode** — the stdio TS server's long-term role: only the editor-down tools (read_log, get_compile_errors, docs, run_self_test, get_bridge_status), for diagnosing a dead editor that the native server dies with.
- **`scripts/audit-mcp-quality.mjs`** — surface quality gate: built-in name/toolset collisions (hard fail), vague descriptions, missing param docs, missing next-step/limit notes.
- **`scripts/verify-native-mcp.mjs`** — live verify-gate over streamable HTTP: toolset inventory, search discovery, read-only spot-runs, nullable-binding check, mutating GUID round-trip, inline-image check, error semantics.
- **`docs/ADDING-A-TOOL.md`** — the new-tool factory: template, checklist, naming conventions, regeneration workflow.

### Added — wave-4 tools (Batch 54: vehicles — NEW bridge_vehicle toolset)

- **`create_vehicle_controller`** — make any Rigidbody prop drivable: 4-corner raycast suspension (spring/damper via ApplyForceAt), mass-scaled engine, speed-scaled yaw steering, lateral grip (low = drift), and a BUILT-IN driver seat (press E; the host assigns the driver network ownership; controller input auto-disabled/restored). Physics APIs verified live via describe_type before generation. Honest limit: compiles + drives is gate-verified; FEEL needs a human playtest.
- **`create_seat_system`** — standalone networked one-occupant seat: host-routed claims, occupant parented with input disabled, exit tries each ExitOffsets entry for the first clear spot. Chairs, benches, turret mounts.
- **`tune_vehicle`** — arcade / drift / offroad / race handling presets applied by property name via reflection: targets the generated controller out of the box, partially tunes anything exposing the same names, reports applied+missing. Live gate e2e: drift preset applied to a compiled controller and read back (GripFactor 0.85 → 0.35).
- **`create_physics_grab_tool`** — physgun-lite: eye-trace grab of Rigidbody props, velocity-set spring follow (physics stays LIVE — collides and swings, unlike the parented carry system), throw on attack1, host-routed ownership.

### Added — wave-3 tools (Batch 53: scene checkpoints, scene orientation, idle/team scaffolds)

- **NEW `bridge_workflow` toolset — the agent-side undo.** `checkpoint_scene` snapshots every root GameObject (full serialization) to temp storage outside the project; `restore_checkpoint` replaces the scene contents from a snapshot (explicit id required — destructive, never guesses; the scene FILE stays untouched until save_scene); `list_checkpoints` browses them. Checkpoint before risky batch edits, roll back if they go wrong — the practical answer to the engine's addon-inaccessible undo (see Known limitations).
- **`describe_scene`** (bridge_scene, read-only) — one-call scene orientation: component histogram, cameras with positions, light count, tag histogram, aggregate content bounds. The scene-level partner to describe_project; works in play mode too.
- **`create_team_assigner`** (bridge_scaffold_gameplay) — host-authoritative smallest-bucket team draft: AssignSmallest(steamId), [Rpc.Broadcast]-mirrored roster, static OnTeamAssigned, Rebalance/GetTeam/GetMembers.
- **`create_idle_income`** (bridge_scaffold_gameplay) — host-auth passive income ticker that auto-wires any sibling wallet with AddMoney(int) via TypeLibrary reflection (zero compile-time coupling), [Sync(FromHost)] TotalEarned, overridable Grant() seam. Completes the idle kit: create_economy_wallet + this + create_offline_progress.

### Added — wave-2 tools (Batch 52: real prefabs, batch buildout, playtest control)

- **Prefabs are REAL now.** `create_prefab` writes a FULL engine serialization (every component with its property values, all children — the same JSON the editor writes; the old handler wrote a minimal descriptor that dropped both). `instantiate_prefab` actually recreates the tree: engine `GameObject.Clone` for registered prefabs, with a guid-remapped deserialize fallback for freshly-written files (repeat instantiations never collide). `get_prefab_info` returns a structured tree summary (per-node component types, referenced prefabs, depth) instead of a raw JSON dump.
- **`batch_delete` / `batch_add_component` / `batch_reparent`** (bridge_batch) — the batch family rounds out, all with the `dryRun:true` validate-first convention. batch_delete reports names + child counts before you commit; batch_add_component skips already-equipped objects; batch_reparent guards against cycles.
- **`playtest_abort`** (bridge_playtest) — stop a stuck/wrong playtest immediately with input state restored; the partial transcript stays readable via playtest_status.
- **`find_broken_references` round 2** — now also scans every `.scene`/`.prefab` FILE for prefab references to deleted/renamed files (`missing_prefab_file`), the break class scene-level checks can't see. `scanFiles:false` to skip.

### Added — wave-1 tools (Batch 51: audit & batch operations)

- **`find_broken_references`** (bridge_validation, read-only) — scene-wide broken-reference scan: renderers with no Model, component properties referencing DESTROYED GameObjects/Components, null component entries whose type no longer compiles. Returns `{ total, showing, truncated, objectsScanned, issues[] }` with fix hints.
- **`batch_set_property`** (bridge_batch — new toolset, the batch_operations home) — one property, same value, many objects, in one call; **`dryRun:true` validates and reports current values without applying** (the relaunch's dry-run safety convention, first landing). Per-object ok/error results with previous values. Scene-mutating (play-mode guarded).
- **`describe_project`** (bridge_project, read-only) — one-call orientation: identity, open scene + object count, scene/prefab file lists, code footprint, custom Component types, installed libraries, with next-tool hints.

### Changed

- `get_bridge_status` and `set_prefab_ref` were inline dispatch special cases invisible to the wrapper gate — both are now registered handlers (`Editor/CoreCommandHandlers.cs`; the dormant `SetPrefabRefHandler` finally registered), one dispatch path for both transports. **223 handlers** (was 219).
- `audit-parity.mjs` version lock now skips a top `[Unreleased]` CHANGELOG section (keep-a-changelog practice) and locks on the first released heading.

### Known limitations

- **No auto-undo for bridge scene mutations** (native-server convention not adoptable):
  `FullUndoSnapshot`/`UndoSystem.Snapshot` verified inert on engine 26.07.08b — the
  built-in tools' undo uses an internal mechanism addons can't reach. Engine-watch item;
  the verify-gate carries a SKIP marker until a public per-edit undo hook ships.

### Dropped from the native surface (native built-ins are 1:1 equivalents)

`spawn_model`, `list_scenes`, `save_scene`, `undo`, `redo`, `remove_component` — use the built-in `scene` toolset versions. All six remain available over file IPC (v1.x path) until v2.1.0.

## [1.20.0] -- 2026-07-08

**+19 tools -- "Director's Cut", the biggest single wave since v1.4.0: same-week support for `Sandbox.MovieMaker` (it just landed in the shipping build) plus a broad Tier-2 sweep -- cinematics & dialogue, networking primitives, interaction & carry, loot/economy, and UI feedback. 228 tools / 219 handlers (was 209/200). Six new handler families, all additive -- no existing tool contract changed. Verify-gated live on Gravehold: all 15 codegen scaffolds generated -> hotloaded -> compile-checked clean -> TypeLibrary-load-confirmed (the gate caught a real SDK bug first -- see below), and the 4 MovieMaker tools live-verified end to end.**

### Added -- MovieMaker / cutscene playback (the headline; Batch 45)

`Sandbox.MovieMaker` reached the **shipping** engine build (confirmed via `search_types` 2026-07-08 -- it was absent 2026-07-02), so the bridge gets its first coverage of the sequencer. New `MovieMakerHandlers.cs` + `moviemaker.ts`.

- **`list_movies`** -- enumerate the project's `.movie` resources (authored in the editor's **Movie Maker** dock): asset-relative path, whether each loads via `ResourceLibrary`, and whether it has a compiled clip. Start here -- an empty list means the movie has to be authored in the dock first.
- **`add_movie_player`** -- wire a `Sandbox.MovieMaker.MoviePlayer` (+ an optional `.movie` `MovieResource`) onto an object, or spawn a new "Movie Player" object; `isLooping` / `timeScale` / `playOnStart` map straight onto the component. Scene-mutating (refused in play mode).
- **`play_movie`** / **`stop_movie`** -- start/stop playback on a given player (or the first in the scene); `play_movie` can load-and-play a different clip and seek via `positionSeconds`, `stop_movie` optionally rewinds. Clips genuinely advance in **play mode** (the response says so in edit mode); both stay callable during play mode.
- **Live-verified end to end on Gravehold:** `add_movie_player` creates + wires a `MoviePlayer`, `play_movie` correctly errors with no movie wired, `stop_movie` + delete clean up. **Honest limitation:** movies are AUTHORED in the Movie Maker dock -- the bridge wires and plays them, it does not author keyframes; real playback verification needs an authored `.movie` clip (the project had none). A record-to-clip tool (`MovieRecorder`) stays queued.

### Added -- Cinematics & Dialogue (hand-authored, engine-proof -- zero assets)

The no-asset cinematic path -- full C# control, works on today's engine with no MovieMaker dependency. New `CinematicsHandlers.cs` + `cinematics.ts`.

- **`create_cutscene_director`** -- a hand-authored camera-shot player: author shots in the inspector as parallel lists (`ShotPositions` / `ShotAngles` / `ShotHoldSeconds` / `ShotBlendSeconds`, optional per-shot `ShotLookAt`), and at runtime it takes over `Scene.Camera` in `OnPreRender` **only while playing** -- hand-rolled smoothstep ease between shots -- capturing the prior transform and restoring it exactly when finished (the same un-apply discipline as `create_camera_shake`). Static `Play()` / `Play("name")`, static `OnCutsceneFinished`, `IsCutscenePlaying` gate; `LockInput` freezes input via `Input.ClearActions()` while still reading the skip action first (a locked cutscene stays skippable); optional razor_lint-safe letterbox overlay.
- **`create_dialogue_system`** -- a sealed state+data component paired with a razor_lint-safe typewriter Razor HUD: lines authored as `"Speaker: text"`, a `TimeSince`-driven reveal folded into `BuildHash`, advance/skip on one input, static `OnLineShown(index, speaker)` (pair with `add_lipsync` to drive morphs/audio per line) + `OnDialogueFinished`. Pairs with `create_interactable` to trigger dialogue on use.

### Added -- Networking primitives (Track B)

The correctness primitives every networked s&box game hand-rolls -- and usually gets wrong. New `NetPrimitivesHandlers.cs` + `netprimitives.ts`.

- **`create_host_rpc_action`** -- the SAFE skeleton for "a client asks the host to DO something": a client-callable `Request()` forwards to an `[Rpc.Host] SubmitRequest()` that re-resolves identity via `Rpc.Caller` (never trusting client args), enforces a per-`SteamId` cooldown (`Dictionary<ulong, TimeSince>`), runs your authoritative TODO, and fires the static `OnActionExecuted(Connection)`. The answer to the #1 exploit class -- `[Rpc.Host]` is callable by any client with forged args, so identity + rate-limit + validation live *inside* the host body. Folds the backlog's `add_rate_limited_rpc`.
- **`add_targeted_rpc`** -- the unicast pattern: a host-side `SendTo(Connection, msg)` wraps its `[Rpc.Broadcast]` in `using ( Rpc.FilterInclude( target ) )` so only the target executes the body (`OnReceived`). The right way to send to one player -- a private prompt, a personal reward -- instead of broadcasting to everyone and filtering client-side.
- **`create_local_player_resolver`** -- the corpus footgun-killer: a static `Local` that finds THIS machine's player (`Network.Owner == Connection.Local` online, the only tagged player offline), cached + revalidated so a respawn re-resolves, plus a static `IsLocal(go)`. Works identically online and offline -- no `if (Network.IsOwner)` guard that silently disables everything in a solo playtest.
- **`add_host_migration_recovery`** -- a proxy->authority transition detector: when `IsProxy` flips true->false (this client is promoted to host), it fires the static `OnBecameHost(GameObject)` + a rebuild TODO, then a deferred `SettleSeconds` validation pass -- the recommended shape for s&box's known host-migration state loss. Inert offline; needs a real migration to fire.

### Added -- Interaction + carry (Tracks E/F)

New `InteractionPackHandlers.cs` + `interactionpack.ts`.

- **`add_interaction_prompt`** -- an eye-traced "Press E" HUD (`.razor` + `.razor.scss`, razor_lint-safe): each frame traces from `Scene.Camera` out to `Range`, and when the crosshair is on a `Component.IPressable` shows a centered pill (its `GetTooltip()` or a default from the action). The visible half of the interaction loop -- pairs with `create_interactable` / `add_interaction_station`, which handle the press. LOCAL/visual-only.
- **`create_hold_to_confirm`** -- a hold-to-fill action: `Progress` fills 0..1 over `HoldSeconds` while an input is `Input.Down`, snaps back (or drains, `DecayOnRelease`) on early release, fires the static `OnConfirmed(GameObject)` at full then cools down. The "hold E to disarm/revive" primitive; read `Progress` for your own radial/bar. Owner-only (`IsProxy`-guarded), single-player safe.
- **`create_carry_system`** -- first-person pickup/carry/throw for physics props: eye-traces `Scene.Camera` for a `Rigidbody` tagged `CarryTag`, routes an `[Rpc.Host]` grab that re-validates and hands network ownership to the carrier (`GameObject.Network.AssignOwnership(Rpc.Caller)`), disables the rigidbody's `MotionEnabled` while held, follows a `HoldOffset` each `FixedUpdate`, and throws with an impulse. Held id is `[Sync(SyncFlags.FromHost)]` so proxies see the carry; `OnPickedUp` / `OnDropped` events. Single-player safe.

### Added -- Loot / economy (Track D)

New `LootEconomyHandlers.cs` + `looteconomy.ts`.

- **`create_gacha_drop_table`** -- a host-authoritative two-level roller: parallel `RarityNames` / `RarityWeights` pick a rarity by cumulative weight, a flat `"Rarity:Item"` list picks the item, a pity counter (`PityAfter`) guarantees the rarest tier, and duplicate detection fires an `OnDuplicate` hook. The roll routes through `[Rpc.Host]` (caller re-validated -- NetFlags is not security) and fans out via `[Rpc.Broadcast]` (`OnRolled`). Folds `create_pity_loot_roll`; pairs with `create_economy_wallet` + `create_inventory`.
- **`create_currency_pickup`** -- a networked coin (`Component.ITriggerListener`): the HOST validates a `PlayerTag` entry, grants `Value` into the player's wallet, then destroys the pickup network-wide (host `Destroy()` replicates -- there's no `NetworkDestroy` on this SDK); an optional magnet accelerates it toward the nearest player. The deposit is a one-line **`Grant` seam** wired to `player.Components.Get<EconomyWallet>()?.AddMoney(...)` -- compiles standalone with no hard reference to a wallet class. Pairs with `create_economy_wallet` + `create_floating_combat_text` (spawn a "+N" popup).
- **`create_offline_progress`** -- the idle-game staple: persists `LastSeenUtc` to `FileSystem.Data`, on enable computes the elapsed time since `LastSeenUtc` (clock-rollback guard, clamped to `MaxOfflineHours`) and replays it through a `SimulateOffline(seconds)` TODO in fixed `TickSeconds` chunks (deterministic, frame-rate independent), then fires the static `OnOfflineProgressApplied(seconds)`. `IsProxy`-guarded so a client can't author their own earnings.

### Added -- UI / feedback (Track C)

razor_lint-safe by construction, modeled on `create_leaderboard_panel`. New `UiFeedbackHandlers.cs` + `uifeedback.ts`.

- **`create_worldpanel_ui`** -- a diegetic, clickable world-space UI: a `PanelComponent` (+ scss) meant to share a GameObject with a `Sandbox.WorldPanel` (the render surface), with example buttons raising a static `OnButtonPressed(id)` so game code reacts without editing the panel. **Scene prerequisite documented:** clicks only fire when a `Sandbox.WorldInput` exists in the scene (on the camera/player) with its `LeftMouseAction` set -- without it `@onclick` never fires.
- **`create_proxy_nametag`** -- a `TextRenderer`-billboarded owner name above a networked player: reads `Network.Owner.DisplayName`, renders only when `Network.IsProxy` (so you never see a tag over your own head), spawns a child object so billboarding doesn't rotate the model, and fades out past `MaxDistance`.
- **`create_combo_meter`** -- a combo system: a headless authoritative component (static `Bump()`, a `ComboWindowSeconds` decay window tracked with `TimeSince`, multiplier tiers) + a pulsing Razor HUD subscribed to the static `OnComboChanged`. Pairs with `create_health_system` / `create_floating_combat_text`.

### Notes -- the verify-gate earned its keep again

- **All 15 codegen scaffolds were generated into the live Gravehold project, hotloaded, and compile-checked** before shipping; every generated component then loaded as a `Component` in the `TypeLibrary`. **The gate caught a real SDK bug:** the generated code called `.IsValid` on `Sandbox.Connection`, which has **no `IsValid` member** on this SDK (3 sites -- `create_host_rpc_action` x2, `add_targeted_rpc`, `create_gacha_drop_table`). Fixed to null checks, regenerated, all 15 then compiled clean. Reflection over folklore -- lesson added to `CLAUDE.md` ("`Connection` has no `IsValid` -- null-check it").
- The 19 new tools need both the updated MCP server (`sbox-mcp-server@1.20.0`) and the republished addon (`BridgeVersion` `1.20.0`). **No breaking changes** to existing tool contracts.

### Roadmap / engine-watch

- **MovieMaker SHIPPED** -- the sequencer's `MoviePlayer` / `MovieResource` are now in the shipping build and covered (above). Still queued: **record-gameplay-to-clip** (`MovieRecorder`) and **offline lipsync generation** (only `Editor.VisemeEditor.Visemes` is public so far).
- **Loopback multi-instance socket is still absent from the shipping build** (`search_types "loopback"` = 0 on 2026-07-08) -- the local-loopback multiplayer-test harness (spawn N clients, drive each via `playtest`, assert sync) stays queued for **v1.21.0**. See `docs/TOOL_BACKLOG.md`.

## [1.19.0] -- 2026-07-07

**+3 tools -- the Game Feel pack. Camera shake, flickering lights, floating damage numbers: the "juice" layer that makes a mechanically-working game feel alive. 209 tools / 200 handlers (was 206/197) -- the bridge crosses 200 editor handlers. Additive -- no existing tool contract changed. All three live-verified (handlers compiled + every scaffold's generated code compile-verified in the game assembly + TypeLibrary-load-confirmed).**

### Added -- game-feel scaffolds (Tier-2 of the mined backlog begins)

All three generate LOCAL/visual-only components (no `[Sync]`) -- the nextSteps note the multiplayer pattern (call from an `[Rpc.Broadcast]` so every client gets the juice).

- **`create_camera_shake`** -- the corpus-standard trauma model: events add `Trauma` (0..1), shake magnitude is `Trauma²` (footsteps barely register, explosions slam), offsets are smooth Perlin noise -- not white-noise jitter -- and trauma decays every frame. Fire from anywhere via the static `CameraShake.Shake(0.4f)`. Applies in `OnPreRender` AFTER controllers position the camera, with an un-apply guard (only subtract our own last write) so it neither fights a controller-driven camera nor accumulates on a static one, and restores the camera exactly at trauma zero. New `GameFeelHandlers.cs` + `gamefeel.ts`.
- **`add_flicker_light`** -- a light-flicker animator attached to an existing PointLight / SpotLight / DirectionalLight (pass `lightId`): five presets -- Candle (soft organic sway), Fluorescent (steady with random dips), Faulty (hard on/off cuts), Pulse (sine breathing), Lightning (dim baseline, rare bright flashes) -- modulating `LightColor` around the color captured on enable and restoring it exactly on disable. `Intensity` sets flicker depth, `Speed` scales the pattern, output smoothed so hard styles read as a light, not strobe noise. The single biggest atmosphere win per call for horror/night scenes -- pairs with `apply_atmosphere`.
- **`create_floating_combat_text`** -- rising, fading, camera-billboarded damage popups over `TextRenderer`: no Razor, no WorldPanel, zero UI setup. Nothing to place -- the generated class carries a static factory (`FloatingCombatText.Spawn(pos, "-25", Color.Red)`) that spawns a popup which rises, fades over `Lifetime`, and destroys itself. Pairs with `create_health_system`: call it from the damage path and every hit prints its number.

### Roadmap

- **Next (v1.20.0): the MovieMaker/cutscene tool family** -- `Sandbox.MovieMaker` (MoviePlayer component, MovieResource, keyframe tooling) is under active upstream development and entirely uncovered by the bridge; same-week engine support is the play that worked for `add_lipsync`. If Facepunch's local loopback socket for multi-instance testing reaches the shipping build first, the multiplayer-test harness takes the slot instead. See `docs/TOOL_BACKLOG.md`.

## License change -- 2026-07-02

**Relicensed from AGPL-3.0-or-later to the s&box Claude Bridge Source-Available License 1.0 (no redistribution).** No code/tool changes; still 206 tools / 197 handlers. You may use the bridge and modify it locally to build your own s&box games (free or commercial); you may NOT redistribute, fork, mirror, repackage, re-host, or offer it as a service. The "s&box Claude Bridge" / "sboxskins.gg" name and branding are trademarks and not licensed for reuse. Versions published before this change remain available under AGPL-3.0-or-later; this and all later versions are governed by the new license. See `LICENSE` and `NOTICE`.

## [1.18.0] -- 2026-07-02

**+5 tools -- same-week support for s&box's brand-new LipSync component, plus the four remaining Tier-1 community-demand scaffolds. 206 tools / 197 handlers (was 201/192). Additive -- no existing tool contract changed. All five live-verified (handlers compiled first try; every scaffold's generated code compile-verified in the game assembly + TypeLibrary-load-confirmed; add_lipsync wiring property-verified on a live citizen).**

### Added -- new engine feature support

- **`add_lipsync`** -- wire up `Sandbox.LipSync`, the component Facepunch shipped **2026-07-01** (the lipsync/viseme Sound Editor update): drives a `SkinnedModelRenderer`'s facial morphs from a playing sound. One call adds LipSync, wires the renderer (same GameObject or `rendererId`), and optionally binds audio -- an existing sound component, or a new `SoundPointComponent` loaded from a `.sound` path -- plus `morphScale` / `morphSmoothTime` tuning. Morphs animate at runtime while the sound plays (verify in play mode via `capture_view`). New `LipSyncHandlers.cs`; tool lives in `characters.ts`.

### Added -- community-demand scaffolds (the Tier-1 backlog, complete)

Mined demand from the 51-game corpus (`docs/TOOL_BACKLOG.md`); with these four, **every Tier-1 item has shipped**.

- **`create_round_state_machine`** (5x demand -- the top ask) -- the full state-machine beyond `create_round_phase_machine`: a sealed manager singleton (`[Sync(SyncFlags.FromHost)] StateIndex`, index-wrap advancing, `CanEnter()` skip) driving an abstract `RoundState` base (Begin/Tick/OnTimeUp/Finish lifecycle, per-state `[Sync]` `TimeUntil`) plus N sealed named-state stubs that auto-attach on start. Transitions announce via a static event + `[Rpc.Broadcast]` mirror with per-index dedupe (never double-fires; `[Sync]` index is the late-joiner reconcile). New `RoundStateMachineHandlers.cs` + `roundstate.ts`.
- **`add_interaction_station`** -- a one-occupant `IPressable` station (crafting bench / shop till / arcade cabinet): occupancy held as a `[Sync(FromHost)]` Guid (GameObjects aren't `[Sync]`-able), client `Press()` routed host-side via `[Rpc.Host]`, a reservation grace window (`TimeUntil`) for the last user, an optional unlock-level gate via a static `ResolveUserLevel` hook, and a static `OnStationOpened` event to open your overlay UI. New `InteractionStationHandlers.cs` + `stations.ts`.
- **`create_event_director`** -- a generalized L4D-style pacing director: interval roll (host-only), cumulative-weight pick over `EventPrefabs` + parallel `Weights`, active-set dedupe, `MaxActive` cap, and a generated `*TimedEvent` companion so every spawned event self-destructs after `EventLifetime`. `RollInterval()` is the documented adaptive-pacing extension point. New `EventDirectorHandlers.cs` + `director.ts`.
- **`create_save_slots`** -- the multi-slot companion to `create_save_system`: a `saveslots.json` manifest (per-slot Used/Name/SavedAtUnix/PlaytimeSeconds -- the slot-picker read) + per-slot payload files over `FileSystem.Data`, versioned POCO with delete-on-version-mismatch, and optional GUID scene reconciliation (`sceneReconciliation: true` -- tombstone destroyed objects, reposition survivors). New `SaveSlotsHandlers.cs` + `saveslots.ts`.

### Docs

- **`.claude-plugin/marketplace.json` brought current** -- it had drifted to v1.16.0 / stale counts (it sat outside the parity script's version-lock set).
- Doc-hygiene sweep: `INSTALL.md` no longer claims the dock must stay open (static frame loop since v1.3.0) or hardcodes a stale handler count; `CLAUDE.md`'s contradictory v1.10.0 handler line removed; stale plan-status notes in `docs/plans/` marked shipped; count style unified on "200+".
- **Engine-watch (researched this release, queued in `docs/TOOL_BACKLOG.md`):** Facepunch merged a **local loopback socket for multi-instance testing** (2026-07-02 -- not in the shipping build yet; a future bridge multiplayer-test harness), and the **MovieMaker/sequencer** system (`MoviePlayer` component, keyframe tooling) remains an uncovered tool-family candidate. Upstream reliability fixes relevant to existing tools: nested-prefab apply no longer wipes instances (#5232), painted-instance collision fixed (#5108), terrain binary data now survives copy/paste/undo (#5156), malformed-packet server-brick fixed (#5151).

## [1.17.1] -- 2026-06-25

**Playtest-harness polish -- two additions to the `playtest` DSL (no new tools; still 201 tools / 192 handlers). Additive -- no existing contract changed.**

### Added

- **`capture` step** -- `{ "capture": "label" }` (or `true`) screenshots the **live player-POV camera** at that frame (`VisualHelpers.FindMainCamera` -> `RenderToBitmap` -> PNG in TEMP); the path is recorded in the transcript. Diagnostic -- never pass/fail. Lets a verification loop leave visual evidence at the moment that matters (right after a jump, an interaction, a state change).
- **`Displacement` assert read** -- `{ "assert": {"read":"Displacement","op":">","value":50} }` returns the scalar distance the player moved from job start (`(WorldPosition - StartPos).Length`, captured at job start). The clean, facing-independent movement proof -- no more leaning on `WorldPosition changed`, which also trips on gravity-settle. Dogfooded live on Gravehold.

## [1.17.0] -- 2026-06-25

**+2 gameplay-verification tools -- `playtest` / `playtest_status`. The bridge can now run a scripted gameplay loop in PLAY MODE and assert the result IN-FRAME -- the gameplay-verification frontier. 192 handlers / 201 tools (was 190/199). Additive -- no existing tool contract changed.**

### Added -- the playtest harness

- **`playtest`** -- run a scripted step list in play mode (async, ticked by an `[EditorEvent.Frame]` job) and get a pass/fail transcript (`start_play` first; poll `playtest_status`). Step verbs: `move` (analog `WishVelocity` drive -- auto-sets `UseInputControls=false` so the controller doesn't overwrite it each frame, zeroes it after), `look` / `lookDelta` (`EyeAngles`), `action` (hold a named input action down -- rising-edge safe), `jump` (invoke the controller's `Jump(Vector3)` by reflection), `set` (a runtime property -- toggles, or teleport via `WorldPosition`), `wait`, and `assert` (read `WorldPosition[.x|.y|.z]` or `<Component>.<Property>` and compare `> < >= <= == != changed`). Restores `UseInputControls`, zeros `WishVelocity`, and releases held actions on teardown.
- **`playtest_status`** -- poll the running/finished job: live `{ step, totalSteps, passed, failed }` while running, the full per-step pass/fail transcript when done.
- **Why an in-addon runner (not TS round-trips):** verifying a playable *loop* needs input + state-reads + assertions that time-align with the game's frames. Two facts force it: (1) the facepunch `PlayerController` reads `Input.AnalogMove` each frame and **overwrites** a `WishVelocity` you set unless `UseInputControls=false`; (2) transient state (a jump's airborne frame) is **gone** by the time a separate bridge call lands. So asserts are evaluated **in-frame**.
- **Dogfooded live on Gravehold** (facepunch `PlayerController` + the `Keeper*` stack): walk -> assert `WorldPosition` changed (0 -> 215u), jump -> assert `IsAirborne` the very next frame (the transient catch that's impossible via round-trips), land -> assert `IsOnGround`. Verdict **PASS**, re-runs clean; a deliberately-too-short landing wait correctly reported **FAIL** (no false-pass).

Limitation: movement injection is controller/spawn-specific (best-effort) -- the harness faithfully reports when a `move` doesn't take; the `set` verb's `WorldPosition` teleport is the robust positioning fallback. Queued: a `capture` step and a displacement assert op.

## [1.16.0] -- 2026-06-20

**Bug-fix & polish release — no new tools (still 199 tools / 190 handlers), no existing contract changed.**

### Fixed

- **Vector params now accept the `"x,y,z"` string form everywhere.** `ParseVector3` was object-only, so `raycast`, `physics_overlap`, `screenshot_from`, `capture_view` (and every other vector-param handler) threw `"The requested operation requires an element of type 'Object', but the target element has type 'String'"` when handed the comma-string form their MCP schemas advertise. `ParseVector3` now routes non-objects through the flexible parser (string / array / number), fixing the entire class in one place. Verified live on `raycast` / `physics_overlap` / `screenshot_from`.

### Docs

- **Troubleshooting §2 corrected** — the bridge frame loop is a **static** `[EditorEvent.Frame]` handler (since v1.3.0); the Claude Bridge dock does **not** need to be open. The old entry wrongly called the visible dock a hard requirement.
- **Troubleshooting §13 (`create_material` dictionary-key error) marked resolved** (v1.7+; verified working live).

### Investigated — no change needed

- **`run_tests` determined infeasible and dropped from the backlog.** s&box unit-test projects target `net10.0` and ProjectReference the whole s&box editor build chain (hammer / shadergraph / dooeditor / moviemaker / …) plus s&box source generators, built inside s&box's own context with its bundled SDK. An external `dotnet test` can't reproduce that (the system carries only the .NET 8 SDK), so there is no clean MCP-server-side path. Use s&box's in-editor test runner.
- **`set_property` / `set_runtime_property` on a `Vector3`** — the old "silent no-op" note could not be reproduced; set + read-back works for object, string, and add-time forms. Resolved by earlier work.

## [1.15.0] -- 2026-06-18

**+5 debug-draw tools ported from the Claude Bridge for Unity -- visualize geometry in the scene. 190 handlers / 199 tools (was 185/194). Additive -- no existing tool contract changed.**

### Added -- debug-draw (Unity carry-over wave 2)

- **`debug_draw_line` / `debug_draw_ray` / `debug_draw_box` / `debug_draw_sphere`** -- draw world-space debug primitives (line, arrow, wireframe box, wireframe sphere) with a color + thickness. They accumulate on a single NotSaved `ClaudeDebugDraw` holder and render in BOTH modes: editor via `Gizmo.Draw.*` (visible in the live viewport), play via `Scene.DebugOverlay.*` (visible in-game and through `capture_view`). Live-verified on the running bridge -- edit-mode gizmos confirmed in the editor viewport, play-mode overlay confirmed via `capture_view` (green box / red sphere / yellow line / blue ray rendered correctly).
- **`debug_clear`** -- remove all debug primitives (destroys the holder).
- Makes invisible logic visible: a `raycast` hit, a `physics_overlap` volume, a `trigger_zone`'s bounds, an NPC's `SightRange`/`FovDegrees`, a patrol path.

Known limitation: edit-mode gizmos render in the live editor viewport but are NOT captured by `take_screenshot`/`screenshot_from` (the editor gizmo pass isn't in that camera render) -- use `capture_view` in play mode to see debug draws through the bridge.

This completes the Unity carry-over wave begun in v1.14.0; the remaining `run_tests` candidate (project test-runner) needs a `dotnet test` feasibility spike and is not included.

## [1.14.0] -- 2026-06-18

**+2 engine/workflow meta-tools ported from the Claude Bridge for Unity -- play-mode time control and a live performance read-out. 185 handlers / 194 tools (was 183/192). Additive -- no existing tool contract changed.**

### Added -- debug-viz / meta-tools (Unity carry-over wave)

- **`set_time_scale`** -- set the running game's time scale during play mode (Unity's `playtest_set_time_scale`): `0` = pause, `0.1` = slow-mo to watch a fast interaction frame-by-frame, `2`+ = fast-forward idle/economy ticks. Sets `Scene.TimeScale` (clamped 0-100); no-ops with an error outside play mode (the edit scene doesn't tick). Returns the applied + previous values. API grounded in shipping game code (sdoomresurrection `Pause.cs`, ss1 `Manager.cs`).
- **`get_profiler_stats`** -- read-only dump of live engine performance counters (Unity's `get_profiler_stats`): FPS, frame/GPU ms, bytes allocated, process memory, exception count, and per-category timings (update/physics/ui/render/network/gcPause) averaged over `frames`. Reads `Sandbox.Diagnostics.PerformanceStats`. API grounded in darkrpog `RoleplayPerfDiagnostics.cs`.

Partial wave -- the higher-value `debug_draw_*` (editor gizmo + play-mode `DebugOverlay`) and `run_tests` (`dotnet test` spike) candidates in `docs/plans/2026-06-17-unity-carryover-meta-tools.md` are not in this release.

## [1.13.0] -- 2026-06-12

**Four new tools (Razor leaderboard scaffold, slot inventory, stat modifier engine, placement-mode pair), review hardening from an Opus deep sweep, and atomic IPC response writes. 183 handlers (was 179). Additive -- no existing tool contract changed.**

### Added -- Wave 3 tools (compile-verified live)

- **`create_leaderboard_panel`** -- scaffold a Razor `PanelComponent` leaderboard bound to `Sandbox.Services.Leaderboards`: async `Get`/`Refresh(CancellationToken)` fetch (API surface confirmed via `describe_type` -- `Board.Refresh` requires a `CancellationToken`, caught by the verify-gate), per-panel fetch cooldown, `BuildHash` override so the panel re-renders on data change, long->int rank cast (caught by the verify-gate). The first scaffold that generates both a `.razor` and a `.razor.scss` file; passes the bridge's own `razor_lint` by construction. Transpiled live to `Sandbox.UI` namespace. Compile-verified live.
- **`create_inventory`** -- scaffold a slot-based inventory component: parallel `ItemIds`/`Counts` lists (the convergent 51-game corpus shape), stack-first `TryAdd` with rollback on overflow, `TryRemove`/`CountOf`/`Move`/`Clear`, static `OnChanged` event for UI binding. The largest SYSTEMS entry in the 51-game `CORPUS-INDEX` (8 games), now one tool call. Empty-string item-id escaping corrected during verify-gate. Compile-verified live.
- **`create_stat_modifier_system`** -- scaffold the Set->Add->Mult stat engine from the ss1/ss2 corpus: generated `{name}Stat` enum, modifiers keyed by source object for clean per-source removal, priority resolution (Set wins, then additive sum, then multiplicative product), `GetStat(stat)` single read point, `OnStatChanged` event. The substrate under the entire progression-upgrades section (8 games). Compile-verified live.
- **`create_placement_mode`** -- scaffold the two-phase ghost->commit build tool: client-local ghost object (`NetworkMode.Never`, tinted valid/invalid), `camera.ScreenPixelToRay` mouse ray for positioning (API confirmed via `describe_type` -- cookbook's `GetMouseRay` does not exist on this SDK, caught by the verify-gate), optional grid snap, client-side validity check for UI feedback, host-side re-validation + `NetworkSpawn` commit, `OnPlaced` event. Compile-verified live.

### Fixed -- review hardening (Opus-assisted deep sweep)

- **`create_networked_player` dead `moveSpeed` param** -- the `moveSpeed` MCP parameter was accepted but never forwarded into the generated scaffold template; the player always spawned with the hardcoded default speed regardless of what was passed. Fixed: `moveSpeed` is now interpolated into the generated C# body.
- **Atomic IPC response writes** -- `res_*.json` files were written in-place; a fast MCP server poll could read a partial response and fail with a JSON parse error. Fixed: responses are now written to a `.tmp` path and atomically renamed, matching the existing atomic write on the request side (v1.5.0). Belt-and-suspenders against the UTF-8 BOM fix from v1.5.0.
- **`get_all_properties` schema cleanup** -- the `includeInherited` parameter appeared in the MCP schema but was unused in the handler (all properties were always returned). Removed from the schema to avoid misleading callers.
- **Stale MathX-only comments** -- inline code comments in several scaffold generators still said "use MathX, Math/MathF are blocked" after the v1.12.0 whitelist correction. Corrected to reflect that `System.Math`/`MathF` compile on the current SDK.

### Notes

- **The verify-gate works.** Four real API/codegen bugs were caught this release before shipping -- `Board.Refresh` requiring `CancellationToken` (not in docs), `GetMouseRay` not existing (cookbook named it wrong; `ScreenPixelToRay` is the real method), inventory empty-string escaping, leaderboard long->int rank cast. All four were caught by the generate->hotload->compile-check loop, not by code review. Reflection over folklore.
- The 4 new tools require both the updated MCP server (`sbox-mcp-server@1.13.0`) and the republished addon (`BridgeVersion` `1.13.0`). The review-hardening fixes (atomic IPC, dead-param, schema cleanup) are addon/server-side; update both halves.
- **No breaking changes** to existing tool contracts.

---

## [1.12.0] -- 2026-06-09

**Six new tools across two waves (lint + scaffolds + asset ops), a CI parity gate, a C# syntax gate, a full semantic bridge-map rebuild, and a whitelist correction. 179 handlers (was 173). Additive -- no existing tool contract changed.**

### Added -- Wave 1 tools (compile-verified live)

- **`create_interactable`** -- scaffold a `Component, Component.IPressable` stub: `Press(Event)` host-validated action, `Look`/`Hover`/`Blur` prompt hooks, `CanPress` gate, `GetTooltip` text, and an `IsProxy` guard so only the owner fires the action. The missing primitive between "scene exists" and "player can do something." `IPressable` surface confirmed via `describe_type` (8 members); generated code compile-verified live.
- **`create_weighted_loot_table`** -- scaffold a loot-table component: parallel `Names`/`Weights` lists, cumulative-weight `Roll()`, optional pity counter (`PityAfter` flag), host-authoritative, static `OnLoot` event. The canonical weighted-pick shape that 7 corpus games hand-rolled independently -- now one tool call. Compile-verified live.
- **`sandbox_lint`** -- pre-compile static scan of project `Code/*.cs` for API whitelist violations before hotload: flags `Array.Clone()` (still blocked), `System.Net.*`, other known-blocked BCL members with file+line and a suggested fix. Catches whitelist rejections that the compiler would surface with no file path, masked by the broken-reference cascade. Tuned against a deliberate live compile-failure test.

### Added -- Wave 2 tools (compile-verified live)

- **`create_save_system`** -- scaffold a versioned `PersistenceManager`: POCO DTO + `FileSystem.Data.WriteJson`/`ReadJsonOrDefault`, version field + delete-on-version-mismatch, dirty-flag debounced autosave, clamp-on-load `Sanitize()`, `IsProxy` guard so only the owner loads/saves. The single most-demanded tool in the 51-game corpus mining (7x independent demand). `BaseFileSystem.ReadJsonOrDefault<T>/WriteJson<T>` confirmed via `describe_type`; generated code compile-verified live.
- **`razor_lint`** -- static scan of project `.razor`/`.razor.scss` files for every known Razor transpiler footgun: switch-expressions in `@code` blocks, non-ASCII characters in `@code`, `PanelComponent` missing `BuildHash`, root type-selector SCSS rules. Reports file+line with a plain-English fix. Direct sibling of `networking_lint`/`scene_validate`; catches the "valid code, opaque crash/silent no-op" class of Razor bugs where `get_compile_errors` shows nothing useful.
- **`copy_asset_with_dependencies`** -- copy a model/material into the project with its full dependency closure (`Editor.Asset.GetReferences(deep:true)`): skips cloud/procedural/transient assets, shadow-guards both the dependency paths and the destination directory against core asset trees (`models/citizen`, `models/dev`, `materials/dev`, `materials/default`). Kills gotchas #4 (ERROR mesh from shadowed asset) and #5 (shadow-induced endless recompile loop) in one tool call.

### Added -- CI & gates

- **`scripts/audit-parity.mjs`** -- zero-dependency Node CI script: checks for duplicate `server.tool()` names in TS, duplicate `Register()` names in C#, full TS<->C# command parity (every `bridge.send()` has a handler; every handler is referenced), and a 4-way version lock (`package.json`, `plugin.json`, `BridgeVersion` const, CHANGELOG first heading). Exits 0 on pass, 1 on any failure.
- **`.github/workflows/ci.yml`** -- GitHub Actions workflow: runs the parity audit on every push and PR to `main`. Catches drift before it ships.
- **`scripts/check-csharp-syntax.py`** -- tree-sitter pre-sync syntax gate: parses every `.cs` file in `sbox-bridge-addon/Editor` and fails on any `ERROR`/`MISSING` node (unbalanced braces, truncated files, broken interpolated-string escaping) before the code is synced into a live s&box editor where a failed compile takes the whole bridge down.

### Changed -- Whitelist correction (affects all s&box devs reading stale advice)

- **`System.Math` and `System.MathF` NOW COMPILE** in s&box game code on the current SDK -- the old "MathX only" rule documented in `CLAUDE.md` and `docs/BRIDGE_GOTCHAS.md` was stale. Both files corrected. `sandbox_lint` does NOT flag `Math`/`MathF` usage.
- **`Array.Clone()` is STILL whitelist-blocked** (verified via deliberate live compile failure: "System.Array.Clone() is not allowed when whitelist is enabled"). Use `.ToArray()`. `sandbox_lint` flags this with the fix.
- `sandbox_lint` tuned accordingly: `Math`/`MathF` removed from the advisory list; `Array.Clone()` is the canonical blocked-clone example.

### Changed -- Bridge map (semantic rebuild)

- Full semantic bridge-map rebuilt **without an API key** (Claude subagent extraction via the graphify skill): **3548 nodes / 4473 edges / 257 communities** with 50 human-named communities. Previous graph was code/AST only. `docs/graph/` updated (`graph.json`, `graph.html`, `GRAPH_REPORT.md`).

### Notes

- **`copy_asset_with_dependencies` destination shadow-guard:** the tool refuses any destination path that is or descends from the core asset trees (`models/citizen/**`, `models/dev/**`, `materials/dev/**`, `materials/default/**`) -- both for the copied asset AND every resolved dependency. This is the gotcha #5 fix; the guard is intentionally conservative.
- **`check-csharp-syntax.py` known false positive:** tree-sitter mis-flags the `$@`-template region in `CreateSaveSystemHandler` that the real Roslyn compiler accepts without issue. Treat any report on that region as advisory; the generated code is correct.
- The 6 new tools need both the updated MCP server (`sbox-mcp-server@1.12.0`) and the republished addon (`BridgeVersion` `1.12.0`). The CI/parity scripts and whitelist correction are server/docs-side only.
- **No breaking changes** to existing tool contracts.

---

## [1.11.0] — 2026-06-09

**Two host-authoritative "game director" scaffolds, and the cookbook fully re-mined across all 51 games. 173 handlers (was 171). Additive — no existing tool contract changed.**

### Added — scaffolds (mined from the 51-game corpus)

- **`create_round_phase_machine`** — a host-authoritative `[Sync(SyncFlags.FromHost)]` phase machine: a `CurrentPhase` enum cycled on a per-phase `TimeUntil` timer (host-only), per-phase duration `[Property]`s, a `Loop` toggle, a `StartPhase(Phase)` host-jump, and a static `OnPhaseChanged` event firing uniformly on host + proxies. The easy single-component variant of the 5×-requested `create_round_state_machine`. Generated code compile-verified live (`describe_type` confirmed the enum / `TimeUntil` / switch-expressions loaded).
- **`create_day_night_clock`** — a host-authoritative time-of-day clock: `[Sync(SyncFlags.FromHost)]` `TimeOfDay` (0–24) + `Day` advancing by `Time.Delta`, `IsDay`/`IsNight` from sunrise/sunset hours, and static `OnNewDay` / `OnDayNightChanged` events. Generated code compile-verified live.

With v1.10.0's `create_economy_wallet`, these complete a **"game director" trio** (currency + round-flow + time). ~180 more mined tool ideas remain queued in `docs/TOOL_BACKLOG.md`.

### Changed — Cookbook fully re-mined

- v1.10.0 added 8 new references + corpus-refreshed 10 high-traffic ones. **v1.11.0 finishes the job:** the remaining **20 system/genre references + all 11 engine references** now carry a "Corpus refresh (2026)" section grounded in the 51-game findings — so **all 41 existing references** (plus the 8 new ones + `CORPUS-INDEX.md`) are refreshed across the full corpus.

---

## [1.10.0] — 2026-06-09

**Four new tools (call a method with args, define input actions, drive the player in play mode, scaffold a currency wallet), eight authoring-tool fixes, two newly auto-detected editor gotchas, a known-issues doc, and a big cookbook expansion (51 games re-mined, +8 references). 171 handlers (was 166). Additive — no existing tool contract changed.**

### Added — New tools

- **`invoke_method`** — call a component method **by name with arguments** on a live GameObject (reflection + the bridge's value coercion, matched by name + arg-count). The arguments-capable companion to `invoke_button`: drive game state, fire the method behind a UI button, or poke a system from the bridge.
- **`ensure_input_action`** — add a custom input action to the project's `<project>.sbproj` (`Metadata.InputSettings.Actions[]`) so `Input.Pressed("MyAction")` resolves. Seeds the default action set if missing, and notes that a restart/reload is needed for a new action to take effect in play mode.
- **`drive_player` / `drive_player_status`** (EXPERIMENTAL) — drive the active `PlayerController` directly across play-mode frames (set `EyeAngles`, feed analog move/wish state by reflection, and hold a named action down every frame so `Input.Pressed` finally catches an edge). A *partial* answer to "the bridge can't synthesize gameplay input" — still no substitute for a human playtest (see `docs/BRIDGE_GOTCHAS.md` #1).
- **`create_economy_wallet`** — scaffold a host-authoritative `[Sync(SyncFlags.FromHost)]` currency component (`AddMoney`/`TrySpend`/`SetMoney`/`CanAfford` + an `OnMoneyChanged` event). Money is host-written so a client can't author their own balance (the classic economy exploit). The first scaffold mined from the 51-game corpus — its generated code was compile-verified live; ~180 more candidate tools are queued in `docs/TOOL_BACKLOG.md` for v1.11.0.

### Fixed — authoring-tool gotchas

- **`set_transform` scale** now accepts an object `{x,y,z}`, a single number (uniform), an array, or a `"x,y,z"` string (new `ParseVector3Flexible`). Verified all forms.
- **`create_gameobject`** honours `parent` **or** `parentId` (`SetParent(keepWorldPosition:false)`) — verified the child reports the correct parent.
- **`duplicate_gameobject` + `grid_duplicate`** work in **edit mode** (wrapped in `using (scene.Push())`) — no more "No Active Scene". Verified.
- **`place_along_path`** gained `align` + `randomizeYaw` flags; the default is now **deterministic** (no surprise random yaw).
- **`execute_csharp`** sweeps stale `__Exec_*.cs` temp files on bridge start, and multi-line snippets compile (the body is injected inside the generated method's try-block, not at class scope).
- **`spawn_model`** with a bad / unmounted path returns a **`warning`** ("resolved to the ERROR placeholder model …") instead of a clean "success" — verified.
- **Vector / color coercion** widened across `set_tint` (+`color` alias), `add_light` / `set_fog` / `set_skybox`, etc. — they accept object / `"r,g,b,a"` / array forms (the shared `Vector3Schema` / `ColorSchema` are now object|string unions).
- **`get_compile_errors`** filters the broken-reference cascade so it surfaces the real CS diagnostic.

### Added — Auto-detected editor gotchas

- **"Default Surface not found"** on `raycast` / `raycast_terrain` is now caught and returned as `{ recoverable: true, recovery: "restart_editor" }` with a plain-English message, instead of a cryptic exception.
- **`install_asset`** returns `restartRecommended: true` and **`trigger_hotload`** includes a `packageNote` — a newly-added `PackageReference` no longer silently fails to resolve. Both marked auto-handled in `docs/BRIDGE_GOTCHAS.md`.

### Added — Known-issues doc

- **`docs/BRIDGE_GOTCHAS.md`** — the engine limits + workflow lessons that are *not* code-fixable (input synthesis, surface registry, package refs, asset-pipeline ERROR mesh, Razor/SCSS quirks, the API whitelist, GPU stalls), each as Symptom → Why → Fix/Workaround.

### Changed — Cookbook (51-game re-mine)

- The **`sbox-cookbook`** skill was re-mined across **51** current public-source s&box games (was 47 — added `facepunch.ss2`, `despawn.murder`, `facepunch.fair`, `barrelproto.ragroll`). **+6 genre references** (social-deduction, survivor-roguelite, coop-kitchen, board-game, casino-gambling, physics-sports) and **+2 system references** (ai-director, services-backend). High-traffic references (economy-currency, save-persistence, round-match, shop-vendor, progression-upgrades, leaderboards-services, building-placement, anti-cheat, tycoon-idle, deathmatch-arena) got a "Corpus refresh" pass with the newly-mined implementations. New **`references/CORPUS-INDEX.md`** cross-references which games implement each system/genre — so a recipe can be composed by pulling pieces from several games. Per-game sources and provenance are recorded in the cookbook's bundled reference files.

### Added — Bridge map (knowledge graph)

- **Shipped a graphify knowledge graph of the bridge at `docs/graph/`** (`graph.json`,
  `graph.html`, `GRAPH_REPORT.md`, `README.md`). Every tool maps to its C# `IBridgeHandler`
  and to the docs — `IBridgeHandler` is the spine (top god node), and `MyEditorMenu.cs` is
  flagged as a large monolith to split. Browse it with `graph.html`, or query it with
  `graphify query "…" --graph docs/graph/graph.json`. It can go stale — `scripts/regen-graph.ps1`
  does a deterministic code/AST refresh, and re-running the `/graphify` skill rebuilds the full
  doc-inclusive graph. Maintainers should regenerate it as part of every release.

## [1.9.0] — 2026-06-07

**6 new inspection & validation tools — see what replicates, lint your networking, catch scene footguns, read saves/services, drive input. 166 handlers (was 160). Plus a new `sbox-cookbook` recipe skill and an AGPL relicense.** Verified live against the SDK. New TS module `tools/inspection.ts`; the C# handlers are **"Batch 37"** in the addon. Additive — no existing tool contract changed.

### Added — Inspection & validation (Batch 37)

- **`inspect_networked_object`** — dump a single object's `Network.*` state plus **every component's `[Sync]` fields** (flags + live values), so you can see exactly what an object replicates.
- **`networking_lint`** — static scan for multiplayer footguns: unguarded `[Sync]` mutators, money / health / score declared as plain `[Sync]`, `List`/`Dictionary` typed as `[Sync]`, and `[Rpc.Host]` methods that never re-check `Rpc.Caller`.
- **`scene_validate`** — flags scene-setup footguns: no camera in the scene, stray `Rigidbody`s on root objects, and `IsTrigger`-vs-trace mismatches.
- **`save_inspect`** — list / read / diff the project's `FileSystem.Data` save files.
- **`services_query`** — read `Sandbox.Services` stats + leaderboards.
- **`simulate_input`** — drive named input actions in play mode (press / hold a bound action without a keyboard), to exercise input-driven systems.

### Added — `sbox-cookbook` skill

- A new bundled skill: a master **router** indexing **code-grounded recipes** mined from **27 current (2026) public-source s&box games** plus the modern engine repos. Its `references/` hold **11 engine** references (networking-authority, architecture, components-lifecycle, player-controller, ui-razor, combat-weapons, input-interaction, physics-traces-movement, worldgen-rendering, performance-threading, data-assets), **15 systems** (inventory, economy-currency, shop-vendor, save-persistence, progression-upgrades, gacha-loot, leaderboards-services, idle-offline, building-placement, crafting, dialogue, round-match, spawning-waves, anti-cheat, level-design), and **14 genre recipes** (tycoon-idle, shopkeeper, document-sim, roleplay, sandbox-voxel, social-hub, platformer-obstacle, deathmatch-arena, card-battler, survival-horror, gacha-crawler, puzzle, vehicles, party-microgame). Ask "how do I build a tycoon / an inventory / a save system?" and it routes to a grounded how-to. Full bundled skill set: `sbox-api`, `sbox-build-feature`, `sbox-setup`, `sbox-scaffold-game`, `sbox-cookbook`.

### Changed — License

- **Relicensed GPL-3.0 → AGPL-3.0-or-later** (the `LICENSE` file and every `license` field). The AGPL's network/hosted-service clause now applies: if you run a modified bridge as a service, make your modified source available to its users.
- **Added a `NOTICE`** with the AGPL summary plus a **branding/trademark** note: the code is open under AGPL, but the **"s&box Claude Bridge"** / **"sboxskins.gg"** name and branding may not be reused to present a fork as the original.

### Notes

- The 6 new tools need both the updated MCP server (npm `@1.9.0`) and the republished addon (`BridgeVersion` `1.9.0`); the cookbook skill and the license/NOTICE changes are docs-side.
- **No breaking changes** to existing tool contracts.

---

## [1.8.0] — 2026-06-06

**Reliability + correctness pass on core authoring tools, verified live in the editor. Additive — no existing tool contract changed.**

### Fixed
- **`set_property` / `add_component_with_properties` value types now actually apply.** Value-type values reach the addon as strings, and `PropertyDescription.SetValue` does NOT auto-parse a string into `Vector3`/`Vector2`/`Color`/`Rotation` — it silently no-op'd (the long-standing "Vector3 reads back default" bug). `CoercePropertyAndSet` now builds the typed value explicitly (new `ExtractFloats` accepts `"x,y,z"`, `[..]`, or `{...}` forms). Verified: `Vector3` + `Color` set and read back correctly.
- **`set_material_property` auto-creates a `MaterialOverride`** (via `Material.Create`) when none is assigned, instead of erroring "assign a material first." Verified (`autoCreatedMaterial:true`).
- **Runtime-spawned GameObjects are addressable** — `get_/set_property` (and the `*_runtime_property` variants) fall back to scanning the live scene by runtime `.Id` when the persisted-GUID lookup misses, so objects created during play can be targeted.
- **`create_scene` now writes under `Assets/` and registers the scene** — it was writing to a project-root `Scenes/` dir (outside `Assets/`, so unloadable) and not registering with AssetSystem, so `load_scene` failed until a manual `recompile_asset`. It now honours the `path` param under `Assets/`, registers/compiles the scene, and returns the assets-relative path — `create_scene` → `load_scene` works directly. `load_scene` also self-compiles an unregistered scene as a fallback.

### Added / improved
- **`set_fog`** supports `cubemap` (`CubemapFog`) and `volumetric` (`VolumetricFogVolume`) in addition to `gradient`.
- **`invoke_button`** can call methods that take parameters — pass `args` (count-matched, coerced per parameter type).
- **`add_sync_property`** honours `syncFlags` → `[Sync( SyncFlags.X )]`; **`add_rpc_method`** honours `methodParams` → a real method signature.
- **`trigger_hotload`** nudges a recompile of externally-edited C# by bumping project `.csproj` timestamps (falls back to advising play-mode / `restart_editor`).
- Map/terrain/cave/forest tools now expose a `component` override (target a builder named differently than MapBuilder/CaveBuilder/ForestGenerator) and give a clearer "component not found" error pointing to it + the `invoke_button` fallback.

### Known issues
- `create_scene` does not yet generate `includeDefaults` content (camera / light / ground) — it writes an empty scene; add those via `create_gameobject` / `add_light` after loading.

## [1.7.0] — 2026-06-04

**Four-wave release: play-mode eyes, reliability, playable-game scaffolds, and NPC brains. 169 tools / 160 handlers.** Built locally and verified against the live editor (including live play-mode capture and compiling generated code). Additive — no existing tool contract changed.

### Added — Play-mode eyes
- **`capture_view`** — capture a PNG of the scene from a camera via `CameraComponent.RenderToBitmap` + `Bitmap.ToPng`. Unlike `take_screenshot`/`screenshot_from` (editor-only), this renders a camera's view of the ACTIVE scene, so **it works in play mode — capturing the running game** (the player POV + HUD with `renderUI`). No args = the live main camera; `position`/`id` = a temp camera that never disturbs the game's own. Returns the saved PNG path (uniquely named — no filename collisions). VERIFIED capturing the running game.

### Added — Playable-game scaffolds (the non-coder mission)
- **`set_component_reference`** — wire a component property to a live scene GameObject by GUID (the missing gap: `set_property` is primitives-only, `set_prefab_ref` is prefab-assets-only).
- **`add_component_to_new_object`** — atomic create GameObject + add component + set properties.
- **`create_objective_system`** / **`create_health_system`** / **`create_pickup`** — generate self-contained, sandbox-legal gameplay components (the win/lose primitive; health/damage with host-authoritative guards; collectibles). Generated code VERIFIED to compile.
- Bundled **`sbox-scaffold-game`** skill (first-person preset) that orchestrates these into a playable starter.

### Added — NPC brains
- **`create_npc_brain`** — generate an FSM behavior `Component` (Idle/Patrol/Wander/Chase/Search/Flee/Ambush) with occlusion-aware perception (FOV cone + range + LOS trace + hearing) and 5 presets (patrol/guard/hunter/swarm/skittish); host-authoritative + `[Sync] CurrentState` when networked. Generated code VERIFIED to compile + load (full `[Property]` surface).
- **`place_patrol_route`** / **`assign_patrol_route`** — author + wire patrol waypoints into the brain.
- **`create_npc_spawner`** — generate a spawner (continuous / waves / burst, max-alive cap, networked).
- **`simulate_npc_perception`** — READ-ONLY edit-mode verifier: runs the exact FOV+range+LOS check and reports `canSee/inFov/inRange/losBlocked/blockedBy` — verify perception without entering play mode.

### Added — Reliability
- **`run_self_test`** — end-to-end health check / regression gate (create → component → model → bounds → capture → recompile → cleanup), pass/fail per subsystem, self-cleaning, refuses to run in play mode.

### Fixed
- **`create_material` crash** — it called `p.GetProperty("name")` and threw `KeyNotFoundException` when the tool sent `path` (the "dictionary key" bug). Now reads `path` (or legacy `name`/`directory`) and honors `properties`. VERIFIED.
- **`is_playing` stale flag** — `isPlaying` no longer folds in the unreliable `sessionPlaying` (true after a restart); authoritative flag is `gameFlag || tracked`, with `sessionPlaying` kept as a diagnostic field. VERIFIED.
- **`get_bridge_status`** now reports the bridge `version` + `handlerCount` (the tool was reading a dead `editorVersion`); wired to the addon's built-in status command.

### Notes
- `capture_view` + the 5 scaffold + 5 NPC tools need both the updated MCP server and the republished addon; the `create_material`/`is_playing`/`get_bridge_status` fixes are addon-side; `run_self_test` is server-side.
- Deferred to a follow-up: the `create_player_controller` upgrade (third-person/top-down + scene placement — spec'd, with a couple of unverified input APIs to confirm) and `ensure_input_action`.

---

## [1.6.0] — 2026-06-03

**Animation + better eyes. Drive `SkinnedModelRenderer` animgraphs (`set_animgraph_param`, `play_animation`, `list_animations`), read world bounds (`get_bounds`), and capture an object from multiple angles in one call (`screenshot_orbit`). 157 tools / 149 handlers.**

### Added

- **`list_animations`** — list the sequences on a GameObject's `SkinnedModelRenderer` (merges `Sequence.SequenceNames` + `Model.AnimationNames`) and report whether it's AnimationGraph-driven. A spawned Citizen returns 500+ sequences.
- **`set_animgraph_param`** — set an AnimationGraph parameter via `SkinnedModelRenderer.Set(name, value)` — the way to drive Citizen/animgraph motion (e.g. `move_x`/`move_y` float, `b_grounded`/`duck` bool, or a Vector3). Poses preview in-editor when `PlayAnimationsInEditorScene` is on. Verified: `duck=1.0` visibly crouches a Citizen.
- **`play_animation`** — play a named sequence by setting `SkinnedModelRenderer.Sequence` (best for raw-sequence models; for animgraph characters prefer `set_animgraph_param`). Validates the name against the model's sequences and returns the available list on a miss.
- **`get_bounds`** — a GameObject's world-space bounding box: center, size, extents, mins/maxs, radius (`GameObject.GetBounds()`). Objects with no renderer report `empty:true` with their world position.
- **`screenshot_orbit`** — capture an object from several angles in one call. Drives `get_bounds` for framing, then `screenshot_from` per angle (each its own frame — the reliable capture path), spacing shots so s&box's 1-second screenshot filenames don't collide. Returns the saved PNG paths in order for Claude to read.

### Notes

- Additive only — no existing tool contract changes. `set_animgraph_param` / `play_animation` are play-mode-guarded edit-mode tools (like `pose_citizen`); `get_bounds` / `list_animations` are read-only.
- The 5 new tools need both the updated MCP server (npm `@1.6.0`) and the republished addon; existing tools are unaffected by version skew.

---

## [1.5.2] — 2026-06-03

**`recompile_asset` — compile a project asset from the bridge (`Editor.AssetSystem.RegisterFile` + `Asset.Compile`), so an asset Claude writes or edits gets its compiled form regenerated without a manual editor step. 152 tools / 145 handlers.**

### Added

- **`recompile_asset`** — register + compile a project asset by path. Verified on materials (`.vmat` → `.vmat_c`). Pairs with `write_file` / `edit_script`: edit an asset, then recompile it so the change takes effect. Built on the editor's `AssetSystem` (`RegisterFile` → `Asset.Compile(true)`, with a `CompileResource` fallback).

### Notes

- **Particles still can't be authored through the bridge.** This release confirmed *why*: the engine reports "Failed to find compiler for `.vpcf`" — there's no particle compiler reachable from the addon's compile path (unlike materials, whose compiler is loaded). Author particles in s&box's particle editor, then `spawn_vpcf` plays the compiled result.
- No breaking changes.

---

## [1.5.1] — 2026-06-03

**Closes the dev loop + adds library awareness. `restart_editor` lets Claude relaunch the editor itself (no more manual restarts to apply C# changes); `list_libraries` detects installed addons; and a `sbox-setup` onboarding wizard greets new users. 151 tools / 144 handlers.**

### Added

- **`restart_editor`** — restart the s&box editor and wait for the bridge to reconnect (`EditorUtility.RestartEditor` + headless unsaved-scene handling, saves by default). Closes the C#-edit→recompile loop: addon/bridge changes apply without a manual restart. Uses the engine API directly — **no dependency on the `auto_restart` library** (that's just where the mechanism was confirmed).
- **`list_libraries`** — list installed s&box libraries/addons (reads `Libraries/` + each `.sbproj`). Lets Claude discover what's available to build on — character controllers (Shrimple `fish.scc`, `facepunch.playercontroller`), world tools, etc. — and leverage them via `add_component_with_properties` instead of writing from scratch.
- **Setup wizard** — a `sbox-setup` skill plus a connect-time nudge in the MCP `instructions`: welcomes, verifies the bridge, detects libraries, recommends a first move, and points to help + feedback.

### Notes

- No breaking changes — all 1.5.0 tools unchanged.

---

## [1.5.0] — 2026-06-03

**16 new tools — self-diagnosis, aimed screenshots, navmesh + spatial queries, real `.vpcf` particles, console/C# execution, and live docs search — plus a security & correctness hardening pass from an external code audit. Total: 150 tools / 142 handlers.**

### Added

**Self-diagnosis (MCP-server-side — work even when the editor has crashed):**
- `read_log` — tail/filter `sbox-dev.log` directly (auto-locates via Steam `libraryfolders.vdf`, or `SBOX_LOG_PATH`).
- `get_compile_errors` — surface the latest C# compile failures from the log. Claude can finally see its own errors instead of guessing.

**Verification & camera:**
- `screenshot_from` — **aim a screenshot at any object or point.** `take_screenshot` always renders from the scene's Main Camera (one fixed angle), which made most visual changes impossible to verify; `screenshot_from` moves the Main Camera to frame a target, captures, and restores it. This is what makes the authoring layer screenshot-verifiable.
- `frame_camera` — move the editor viewport to focus an object/point (the editor's own view; distinct from the screenshot camera).

**Navigation (real editor ops, not component wrappers):**
- `bake_navmesh` — enable + bake the scene navmesh (`NavMesh.BakeNavMesh`) so agents can path. Async.
- `get_navmesh_path` — query a walkable route between two points (`GetSimplePath`); returns the path or `reachable:false`.

**Spatial & reflections:**
- `physics_overlap` — sphere/box volume query (the volume counterpart to `raycast`): which objects sit in a region.
- `bake_reflections` — bake all `EnvmapProbe`s (`EnvmapProbe.BakeAll`); a placed probe captures nothing until baked.

**Particles:**
- `spawn_vpcf` — play a compiled `.vpcf` via `LegacyParticleSystem` — the reliable particle path (the Batch 18 runtime `ParticleEffect` graph does not render through the bridge). See Known Issues re: source assets.

**Console & C#:**
- `console_run` — run an s&box console command / ConCmd (`ConsoleSystem.Run`).
- `execute_csharp` *(experimental)* — compile + run a C# snippet in the unsandboxed editor context (temp `[ConCmd]` → hotload → run → read result from the log → clean up).

**Object utilities:**
- `remove_component` (counterpart to `add_component_with_properties`), `get_tags` (counterpart to `set_tags`).

**Documentation search (MCP-server-side):**
- `search_docs`, `get_doc_page`, `list_doc_categories` — search the official `Facepunch/sbox-docs` guides (225 pages; git-tree cached + raw Markdown).

### Security & Fixed

External code-audit remediation:
- **Handler errors were reported as success.** The dispatch hardcoded `success=true`, so a handler returning `{ error }` (e.g. "Path traversal denied") surfaced as a successful call — `write_file` even printed "File written successfully". The dispatch now detects a handler-level `error` and reports `success=false`; `write_file` no longer claims false success.
- **Path-traversal hardening.** All file/asset handlers resolve user paths through one separator-safe `TryResolveProjectPath` helper (canonicalize + containment) — 25 call sites. Previously `list_project_files`/`create_script`/`create_scene` had no containment check (a rooted path escaped the project).
- **Generated C# identifiers are sanitized** (`SanitizeIdentifier`) — a `name` with spaces/punctuation/keywords no longer emits an uncompilable `class` declaration.
- **Atomic IPC.** The MCP server writes request files to a temp path then atomically renames, so the editor can't consume a half-written large payload.
- **Honest networking schemas.** `add_sync_property` / `add_rpc_method` params + descriptions now reflect what the addon actually does (annotate an existing property with `[Sync]`; generate an empty RPC stub) instead of advertising unimplemented options.
- **Version pinning + doc reconciliation.** The plugin pins the server version; stale tool counts (78/99/109/131) reconciled to the real totals.

### Notes & Known Issues

- **Particles:** the experimental Batch 18 runtime `ParticleEffect` tools (`spawn_particle` etc.) still do not render visibly through the bridge (confirmed: at most a single flat sprite, nothing in play mode). `spawn_vpcf` is the supported path, but no flame `.vpcf` ships in a bridge-loadable form (`ParticleSystem.Load` returns null for the cloud-cached `impact.generic`); a project-owned `.vpcf` is under investigation.
- `execute_csharp` is experimental (hotload latency; briefly recompiles the project editor assembly).
- The `is_playing` `sessionPlaying` field can read stale (true in edit mode after a restart) — trust `gameFlag`.
- **No breaking changes** to existing tool contracts (networking schema descriptions clarified, not removed).

---

## [1.4.0] — 2026-06-02

**32 new authoring tools across 7 batches — lighting & atmosphere, characters, scene layout, environment scatter, and object utilities. The bridge goes from "manipulate one object at a time" to "compose a whole scene." Tool count 99 → 131 (handlers 100 → 132).**

### Added

**Visual & Atmosphere (Batch 17 — 7 tools)** — author scene mood directly instead of hand-driving `add_component_with_properties` (which can't even set a Color):
- `add_light` — directional / point / spot / ambient. Note: s&box lights have **no brightness field** — intensity is the colour's magnitude, so the `brightness` param scales the colour's RGB (use >1 for HDR).
- `set_fog` (gradient distance haze), `add_post_process` (generic — Bloom / Tonemapping / ColorAdjustments / Vignette / DepthOfField / etc. on the main camera), `set_skybox`, `add_envmap_probe`.
- Presets: `apply_atmosphere` (`horror-night` / `foggy-dawn` / `warm-interior` / `overcast` — a full day→night transform in one call) and `apply_post_fx_look`.

**Characters & Models (Batches 19–20 — 9 tools)** — spawn, dress, pose, accessorize:
- `spawn_model` (any model + tint), `spawn_citizen` (animated Citizen + `CitizenAnimationHelper`, idles in-editor), `dress_citizen` (apply `.clothing` resources via `ClothingContainer`), `set_bodygroup`, `pose_citizen` (hold type / move style / sitting / crouch).
- `equip_model` (attach a prop to a bone or attachment point), `set_look_at` (gaze tracking), `add_ragdoll` (`ModelPhysics`), `set_expression` (facial morphs; call with no morph to list the model's available blendshapes).

**Scene & Level Building (Batch 21 — 5 tools)**:
- `snap_to_ground` (raycast-drop onto the surface below), `align_objects`, `distribute_objects`, `grid_duplicate` (array copies in an X/Y/Z grid), `measure_distance` (read-only).

**Environment & Props (Batch 22 — 3 tools)**:
- `scatter_props` (N model copies in a radius — seeded, ground-snapped, grouped), `randomize_transforms` (yaw/scale variation for a natural look), `group_objects` (reparent a set under a centroid empty).

**Object Utilities & Queries (Batch 23 — 4 tools)**:
- `find_objects` (query by name / component type / tag — read-only, and composable: feed the GUIDs into align / distribute / set_tint / group / delete), `set_tint`, `replace_model`, `set_tags` — each operates on one object or many.

**Experimental — VFX / Particles (Batch 18 — 4 tools):** `spawn_particle`, `create_particle_effect`, `add_trail`, `add_beam`. These compile and build the correct component graph, but **runtime particle rendering is currently unverified through the bridge** — s&box's component `ParticleEffect` needs sprite assets plus a live-play view the bridge can't supply. Shipped as experimental; for guaranteed-visible particles use a legacy `.vpcf` + `LegacyParticleSystem`, or wire them up by hand in the editor.

### Notes

- **Design principle this release — verifiable-first.** Every non-experimental tool produces a static mesh / pose / state that renders in the editor viewport (screenshot-verifiable) or returns checkable data. That's the lesson from the particle batch, which is the one category the bridge fundamentally can't see.
- New MCP tool modules: `tools/visuals.ts`, `tools/characters.ts`, `tools/leveltools.ts`, `tools/objecttools.ts`.
- **No breaking changes** — every v1.3.2 tool is unchanged.

---

## [1.3.2] — 2026-06-02

**Bridge liveness + diagnostics. Fixes the "connected, 0ms ping, but every scene call times out" report: `ping` / `connect` were a file-existence check, not proof the editor was alive.**

### Fixed

- **Permanent false-positive "connected".** `status.json` was written once at startup and never updated or removed, so after the bridge ran even once, `connect()` / `ping()` / `isConnected()` reported "connected" forever — including when the editor was closed, crashed, or its frame loop had stalled (`ping()` only stat'd that file, hence the ~0ms). It is now a **heartbeat**: the addon refreshes `status.json` (with a `heartbeat` timestamp) from the editor frame loop, and the MCP server treats a heartbeat older than 5s as **disconnected**. Driving it from the frame loop means a stalled/closed editor goes stale within seconds — no separate (and unreliable) shutdown event needed. Old addons without a `heartbeat` field are still treated as connected, so upgrading the server alone never regresses a working setup.

### Added

- **`SBOX_BRIDGE_IPC_DIR`** (MCP server) — overrides the IPC directory. The #1 cause of a silent 30s hang is the Node side (`os.tmpdir()`, reads `TEMP` first) and the C# side (`Path.GetTempPath()`, reads `TMP` first) resolving **different** temp dirs; point both at one dir to realign. The addon logs and writes its resolved dir (`[SboxBridge] … IPC at <dir>` and `status.json.ipcDir`) so you know what to match.
- **Timeouts now name the failing side.** Instead of a bare `Request timed out after 30000ms`, a timeout reports whether the editor never consumed the request (not running / wrong IPC dir) or consumed it but never responded (frame loop stalled / handler errored) — with the IPC dir and a pointer to the `[SboxBridge]` console logs.
- **Richer `get_bridge_status`** — reports the IPC directory, heartbeat age, and the result of a real round-trip, so "heartbeat live but requests not draining" is distinguishable from "fully working" and "not connected". Also surfaces the bridge build version.
- **`BridgeVersion`** in `status.json` and the **Editor → Claude Bridge → Status** dialog, so a marketplace-addon-vs-MCP-server skew is visible at a glance.
- **Transport regression tests** — `npm test` (Node's built-in test runner, zero new deps) covers heartbeat-staleness, the timeout diagnostics, and the IPC-dir override.

### Changed

- Removed the misleading **WebSocket / port 29015** references from code and docs. There is no socket — `SBOX_BRIDGE_HOST` / `SBOX_BRIDGE_PORT` are cosmetic (shown only in `get_bridge_status`). INSTALL.md's old "change the port in `MyEditorMenu.cs`" step (the file no longer contains `29015`) is replaced with a temp-dir-mismatch fix guide.

---

## [1.3.1] — 2026-05-16

**Discoverability patch. No tool changes. Surfaces the new Claude Code plugin and the screenshot-driven workflow inside the existing distribution channels.**

### Added

- **`McpServer.instructions`** — the MCP server now ships an `instructions` string that surfaces at the top of every Claude Code session that uses the bridge (the same mechanism Supabase / TurboTax use). It tells Claude how to work effectively with the bridge: call `get_bridge_status` first, take screenshots and read them after visual changes, use `describe_type` before guessing s&box APIs, scene-mutating tools refuse during play mode, and points at the `sbox-claude` plugin for the full workflow.
- **`sbox-mcp-server` README rewritten** — leads with the Claude Code plugin install path, falls back to the manual three-step install. Tool table updated to 99 working (was 78 from v1.0.0 docs). Includes the two-discipline summary: screenshot after visual changes, `describe_type` before guessing.
- **`claudebridge.sbproj` Description rewritten** — mentions the `sbox-claude` plugin install path inline, so users discovering the addon through the s&box Asset Library see the plugin in the first paragraph.

### Why a patch and not a minor

No tool surface changes, no behavior changes outside of the new instructions text that surfaces at session start. Pure DX patch — users on v1.3.0 should upgrade for the better defaults, but nothing they were doing will break.

---

## [1.3.0] — 2026-05-16

**Closes 5 community issues. Critical bootstrap-crash fix, RPCs work without the dock, hierarchy query gets smarter, phantom tools removed.**

### Fixed

- **Editor bootstrap crash on current s&box builds.** `ClaudeBridge`'s static constructor called `Log.Info`, which dispatches to the menu addon's `ConsoleOverlay`. `ConsoleOverlay.OnConsoleMessage` constructs a `ConsoleEntry` panel, and `Panel..ctor()` calls `InitializeEvents()` which accesses `Game.TypeLibrary` — but `TypeLibrary` is explicitly disabled during `PackageLoader.AddAssembly → RunAllStaticConstructors`. Result: `InvalidOperationException: TypeLibrary is currently inaccessible. Reason: Disabled during static constructors.` and any project depending on this addon becomes unopenable. **Fix:** keep the static constructor empty and run logging / handler registration / IPC bridge startup from the first `[EditorEvent.Frame]` callback (gated by an `_initialized` flag) so init happens after bootstrap completes and `TypeLibrary` is accessible. **Original report and patch by [@FurkanZhlp](https://github.com/FurkanZhlp) in [PR #6](https://github.com/LouSputthole/Sbox-Claude/pull/6).**
- **GitHub issue [#2](https://github.com/LouSputthole/Sbox-Claude/issues/2)** — RPCs hung for 30s whenever the **Claude Bridge** dock panel wasn't open. Reason: `[EditorEvent.Frame]` was on an instance method of `BridgePoller : Widget`, so it only fired during the lifetime of the dock instance. **Fix:** moved the frame handler to a static method on `ClaudeBridge` itself — `[EditorEvent.Frame] public static void OnEditorFrame()`. The dock widget remains as a status display but the bridge no longer depends on it being open.
- **GitHub issue [#4](https://github.com/LouSputthole/Sbox-Claude/issues/4)** — `get_scene_hierarchy` ignored its `maxDepth` parameter and always returned the full tree, overflowing token budgets on real scenes. **Fix:** the handler now honors `maxDepth` (default 10) and gates recursion at that depth. **Bonus:** added optional `rootId` parameter to start traversal from a specific GameObject GUID instead of the scene roots — drill into a subtree without paying for the rest of the scene.

### Removed

- **GitHub issue [#3](https://github.com/LouSputthole/Sbox-Claude/issues/3)** — Removed 10 tools from the MCP server that never had handlers in the bridge addon and only ever returned `"Unknown command: ..."`. The s&box editor doesn't expose public APIs for any of these:
  - **console** (3): `get_console_output`, `get_compile_errors`, `clear_console`
  - **playmode** (2): `pause_play`, `resume_play`
  - **publishing** (5): `build_project`, `get_build_status`, `clean_build`, `export_project`, `prepare_publish`
  
  Tool count goes from 109 → 99 (all working). The MCP `--help` listing and README tables are updated to match. For console output, read `<sbox>/logs/sbox-dev.log` directly.

### Confirmed already fixed

- **GitHub issue [#1](https://github.com/LouSputthole/Sbox-Claude/issues/1)** — UTF-8 BOM in response files causing silent JSON.parse failures. Fixed in **v1.0.0** with `new UTF8Encoding(false)` on the C# write side and `.replace(/^﻿/, "")` on the Node read side.
- **GitHub issue [#5](https://github.com/LouSputthole/Sbox-Claude/issues/5)** — Tool invocations timing out after a compile error. Fixed in **v1.2.0** when per-handler registration was made fault-tolerant and `OnFrame` was wrapped in try/catch with deduplicated logging.

### Compatibility

Drop-in upgrade from 1.2.0. No breaking changes to working tools. Tools that were already broken (`get_compile_errors`, etc.) are no longer listed — calls to them now fail at the MCP server with "tool not found" instead of round-tripping to "Unknown command".

### Acknowledgments

- **[@FurkanZhlp](https://github.com/FurkanZhlp)** — diagnosed and patched the editor bootstrap crash. Without this fix the addon doesn't load on current s&box builds at all.
- **[@Jmcasavant](https://github.com/Jmcasavant)** — three detailed bug reports (#1, #2, #3, #4) with stack traces, reproductions, and suggested fixes. Top-tier issue quality, made the fixes nearly mechanical.
- **[@dvd900](https://github.com/dvd900)** — timeout report (#5) that helped validate the v1.2.0 per-handler resilience work.

---

## [1.2.0] — 2026-05-15

**Stability release. Same 100 tools, far more resilient. Fixes three reported bugs.**

### Fixed

- **`install.ps1` / `install.sh` installed to the wrong folder.** The old installers copied the addon into `<sbox>/addons/sbox-bridge-addon/`. That folder is built-in only and silently refuses to compile custom C#, so first-time installs appeared to "do nothing" and users had to install twice (eventually by hand) before the bridge worked. Reported as "I have to install this twice." Installer now targets the project's `Libraries/claudebridge/` folder where libraries actually compile.
- **`Error calling event 'tool.frame' on 'BridgePoller'` spamming every frame.** If `ClaudeBridge`'s static initializer threw (e.g. handler constructor failed on a newer SDK), every subsequent frame re-threw the wrapped `TypeInitializationException` at ~60×/sec, filling the console with the same message. `BridgePoller.OnFrame` now wraps the call in try/catch with message deduplication — the real underlying exception is logged **once per unique error**, not 60×/sec.
- **Scene corruption from tool calls during play mode.** Mutating the scene while `Game.IsPlaying` was true could desync the serializer and corrupt `.scene` files when saved. Reported as "Claude made tons of errors when trying to make a box and broke my project save." `ProcessRequest` now refuses scene-mutating commands during play and returns a clear error: `'create_gameobject' is not allowed while play mode is active. Stop play first (stop_play) and try again.` Safe-during-play tools (read-only, `take_screenshot`, runtime properties, `start_play` / `stop_play`) unaffected.

### Changed

- **Handler registration is now fault-tolerant.** `Register()` takes a `Func<IBridgeHandler>` factory and try/catches construction. One broken handler no longer takes the entire bridge offline — only that tool becomes unavailable, and the failure is logged with the exception type and message for diagnosis.
- **Installer flags:** `-ProjectPath` / explicit project arg (auto-detects if you have one s&box project in `Documents\s&box projects`), `-ListProjects` / `--list` (show projects then exit), `-RemoveStaleAddons` / `--remove-stale` (delete old wrong-location installs).
- **`install.ps1` removes any stale `claudebridge.editor.csproj`** before s&box gets a chance to use it. The auto-generated `.csproj` contains absolute paths to s&box DLLs on the original build machine and breaks installs on other PCs — deleting it lets s&box regenerate one with the right local paths.

### Added

- **`TROUBLESHOOTING.md`** — the 10 most common failure modes, each with diagnosis and fix. Covers wrong install location, frame-error spam, save corruption, dock-closed timeouts, stale `.csproj` paths, `take_screenshot` save location, broken `create_material`, color formatting, MeshComponent material rendering, and MCP server not registering.
- **Single canonical install path in `README.md` + `INSTALL.md`.** Removed the contradictions between Quick Start, INSTALL.md, and Manual Install sections that previously sent users to three different (often wrong) places.

### Honesty Note

The bridge is excellent at building **game systems** through conversation — player controllers, networking, UI, AI, prefabs, sound events, scripts, components, runtime logic. It is **serviceable but not exceptional** at building **maps** — Claude can drive terrain sculpting, forest placement, and cave layout, but it can't see the result, only read coordinates, so visual polish still needs your eyes on it.

### Compatibility

Drop-in upgrade from 1.1.0. No breaking changes to tool signatures or behavior outside play mode.

### Acknowledgments

Thanks to community user **britishtaxi46** for reporting the three user-facing bugs that drove this release. Stability work is reactive — we don't find these without the report.

---

## [1.1.0] — 2026-04-27

**21 new tools, 109 total. Major focus: world editing and code discovery.**

### Added — Map & World Editing

The bridge now drives map-building components that follow a `[Property] List<Feature>` + `[Button]` pattern. Works with any project structured this way; no special integration required.

- `invoke_button` — press any `[Button]` on any component (the keystone tool)
- `list_component_buttons` — discover buttons available on a component
- `add_terrain_hill` / `add_terrain_clearing` / `add_terrain_trail` — sculpt the heightmap by adding features
- `clear_terrain_features` — wipe Hills / Clearings / Trails / CavePath / all
- `raycast_terrain` — sample surface height at world XY (place props on the surface)
- `add_cave_waypoint` / `clear_cave_path` — edit cave tunnel paths
- `add_forest_poi` / `add_forest_trail` — add clearing zones and trail gaps to procedural forests
- `set_forest_seed` / `clear_forest_pois` — re-roll layouts, reset

### Added — Terrain Sculpting & Painting

- `sculpt_terrain` — direct heightmap brush with raise / lower / flatten / smooth modes
- `paint_forest_density` — paint circular biome regions with density multipliers (0 = clearing, 2 = dense)
- `place_along_path` — drop instances of any model along a curve with spacing, jitter, and scale variation

### Added — Code Discovery

Stops Claude from guessing s&box APIs by exposing `Game.TypeLibrary` reflection.

- `describe_type` — full surface of any type: properties, methods, events, attributes
- `search_types` — find types by name pattern, optionally filter to Components only
- `get_method_signature` — formal signature with all overloads, parameter types, defaults
- `find_in_project` — grep the project for a symbol to find usage examples

### Added — Component Reference

- `set_prefab_ref` — assign a prefab GameObject to a component property (the case `set_property` couldn't handle because prefab references are GameObjects, not primitives)

### Added — Standalone Terrain Builder

- `build_terrain_mesh` — build a heightmap terrain mesh from a JSON spec (hills + clearings) without needing a `MapBuilder` component in the scene

### Fixed

- **`is_playing` always returning false** after `start_play` succeeded. Now uses `EditorScene.Play` with `SetPlaying` fallback, plus a `PlayState` tracker that combines multiple signals (manual flag + `Game.IsPlaying` + active-scene divergence).
- **`MeshComponent.Mesh` NullReferenceException** in `build_terrain_mesh`. `MeshComponent.Mesh` is `null` on a freshly-added component and must be assigned `new PolygonMesh()`. Latent in the previous build; surfaced by live testing.
- **`invoke_button` reporting misleading "Button not found" errors.** The reflection helper was catching `TargetInvocationException`, logging a warning, and returning `false` — which masked the actual exception thrown by the invoked method. Now unwraps and rethrows, so callers see the real inner error (e.g. `NullReferenceException: ... at MyComponent.Build()`) directly.

### Removed

- Legacy hardcoded `build_map` inline command (~150 lines of grey-box scene generator). Superseded by the new component-driver pattern.

### Tool Count

| | Before | After |
|---|---|---|
| Defined | 89 | **109** |
| Implemented | 78 | **100** |
| Not implementable (no s&box API) | 11 | **9** |

### Compatibility

- All 78 existing tools unchanged. Drop-in upgrade.
- The new map-edit tools (`add_terrain_hill`, etc.) work on any project with components shaped like `MapBuilder` / `CaveBuilder` / `ForestGenerator` (a `[Property] List<FeatureClass>` plus `[Button]` to rebuild).
- `invoke_button`, `list_component_buttons`, `raycast_terrain`, `set_prefab_ref`, and all four discovery tools work on any project, no specific component required.

### For Game Developers

To make your own components driveable by the named map tools, follow this convention:

```csharp
public class MyHill {
    [Property] public Vector2 Position { get; set; }
    [Property] public float Radius { get; set; } = 500f;
    [Property] public float Height { get; set; } = 100f;
}

public class MyTerrain : Component {
    [Property] public List<MyHill> Hills { get; set; } = new();

    [Button("Build Terrain")]
    public void Build() {
        var go = Scene.CreateObject(true);
        var mesh = go.AddComponent<MeshComponent>();
        if ( mesh.Mesh == null ) mesh.Mesh = new PolygonMesh();  // ← required, easy to miss
        // ... read Hills, generate vertices/faces ...
    }
}
```

The bridge tools find your component via `Game.TypeLibrary`, mutate the `List<>` via reflection, and re-press the `[Button]` — no per-project bridge changes required.

---

## [1.0.0] — 2026-04-10

Initial public release.

- 78 working tools across 18 categories: project, scenes, GameObjects, components, assets, materials, audio, physics, prefabs, play mode, UI, templates, networking, publishing, status.
- File-based IPC transport via `%TEMP%/sbox-bridge-ipc/` (replaced earlier WebSocket attempt — s&box's sandboxed C# blocks `System.Net`).
- Bridge addon as project-local Library at `Libraries/claudebridge/Editor/MyEditorMenu.cs`.
- BOM-less UTF-8 fix on both sides of the IPC channel (C# `new UTF8Encoding(false)` writes, MCP server strips `﻿` reads).
- 11 tools defined-but-not-implementable due to missing s&box APIs: `pause_play`, `resume_play`, `get_console_output`, `get_compile_errors`, `clear_console`, `build_project`, `get_build_status`, `clean_build`, `export_project`, `prepare_publish`.
