# Bridge Tool Backlog — mined from 51 open-source s&box games (2026-06-09)

The 51-game corpus mining (`sbox-lessons/mining-v2/`) surfaced **352 candidate bridge tools**, **188 flagged "ship-worthy"** by the per-game miners. This is the ranked, de-duplicated roadmap. The single biggest signal: the same **scaffold-a-system** tools were independently requested by many games — those are the highest-confidence additions.

**Folded into v1.10.0:** `create_economy_wallet` (the most-requested gap with no existing scaffold; see below).
**Built in v1.11.0:** `create_round_phase_machine`, `create_day_night_clock` (with v1.10.0's `create_economy_wallet` these form the "game director" trio). Handler count: 173.

**Built in v1.12.0 (Wave 1 + Wave 2, all verify-gated live -- handler count now 179):**
- `create_interactable` (`Component.IPressable` surface confirmed via describe_type; generated component compile-verified)
- `create_weighted_loot_table` (cumulative-weight pick + optional pity; compile-verified)
- `sandbox_lint` (whitelist pre-compile scan; tuned against live deliberate-failure -- Math/MathF NOW whitelisted, Array.Clone() still blocked)
- `create_save_system` (`FileSystem.Data.ReadJsonOrDefault/WriteJson` confirmed + compile-verified; the #1 corpus demand, 7x)
- `razor_lint` (static Razor/SCSS transpiler footgun scan; pure MCP-server-side text scan)
- `copy_asset_with_dependencies` (`Editor.Asset.GetReferences(deep:true)` + shadow-guard against core trees; kills gotchas #4 and #5)

**Built in v1.13.0 (Wave 3), all verify-gated live -- handler count now 183:**
- `create_leaderboard_panel` (Razor PanelComponent + Sandbox.Services.Leaderboards; first scaffold generating .razor + .razor.scss; compile-verified)
- `create_inventory` (slot-based parallel-list inventory, stack-first TryAdd with rollback; compile-verified)
- `create_stat_modifier_system` (Set->Add->Mult engine + per-source removal + OnStatChanged; compile-verified)
- `create_placement_mode` (ghost->commit builder, ScreenPixelToRay mouse ray, grid snap, NetworkSpawn commit; compile-verified)

**10-tool plan COMPLETE -- 10/10 shipped (v1.12.0: 6, v1.13.0: 4).** See `docs/plans/2026-06-09-next-10-tools.md`.

**Built in v1.18.0 (2026-07-02), all verify-gated live -- handler count now 197. TIER 1 IS COMPLETE:**
- `create_round_state_machine` (5x -- the top ask; manager + abstract RoundState lifecycle base + auto-attaching state stubs, CanEnter() skip, static-event + [Rpc.Broadcast] announce w/ late-joiner reconcile; compile-verified)
- `add_interaction_station` ([Sync(FromHost)] Guid occupancy + [Rpc.Host] claim routing + grace window + ResolveUserLevel gate; compile-verified)
- `create_event_director` (parallel-list EventPrefabs+Weights -- ISceneMetadata does NOT exist on this SDK; weighted roll + dedupe + MaxActive + *TimedEvent self-destruct companion; compile-verified)
- `create_save_slots` (saveslots.json manifest + per-slot payloads -- no directory-listing API needed; versioned, optional GUID scene reconciliation; compile-verified)
- Plus `add_lipsync` (not from this corpus -- same-week support for the engine's new `Sandbox.LipSync` component, shipped upstream 2026-07-01).

**Next up:** Tier-2 by theme below, and the ENGINE-WATCH items:
- **Loopback multi-instance networking harness** -- Facepunch merged a local loopback socket for multi-instance testing (upstream master 2026-07-02, NOT in the shipping build yet; `search_types "loopback"` returned 0 on 2026-07-02). When it ships: spawn N loopback clients, drive each via `drive_player`/`playtest`, assert sync -- the real multiplayer-test harness.
- **MovieMaker / cutscene tool family** -- `Sandbox.MovieMaker` (MoviePlayer component, MovieResource, MovieRecorder) is entirely uncovered by the bridge and under active upstream development (keyframe split/join 2026-06-30). Candidates: `add_movie_player`, `play_movie`, maybe record-gameplay-to-clip.
- **Offline lipsync generation** -- the Sound Editor now bakes visemes offline; if a public API surfaces (only `Editor.VisemeEditor.Visemes` visible now), a `generate_lipsync` tool could produce dialogue-ready audio from the bridge.

**Queued:** everything else here, grouped by theme. Full raw list: `sbox-lessons/` mining output (local corpus at `D:\sbox-lessons`).

Legend: **(Nx)** = independently proposed by N games · `easy`/`medium` = miner-estimated build risk.

---

## Tier 1 — multi-game, high-confidence scaffolds (do these first)

| Tool | Games | Risk | What it scaffolds |
|---|---|---|---|
| `create_save_system` | **7x** | medium | Versioned `PersistenceManager` singleton + `ISaveDataProperty<T>` + JSON autosave + delete-on-version-mismatch. The #1 ask — every persistent game needs it. |
| `create_round_state_machine` | **5x** | medium | `RoundManager` singleton + abstract `RoundState` (Begin/Tick/Finish/OnTimeUp, `[Sync] TimeUntil`) + named state stubs, index-wrap, `CanEnter()` skip, host-event-plus-mirror-RPC. |
| `create_economy_wallet` ✅ v1.10.0 | 2x | easy | Server-authoritative clamped-int `Money` + `Add/Take/Set` spend-gate + per-SteamId JSON save + balance-changed event + HUD label. |
| `create_weighted_loot_table` | 2x | easy | Cumulative-weight picker + optional two-tier category roll from a `[Property] Dictionary<string,int>` or GameResource. |
| `create_interactable` / `create_interactor_base` | 2x | easy | `Component, IPressable` stub (Look/Hover/Blur/Press) + `InputTip.Push` prompt + `IsProxy` guard. |
| `create_leaderboard_panel` | 2x | easy | Razor leaderboard bound to a Steam stat key + `CenterOnMe` row + cached async avatar fetch. |
| `add_interaction_station` | 2x | easy | `IPressable` prop with `[Sync]` occupancy + reservation grace window + unlock-level gate, opening an overlay. |
| `create_stat_modifier_system` | 2x | medium | `PlayerStat` enum + `ModifierType{Set,Add,Mult}` engine with priority/sum/product resolution + per-source removal. |
| `create_event_director` | 2x | medium | Prefab-discovery (`ISceneMetadata`) + interval roll + weighted pick + active-set dedupe + timed self-destruct. The generalized AI-director. |
| `create_save_slots` | 2x | medium | Multi-slot Storage save manager + optional GUID scene-object reconciliation (destroy-missing / rehydrate-survivors). |

## Tier 2 — by theme (single-game but clearly reusable)

**Economy / currency (≈12 variants):** `create_currency`, `create_economy_currency`, `create_economy_ledger`, `create_currency_account` (host-guarded deposit/withdraw + transaction ring buffer), `create_currency_pickup` (networked coin w/ merge), `create_idle_income` (1s passive accumulator), `create_idle_economy` (geometric bulk-buy + offline reconciliation), `create_economy_balance` (static const formula class), `add_steam_stat_currency` (currency over `Services.Stats` Sum/LastValue).

**Save / persistence (≈12 variants):** `create_signed_save` (FNV/HMAC + clamp-on-load + per-SteamID forced-reset), `create_save_service`, `add_json_savegame` (autosave + inspector buttons), `create_save_dto` (flat DTO round-trip), `create_binary_save` (magic+version+typed), `create_meta_progression`, `add_local_player_profile`, `add_saveable_field` (`[Save]` onto a property), `create_offline_progress` (DateTime delta + clamp + simulate-tick).

**Leaderboards / stats / achievements (≈14 variants):** `create_leaderboard_service`, `add_leaderboard_stat` (batched 12s flush + IncrementLarge chunking + baseline-delta idempotency), `create_elo_rating_system`, `create_speedrun_leaderboard` (min-aggregation + friends filter + local-best overlay), `create_achievement_set` (strategy-per-achievement), `add_achievement_trigger` (data-driven zone), `wire_services_stats` (Sum vs LastValue accessor wiring), `create_stat_tracker`.

**Round / match / mode flow:** `create_round_phase_manager` (+ vote-to-skip ConCmd), `scaffold_map_vote_flow` (vote panel + tie-random + ChangeScene), `create_minigame_mode` (win-rule → Gamemode subclass + `.mode` GameResource), `create_round_timer_hud` (adaptive 60/8 Hz BuildHash), `create_team_assigner` (smallest-bucket balanced draft).

**Interaction / use:** `create_interaction_router` (Scene.Trace + Tags dispatch → Razor panel), `add_interaction_prompt` (eye-trace + pooled "Press E"), `create_interaction_interface` (`IUse` + PlayerInteractor), `create_proximity_modal` (Request→Rpc.Host→FilterInclude-confirm), `create_hold_to_confirm` (hold-to-fill bar).

**Loot / gacha:** `create_loot_table_resource` (GameResource w/ nested tables + depth cap), `create_loot_table_system` (rarity + `NormalizeExpectedValue` EV pass), `create_gacha_drop_table` (per-rarity chance + dup detection + folder-convention catalog), `create_pity_loot_roll` (per-Connection pity counter).

**Vehicles / seats / carry:** `make_drivable`/`add_seat`, `tune_vehicle` (arcade/sim/drift/offroad Pacejka presets), `create_seat_system` (SeatState + MoveMode freeze + safe-exit trace), `create_carry_system` (rigidbody-disable + Pickup/Drop/Throw RPCs + hand-IK), `create_physics_grab_tool` (physgun spring + FixedJoint + ownership takeover).

**World / render / atmosphere:** `add_water_body` + `create_water_profile` (Gerstner `.wtdef`), `create_daynight_cycle` (synced + gradient sun→moon), `add_render_target_camera` / `create_render_to_texture_screen` (CCTV/portal/mirror), `add_flicker_light` (Style presets), `create_camera_shake` (trauma/Perlin + GameResource), `create_grass_streamer` (LOD GPU-instanced chunks), `create_primitive_builder` (Build.Box/Ball — instant visible no-art).

**UI / feedback:** `override_build_hash` / `add_panel_buildhash` (auto-fold synced props), `create_worldpanel_ui` (diegetic clickable WorldPanel + WorldInput modal gate), `add_value_floater` / `create_floating_combat_text` / `add_damage_popups`, `create_combo_meter`, `create_proxy_nametag`.

**Networking primitives:** `add_sync_from_host_property`, `add_rate_limited_rpc` (per-Connection cooldown), `add_targeted_rpc` (`Rpc.FilterInclude` single-client side-effect), `create_host_rpc_action` (caller-resolve + re-validate skeleton), `add_host_migration_recovery` (proxy→authority detector + rebuild), `add_network_visible_cull` (`INetworkVisible` distance), `create_local_player_resolver` (proxy-safe Local, online+offline).

**Other systems:** `create_genetics_system` (Box-Muller gaussian inheritance + mutation), `create_needs_system` (decaying NeedDefinition → Happiness), `create_utility_ai` (scored Action components), `create_npc_schedule_brain` (Schedule/Task quartet), `create_event_bus` (typed local pub/sub), `create_dialog_coroutine_npc` (IEnumerator yield-return lines), `add_tts_voice` (`Sandbox.Speech.Synthesizer`), `create_placement_mode` (Sims-style ghost-preview + host re-check + NetworkSpawn), `add_day_night_clock`, `create_camera_shake`.

---

## Notes for whoever builds these
- Model them on the existing scaffold generators (`create_health_system`, `create_pickup`, `create_objective_system`, `create_npc_brain`) in `sbox-bridge-addon/Editor/ScaffoldHandlers.cs` — separate handler file, register in `MyEditorMenu.cs`, TS tool in `sbox-mcp-server/src/tools/`.
- **ALWAYS live-compile-verify the generated 