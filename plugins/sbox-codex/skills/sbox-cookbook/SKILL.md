---
name: sbox-cookbook
description: Use when building a whole s&box game or a specific game SYSTEM and you want a proven, code-grounded recipe instead of guessing. Genre playbooks (tycoon/idle, shopkeeper/management, deathmatch/arena, platformer/obstacle course, survival/horror, card-battler, gacha/dungeon-crawler, social-hub, document/inspection sim, puzzle, sandbox/voxel, vehicles, roleplay) and per-system how-tos (inventory, economy/currency, shop/vendor/trading, save/persistence, progression/upgrades/prestige, gacha/loot tables, leaderboards/services, idle/offline earnings, building/placement, crafting, dialogue, round/match flow, spawning/waves, anti-cheat) plus engine references (networking & authority, architecture, player controller, Razor UI, weapons/combat, input, physics/traces, component lifecycle, worldgen/rendering, performance/threading, data assets). Triggers on "how do I build a <genre> game", "inventory system", "save system", "economy / currency", "shop", "upgrade / prestige tree", "leaderboard", "round system", "spawn waves", "gacha / loot box", "building placement", "host-authoritative", and similar. This is a ROUTER — find the system/genre, open the reference; do not answer from SKILL.md alone.
---

# s&box Cookbook — Master Router

A library of **code-grounded recipes** mined from **51 current (2026), hand-built open-source s&box games** (2026-06 refresh re-mined all 51 — added 6 genres + 2 systems below; cross-reference them with `references/CORPUS-INDEX.md`) plus the modern engine repos (Facepunch `sandbox`, `sbox-scenestaging`, `sandbox-plus-plus`, `grubs`, …). Every recipe is **modern GameObject/Component/Scene API** and cites real source you can open. (The original 27 deep-mined games supply the bulk of the genre/system spine; a second batch of 20 — basebuilder, scoops, sneguborka, duck_pond, phenodex, lumberyard, stone_by_stone, newrp, sweeper_otso, wjse, farm_land, garden, suspectra, sandmoney_, minigolf, fill_the_void, chess_otb, nice_putt_idiot, s_sino, sandbox-reforged — adds the host-migration/day-night/buoyancy/genetics/module-loader/sidewalk-NPC variations cited in the references below.)

**This file is an index, not the answer.** Find your system or genre below, then **open that reference** so you load only what you need.

## The four skills, and when each fires
- **`sbox-cookbook`** (this) — *recipes*: "how do I build a **tycoon** / an **inventory** / a **save system**?" → routes to a grounded how-to.
- **`sbox-api`** — *the brain*: how to write correct s&box C# (the Unity→s&box table, the Ten Rules, per-area API references). Open it for exact API surface.
- **`sbox-build-feature`** — *hands + eyes*: the screenshot-driven bridge workflow + runtime gotchas. Open it to build it, run it, and SEE it.
- **`sbox-scaffold-game`** — turns one ask into a playable starter scene.

**Authority of truth:** the recipes teach the pattern; the **live editor reflection** (`describe_type` / `search_types` / `get_method_signature`) is the authoritative signature check for *your* installed SDK. If a recipe disagrees with live reflection, reflection wins.

---

## 🎮 Building a whole game? → Genre recipes
Each `references/genres/<x>.md` gives the **system stack to compose**, a **build order**, and **how the real games do it** (with code refs).

| You're building… | Open | Mined from |
|---|---|---|
| Tycoon / idle / incremental (chop, mine, dig) | `references/genres/tycoon-idle.md` | chop_the_forest, s_miner, digging_simulator |
| Shopkeeper / management (kiosk, store, restaurant) | `references/genres/shopkeeper.md` | doner_kiosk, shop_manager, everything_must_go |
| Document / inspection sim (Papers-Please-like) | `references/genres/document-sim.md` | terryspapers |
| Roleplay (jobs, doors, money, factions) | `references/genres/roleplay.md` | darkrpog |
| Sandbox / voxel (spawn, build, tools) | `references/genres/sandbox-voxel.md` | sandboxwars, ss1 |
| Social hub / lobby game | `references/genres/social-hub.md` | elevator |
| Platformer / obstacle course (your DeathMaze) | `references/genres/platformer-obstacle.md` | jumper, terrys_crash_course, xtrem_road |
| Deathmatch / arena combat | `references/genres/deathmatch-arena.md` | sdoomresurrection, versus |
| Card / draft battler | `references/genres/card-battler.md` | battledraft |
| Survival / horror | `references/genres/survival-horror.md` | natural_disaster_survival, backrooms, sdiver |
| Gacha / dungeon crawler | `references/genres/gacha-crawler.md` | gacha_crawler, multis_cases |
| Puzzle | `references/genres/puzzle.md` | 15_puzzle_master |
| Vehicles | `references/genres/vehicles.md` | vehicle_tool_example |
| Party / microgames | `references/genres/party-microgame.md` | terry_games |
| Social deduction (hidden roles, vote/eject) | `references/genres/social-deduction.md` | suspectra, murder |
| Survivor / bullet-heaven roguelite | `references/genres/survivor-roguelite.md` | ss2 |
| Co-op kitchen / assembly-line (Overcooked) | `references/genres/coop-kitchen.md` | wjse |
| Board game / turn-based (chess, minesweeper) | `references/genres/board-game.md` | chess_otb, sweeper_otso |
| Casino / gambling hub (case-opening) | `references/genres/casino-gambling.md` | s_sino, multis_cases |
| Physics sports (golf, climbing, skater) | `references/genres/physics-sports.md` | minigolf, nice_putt_idiot, ragroll |

## 🧩 Need one system? → System how-tos
Each `references/systems/<x>.md` = what it is + the canonical modern approach + variations across games + gotchas + which games to read.

| System | Open |
|---|---|
| Inventory / backpack / hotbar | `references/systems/inventory.md` |
| Economy / currency (host-authoritative, request→apply→confirm) | `references/systems/economy-currency.md` |
| Shop / vendor / trading | `references/systems/shop-vendor.md` |
| Save / persistence (signed, versioned, `FileSystem.Data`) | `references/systems/save-persistence.md` |
| Progression / upgrades / prestige (data-driven balance tables) | `references/systems/progression-upgrades.md` |
| Gacha / loot / cases (weighted tables, recharge) | `references/systems/gacha-loot.md` |
| Genetics / breeding (heritable genome, Gaussian inheritance, mutations, best-of registry) | `references/systems/genetics-breeding.md` |
| Leaderboards / services (`Sandbox.Services` stats, HTTP) | `references/systems/leaderboards-services.md` |
| Idle / offline earnings / round-robin ticks | `references/systems/idle-offline.md` |
| Building / placement (grid snap, footprint, modular) | `references/systems/building-placement.md` |
| Crafting (recipes, refine, mill/smelt) | `references/systems/crafting.md` |
| Dialogue / quest / VN | `references/systems/dialogue.md` |
| Round / match / gamemode flow (phase machines) | `references/systems/round-match.md` |
| Spawning / waves / NPC | `references/systems/spawning-waves.md` |
| Anti-cheat / validation (clamp, sanitize, sign, provably-fair) | `references/systems/anti-cheat.md` |
| Level design & mapping (modular sets, lighting, blockout, triggers, spawns) | `references/systems/level-design.md` |
| AI director / adaptive pacing (telemetry-driven spawn cadence, composed multipliers) | `references/systems/ai-director.md` |
| Services backend / accounts (JWT auth, optimistic store + reconcile, HTTP) | `references/systems/services-backend.md` |

> **Composing across games?** `references/CORPUS-INDEX.md` cross-references which of the 51 mined games implement each system/genre, with the single best file to read — use it to pull pieces from several games into one system.

## ⚙️ Engine concern? → Engine references
(For raw API surface, also open `sbox-api`.)

| Concern | Open |
|---|---|
| Networking & authority ([Sync] vs FromHost, [Rpc.*], host/proxy, ownership) | `references/engine/networking-authority.md` |
| Architecture (`GameObjectSystem<T>`, `ISceneEvent<T>`, partial-class, registries) | `references/engine/architecture.md` |
| Component & GameObject lifecycle (+ freeze-class gotchas, FindMode, solo-gate) | `references/engine/components-lifecycle.md` |
| Player controller (identity/body/mover split, MoveMode, view-models, spectator) | `references/engine/player-controller.md` |
| Razor UI (BuildHash reactivity, HUD bootstrap, world panels) | `references/engine/ui-razor.md` |
| Weapons / combat (Can/Wants/Do, hitscan vs projectile, AoE, reloads) | `references/engine/combat-weapons.md` |
| Input & interaction (named actions, IPressable, analog, ClientInput) | `references/engine/input-interaction.md` |
| Physics, traces & custom movement (Scene.Trace, kinematic, forces, suspension) | `references/engine/physics-traces-movement.md` |
| World-gen & custom rendering (libsdf, VertexMeshBuilder, render-to-texture) | `references/engine/worldgen-rendering.md` |
| Performance & threading (frame budget, RunInThreadAsync, dynamic audio) | `references/engine/performance-threading.md` |
| Data assets (`GameResource`, prefab registries, editor tooling split) | `references/engine/data-assets.md` |

---

## The cross-cutting laws (true in every recipe)
These bite across every system — they're repeated in the references but live here too so you internalize them:

1. **Authority is the #1 bug class.** Mutating synced state on a proxy silently rolls back. Gate every mutator with `if (IsProxy) return;` (owner-authoritative) or `if (!Networking.IsHost) return;` (host-authoritative); `Assert.True(Networking.IsHost,…)` in dev makes silent desync loud.
2. **`[Rpc.Host]` is callable by any client with forged args.** NetFlags restrict who *invokes*, not security — re-validate ownership/permission/limits and rate-limit (cooldown keyed by `Rpc.CallerId`) *inside* every host body. The proven shape is **request → apply → confirm** (client optimistic → `[Rpc.Host]` re-clamp → `[Rpc.Owner]` echo authoritative).
3. **Money/health/score must be `[Sync(SyncFlags.FromHost)]`.** Plain `[Sync]` lets a client author the value — a classic exploit. Collections use `NetList<T>`/`NetDictionary<K,V>`, not `[Sync] List<>`.
4. **`Network.IsOwner` is false in solo editor playtests** (no lobby = no owner), so IsOwner-only guards silently disable whole systems. Combine with a `LocalSimulation` property: `ShouldSimulate => LocalSimulation || Network.IsOwner`.
5. **`NetworkSpawn` is required for replication** — `Clone()`/`new GameObject` is local-only. Configure before spawn, pass a `Connection` for authority, spawn on exactly one machine (guard with `!IsProxy`).
6. **Persisted state must sanitize + clamp on load** (and version-migrate). Saves outlive your balance changes; FNV/SHA signatures are anti-tamper deterrents, not crypto. Anything with real economy value needs server-side re-validation.
7. **API names drift between SDK builds**, and some training-data APIs are dead. Verify with `describe_type`/`search_types` before writing; wrap volatile calls in try/catch with a fallback.
8. **`async void` in the frame loop leaks** (continuations outlive the GameObject, swallow exceptions). Use `TimeUntil + Destroy` for timed lifetimes; cancel a CTS on teardown for loops.
9. **Static singletons are wiped on hotload.** Use `GameObjectSystem<T>` (reconstructs safely) or `LocalComponent<T> + IHotloadManaged`; null static `Instance` in `OnDestroy`.
10. **`Time.Delta` is 0 when paused** (`Scene.TimeScale=0`) — use `RealTime.SmoothDelta`/`RealTimeSince` for anything that must move while time-scaled. Prefer `TimeUntil`/`TimeSince` over hand-rolled timers.

---

*Router only. Find the system or genre, open its reference, and verify exact API live. Do not answer s&box build questions from this file alone.*
