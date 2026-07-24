# Playable Game Scaffolds — Design Spec

**Date:** 2026-06-04
**Status:** DESIGN ONLY — not implemented. No code edits made.
**Feature wave:** #2 "Playable game in one ask" (the non-coder mission)
**Repo:** repository root
**Target version:** proposed v1.7.0 (additive; no breaking changes)

---

## Goal

Close the gap between what the bridge does today ("place objects, write scripts") and what a non-coder actually wants ("a game I can press Play on"). One sentence — `"make me a first-person game"` — should produce a scene you can enter play mode in and *move around, see a level, and win or lose*, not a folder of `.cs` files you still have to wire up by hand.

Two deliverables:

1. **`scaffold_game(genre)`** — one call wires player + camera + input + a starter test level + a win/lose condition into a playable starter, for a small set of genres (first-person, third-person/platformer, top-down, horror).
2. **Reusable system scaffolds** — health/damage, pickups/collectibles, simple inventory, doors/interactables, trigger volumes, spawners, score/objective, basic HUD, main menu — each a one-call building block the genre presets compose and the user can add à la carte.

This is explicitly a **composition layer over the 157 existing tools**, plus a *small* set of genuinely-new bridge tools where a real capability is missing (see "The capability gaps" below).

---

## Why

### User value

The single most common failure mode for a non-coder using the bridge today is: Claude writes `create_player_controller`, reports "done," and the user presses Play and **nothing happens** — because the generated `PlayerController.cs` was never put on a GameObject, there's no camera, no ground to stand on, no spawn point, and `Input.Pressed("jump")` references an input action that may not be bound. The bridge can *author* a player controller; it cannot, in one ask, hand back a *playable* one. Every genre starts from the same 6-step boilerplate (player body + controller + camera child + collider + spawn + a floor), and a human currently has to know all six steps and drive them tool-by-tool.

`scaffold_game` collapses that to one call and — crucially — produces something **screenshot-verifiable in edit mode** (a player capsule on a floor, framed by `screenshot_from`) and **playable when the user presses Play**. That is the difference between a demo and a toy.

### The asset-library product angle

The bridge's distribution channel is the **s&box Asset Library** (`sboxskinsgg.claudebridge`). Today it ships pure tooling. The system scaffolds described here are, in effect, a **starter-content / template library** delivered *through conversation* instead of through a downloadable sample project:

- **Genre presets become "templates"** — the equivalent of Unity's "3D Platformer micro-game" or Roblox's template games, but generated live and immediately editable, not a fixed sample you reverse-engineer.
- **System scaffolds become "modules"** — a health system, a pickup system, a door — the asset-pack equivalent of buying a "starter kit," except they drop into *your* scene wired to *your* objects.
- This is a strong reason to keep the generated scripts **clean, commented, and self-contained** (one file per system, no hidden runtime dependency on the bridge): they double as **learning material**. A non-coder who scaffolds a game and then reads `Health.cs` is being onboarded to s&box. That is a retention and word-of-mouth lever for sboxskins.gg, not just a convenience.
- It also seeds a **virtuous content loop**: the same generators can later be invoked headless to *produce* downloadable template `.scene`/`.prefab` files for the Asset Library — the scaffold tooling and the asset catalog share one code path.

YAGNI note: the asset-export-to-library path is a *future* payoff, not Phase 1 scope. Phase 1 just makes "one ask → playable" real.

---

## Approach: skill vs. tools

### The decision

**Primary home = a bundled SKILL (`sbox-scaffold-game`) that orchestrates existing tools, backed by a SMALL set of new "smart template" bridge tools.** The split, stated as a rule:

> **Scene composition and multi-step wiring → SKILL.** **Generating a correct, parameterized, self-contained C# system + dropping it onto a GameObject → new bridge tool (a "system scaffold").** **A capability the engine layer genuinely lacks → new low-level bridge tool/handler.**

Why this split and not "one mega `scaffold_game` C# handler":

1. **Composition is exactly what skills are for.** The CLAUDE-style guidance and the existing `sbox-build-feature` skill already establish that multi-step orchestration with screenshot verification lives in markdown, not in a monolithic handler. A `scaffold_game` C# handler that creates 8 GameObjects, writes 5 scripts, sets 20 properties, and wires references would be a 600-line untestable handler that re-implements logic already exposed as tools — and it couldn't adapt (e.g. "use the Shrimple controller I have installed" — `list_libraries`).
2. **The verify loop must stay in the agent.** Only the agent can `screenshot_from` → read the PNG → notice the floor is missing → fix it. A handler can't see. The skill keeps the human-in-the-loop verification discipline that the whole bridge philosophy is built on.
3. **But the per-system C# is fiddly and benefits from being a tool.** Generating a *correct* `Health.cs` with `[Sync]` for multiplayer, a damage event, death handling, and a HUD hook is error-prone to hand-write each time and identical every time. That belongs in a deterministic generator (a tool), the way `create_player_controller` already is — *but improved*. Tools also get the play-mode guard, path-traversal hardening, and identifier sanitization for free via the existing dispatch.

So: **the skill is the conductor; the new tools are better, more-complete instruments.**

### What stays a skill (no new engine capability needed)

- `scaffold_game(genre)` itself — **implemented as the `sbox-scaffold-game` skill**, NOT a single tool. It is a documented, ordered recipe that calls the system-scaffold tools + existing `create_gameobject` / `add_component_with_properties` / `add_screen_panel` / `screenshot_from` / `save_scene`. (See "Why scaffold_game is a skill, not a tool" for the one nuance.)
- Genre-specific scene layout (where the floor/walls/spawn/props go).
- The verify-and-fix loop after scaffolding.
- Choosing whether to reuse an installed controller library (`list_libraries`) vs. generate one.

### What becomes new bridge tools (real generator value, deterministic, reused every time)

The **system scaffolds** (each = one TS wrapper + one C# handler that writes a parameterized `.cs` and optionally drops it on a GO):
`create_health_system`, `create_pickup`, `create_inventory`, `create_interactable`, `create_spawner`, `create_objective_system`, plus an upgraded `create_player_controller` (or a new `create_camera_rig`).

### What becomes new low-level bridge tools (genuine capability gaps)

Only where existing tools cannot express the operation at all:
`add_component_to_new_object` (create GO + add component + set props + parent in one atomic call — a frequently-repeated 3-call sequence), `set_component_reference` (wire a component property to **another GameObject in the scene by GUID** — *today impossible*; `set_property` only does primitives, `set_prefab_ref` only does prefab assets), and `ensure_input_action` (register an InputAction in project settings so `Input.Pressed("interact")` actually resolves).

---

## The capability gaps (what's genuinely missing today)

Established by reading `MyEditorMenu.cs` + the TS tool modules:

| Gap | Evidence | Consequence for "playable in one ask" |
|---|---|---|
| **Templates are script-only** | `CreatePlayerControllerHandler` (line ~2263), `CreateTriggerZoneHandler` (~2413), `CreateGameManagerHandler` (~2365) all just `File.WriteAllText` a `.cs` and return `{created, path, className}`. None touch the scene. | A "player controller" you can't move. Nothing is placed, nothing is wired. |
| **`create_player_controller` ignores most of its params** | Handler hardcodes `MoveSpeed=200f`, `JumpForce=400f`, first-person only; the MCP schema advertises `type`/`moveSpeed`/`jumpForce`/`sprintMultiplier`. | Third-person/top-down genres can't reuse it; advertised knobs are dead. |
| **No way to wire a GameObject reference into a component** | `SetPropertyHandler` (~1536) only converts `float/double/int/bool/string`. `SetPrefabRefHandler` (~1475) sets a property to a *prefab asset's* GameObject, not a *live scene* object. | Can't set `Spawner.SpawnPoint = thatEmpty`, `Door.Hinge = thatPivot`, `Camera follows thatPlayer` — the essence of wiring a game. |
| **No InputAction registration** | Generated code calls `Input.Pressed("jump")` (line 2304). No handler writes input bindings; nothing in the addon touches input settings. | Custom verbs (`interact`, `inventory`) silently do nothing in Play; only s&box's built-in defaults work. **NEEDS API VERIFICATION** (see Risks). |
| **No "create GO + component + props" atom** | Every scaffold needs `create_gameobject` → `add_component_with_properties` → maybe `set_parent`, as 2–3 round-trips. Bridge processes one request per frame, so chains are slow and partially-failing. | Slower scaffolds, more failure surface, harder to keep atomic. |
| **No win/lose condition primitive** | `create_game_manager` writes a stub with `GameState` string and a connect log — no actual win/lose, no respawn, no restart. | "win/lose condition" in the brief has no existing building block. |

---

## Genre presets

Four presets in Phase 1. Each is a documented branch in the `sbox-scaffold-game` skill. All produce: a **playable player**, a **camera**, **input that resolves**, a **starter test level** (something to stand on + something to look at), and a **win/lose condition**, then a `save_scene` + a verification `screenshot_from`.

Shared spine (every preset does this, varying the controller/camera/level):
1. Ensure an empty active scene (or confirm with the user before mutating a non-empty one).
2. Player root GameObject at a spawn position + the genre's controller component + a `CharacterController`/collider.
3. A camera (child of player for FP/TP; fixed/overhead for top-down) wired to follow.
4. A floor (and walls for FP/horror) so you don't fall forever.
5. A spawn point empty (so respawn/objective systems have an anchor).
6. A win trigger + a lose condition (fall-out-of-world or a hazard) via the objective/health systems.
7. A minimal HUD (`add_screen_panel` + a generated HUD `.razor`).
8. `save_scene`, then `screenshot_from` the player and read it.

| Genre | Controller | Camera | Starter level | Win | Lose |
|---|---|---|---|---|---|
| **first_person** | FP controller (WASD + mouse-look + jump/sprint); reuse `facepunch.playercontroller`/`fish.scc` if `list_libraries` finds it, else generate | Camera child at eye height, no mesh visible | Floored room with 4 walls + a goal volume at the far end | Reach the goal trigger | Fall below kill-Z |
| **third_person / platformer** | TP controller (WASD relative to camera, jump) | Camera child, boom offset behind/above, look-at player | Floor + a few platforms (`create_gameobject` boxes at rising heights) + a collectible set + goal | Collect all pickups OR reach goal | Fall below kill-Z |
| **top_down** | Top-down controller (screen-relative WASD, optional click-move) | Fixed camera high above, looking straight down | Bounded arena floor + a few obstacle boxes + pickups | Collect all / reach exit | Touch a hazard volume |
| **horror** | FP controller (slower, no sprint) + flashlight (`add_light` spot, child of camera) | FP camera | Dark interior (`apply_atmosphere "horror-night"` + `set_fog`) + an objective item to find + an exit door (`create_interactable`) | Find item → open exit door | Optional: a hazard/chaser (out of Phase-1 scope; stub) |

Notes:
- Presets are **data-driven within the skill**, not hardcoded C#. Adding "racing" or "tower-defense" later = a new section in the skill markdown + reuse of the same system tools.
- Each preset ends by telling the user, in plain language, what was built and how to play it ("Press Play, move with WASD, reach the green pad to win").

### Why `scaffold_game` is a skill, not a tool

The brief lists `scaffold_game(genre)` as deliverable #1 and the system scaffolds as deliverable #2. The recommendation is that **`scaffold_game` is realized as the `sbox-scaffold-game` skill**, invoked conversationally ("make me a first-person game") or via a slash command. It is *not* a single bridge tool, because (a) it must adapt to installed libraries and existing scene state, (b) it must run the screenshot verify loop, and (c) a monolithic handler would duplicate a dozen existing tools. The skill *uses* the new system-scaffold tools as its deterministic building blocks. If a future need arises for a truly headless "scaffold without an agent" entrypoint (e.g. for asset-library template generation), that can be added later as a thin tool that the skill and the headless path both share — explicitly deferred (YAGNI).

---

## System scaffolds

Each system scaffold is a **new bridge tool** (TS wrapper + C# handler) that generates one clean, commented, self-contained `.cs` and — where it makes sense — places it on a GameObject and wires what it can. They are the reusable building blocks the genre presets compose, and the "module" half of the asset-library angle.

Design rules for all system scaffolds:
- **Self-contained**: no runtime dependency on the bridge; the generated code compiles and runs in any s&box project.
- **Commented for learning** (asset-library angle): top-of-file summary + inline notes on the s&box gotchas (`TimeSince` init, `[Property]` defaults via field initializers).
- **Multiplayer-aware but single-player-safe**: include `[Sync]` on shared state where cheap, but work fine without networking.
- **Parameterized** via the tool schema; generate a `code/` file, return `{created, path, className}` (plus `gameObject` GUID when placed).
- **Play-mode guarded** (they write files / mutate the scene → added to `_sceneMutatingCommands`).
- **Idempotent-ish**: refuse if the target file already exists (matches existing template handlers' `File.Exists` guard), suggest a new name.

| System | Tool | What it generates | Scene wiring |
|---|---|---|---|
| Health/damage | `create_health_system` | `Health` component: `MaxHealth`, `[Sync] Health`, `TakeDamage(float)`, `Heal`, `OnDeath` (event/virtual), optional host-authoritative damage; optional respawn-at-spawn | Optionally placed on a target GO |
| Pickups/collectibles | `create_pickup` | `Pickup` component (trigger-based): on player enter → effect (`score`, `heal`, `item`, `custom`) + despawn + optional sound hook; counts toward objective | Optionally placed; builds GO + SphereCollider(trigger) when `placeInScene` |
| Inventory | `create_inventory` | `Inventory` component: simple slot list, `Add/Remove/Has(itemName)`, max slots, optional `[Sync]`; pairs with `create_pickup action=item` | Placed on player when given a GUID |
| Doors/interactables | `create_interactable` | `Interactable` component: look-at + `interact` keypress (or trigger) → action (`door` open/close via rotate/slide, `toggle`, `teleport`, `custom`); registers/uses the `interact` input action | Placed; can build the pivot GO for a door |
| Trigger volumes | (reuse `create_trigger_zone`, **upgraded**) | Existing tool, extended to optionally place itself on a new GO with a BoxCollider(trigger) and accept a size | New `placeInScene`/`size` params |
| Spawners | `create_spawner` | `Spawner` component: spawn a prefab at this point / at referenced spawn points, interval or wave count, max-alive cap | Placed; spawn-point refs wired via `set_component_reference` |
| Score/objective + win/lose | `create_objective_system` | `ObjectiveManager`: objective type (`collect_all`, `reach_goal`, `survive_time`, `eliminate_all`), tracks progress, fires **win**; a **lose** path (kill-Z / out of lives / timer); restart/respawn; exposes state for the HUD | Placed as a scene singleton; **this is the missing win/lose primitive** |
| HUD | (reuse `create_razor_ui panelType=hud` + `add_screen_panel`) | Existing tools; the skill generates a HUD bound to `ObjectiveManager`/`Health` | Existing |
| Main menu | (reuse `create_razor_ui panelType=menu` + `add_screen_panel`) | Existing tools; the skill wires Play/Quit; a `MenuController` stub | Existing |

The objective/win-lose system and the upgraded controller are the two highest-value additions — they're what turn "objects in a scene" into "a game with a goal."

---

## Tool / skill-by-tool design

For each new tool: name, params, the C# API it calls, what it returns, how a result is VERIFIED, and whether it's play-mode-guarded. **Where an s&box API is not yet confirmed from the code I read, it is flagged `⚠ VERIFY` with the exact `describe_type`/`search_types` step to run at implementation time.**

### A. Skill: `sbox-scaffold-game`

- **Type:** bundled skill at `plugins/sbox-claude/skills/sbox-scaffold-game/SKILL.md`. No handler.
- **Trigger:** "make me a {genre} game", "scaffold a game", "/sbox-scaffold-game {genre}".
- **Inputs (conversational):** genre (one of the four; ask if ambiguous), optional theme/name, confirmation before mutating a non-empty scene.
- **Orchestration:** the shared spine above, branching per genre, calling system-scaffold tools + existing `create_gameobject` / `add_component_to_new_object` / `set_component_reference` / `ensure_input_action` / `add_screen_panel` / `apply_atmosphere` / `add_light` / `save_scene`.
- **Returns (to user):** plain-language summary + "how to play" + the verification screenshot.
- **Verification:** **edit-mode** — after building, `screenshot_from` the player GO and the goal, read both PNGs, confirm player-on-floor and a visible goal; structural read-back via `get_scene_hierarchy maxDepth=2` to confirm the expected GOs/components exist. (Play-mode verification — actually entering Play and confirming movement — is **being researched separately**; design assumes edit-mode for now, and the skill notes "press Play to confirm movement" as the human step.)
- **Play-mode guard:** N/A (skill); the tools it calls are individually guarded.
- **Reuse-first:** calls `list_libraries`; if `facepunch.playercontroller` or `fish.scc` is present, wire it via `add_component_with_properties` instead of generating a controller.

### B. New low-level tool: `set_component_reference`  ← **highest-value gap-filler**

- **Why:** wiring a component property to *another live scene GameObject* is impossible today. This unlocks spawners→spawn points, doors→pivots, cameras→follow targets, objective→player.
- **Params:** `id` (GUID of GO holding the component), `component` (type name), `property` (property name), `targetId` (GUID of the GameObject to reference) **or** `targetComponentId` + `targetComponent` (to reference a *component* on that GO, if the property type is a Component subtype). `clear:boolean` (set null).
- **C# API:** resolve `go` via `scene.Directory.FindByGuid`; find component via `go.Components.GetAll()`; `Game.TypeLibrary.GetType(component).Properties.FirstOrDefault(p => p.Name == property)`; resolve target via `scene.Directory.FindByGuid(targetId)`; if `propDesc.PropertyType` is `GameObject` → `SetValue(component, targetGo)`; if it's a Component subtype → `SetValue(component, targetGo.GetComponent(thatType))`. Mirrors `SetPrefabRefHandler` but with a live-scene target instead of `SceneUtility.GetPrefabScene`.
- **Returns:** `{ set:true, id, component, property, targetId }` or `{ error }`.
- **Verification:** structural read-back — `get_property`/`get_all_properties` on the component, confirm the reference resolved (non-null, expected name). (`get_property` currently stringifies; ⚠ VERIFY it renders a GameObject ref usefully — if not, return the resolved target name in this tool's own result, which is sufficient.)
- **Play-mode guard:** YES (mutating → add to `_sceneMutatingCommands`).
- **⚠ VERIFY:** that `TypeDescription.Property.SetValue` accepts a live scene `GameObject` for a `[Property] GameObject` field (very likely — it's how the inspector does it, and `SetPrefabRefHandler` already sets a GameObject via `SetValue`). Step: `describe_type "GameObject"` is not needed; instead test against a known `[Property] GameObject Target` (e.g. on the generated NPC controller) at implementation time.

### C. New low-level tool: `add_component_to_new_object`

- **Why:** collapses the create-GO → add-component → set-props → parent sequence (2–3 frame-round-trips today) into one atomic handler. Reduces partial-failure surface and is the workhorse the skill calls dozens of times.
- **Params:** `name`, `component` (type name), `properties` (record, same convention as `add_component_with_properties`), `position`/`rotation`/`scale` (optional), `parentId` (optional), `tags` (optional string[]).
- **C# API:** combine `CreateGameObjectHandler` + `AddComponentWithPropertiesHandler` logic: `scene.CreateObject(true)`; set transform/parent/tags; `go.Components.Create(typeDesc)`; apply props via the existing switch. (Refactor opportunity: extract the shared property-coercion into a helper; not required.)
- **Returns:** `{ created:true, gameObject: SerializeGo(go), component }`.
- **Verification:** `SerializeGo` already lists components + transform; the skill reads it back. Optional `screenshot_from id`.
- **Play-mode guard:** YES.

### D. New low-level tool: `ensure_input_action`

- **Why:** generated gameplay verbs (`interact`, `inventory`, `flashlight`) must resolve at runtime. Without this, `Input.Pressed("interact")` is dead.
- **Params:** `name` (action name, e.g. "interact"), `keyboardKey` (e.g. "e"), `groupName` (optional), `gamepadButton` (optional).
- **C# API:** **⚠ VERIFY — this is the least-certain new tool.** s&box stores input actions in the project's input settings (historically the `.sbproj` `InputSettings`/`Actions` block, or a project `GameResource`). Implementation step at build time:
  - `search_types pattern="Input*"` and `describe_type "InputAction"` / `describe_type "InputSettings"` to find the editor-writable surface.
  - Check whether `Project.Current.Config` exposes input actions (the addon already reads `Project.Current.Config.Title/Org/Ident/Type`).
  - **Fallback if no clean API:** read/modify the `.sbproj` JSON directly via the existing path-resolved file I/O (the addon already locates `*.sbproj` — `GetProjectInfoHandler` ~line 2829), inserting the action if absent. This fallback is reliable because `.sbproj` is plain JSON on disk.
- **Returns:** `{ ensured:true, name, key, created:bool }` (created=false if it already existed).
- **Verification:** read the input settings back (or re-read the `.sbproj`); confirm the action is present. Full runtime confirmation needs play-mode (researched separately) — note it as a human step.
- **Play-mode guard:** YES (writes project settings).
- **Scope note:** if API verification shows this is fragile, **descope to Phase 2** and have Phase-1 generated code use only s&box's built-in default actions (`jump`, `attack1`, `attack2`, `use`/`reload` if present — ⚠ VERIFY the default set via a fresh project's `.sbproj`). Interactables would then bind to a confirmed default (e.g. `use`) in Phase 1.

### E. New system tool: `create_objective_system`  ← **the win/lose primitive**

- **Params:** `name` (default `ObjectiveManager`), `directory`, `objective` (`collect_all` | `reach_goal` | `survive_time` | `eliminate_all`), `targetCount` (for collect/eliminate), `timeLimit` (for survive), `loseOn` (`fall` | `timer` | `lives` | `none`), `killZ` (default −1000), `lives` (default 1), `placeInScene` (bool, default true), `restartOnEnd` (bool).
- **C# API:** writes a `Component` singleton (`public static ObjectiveManager Instance`), `[Sync]`-able progress, `RegisterPickup()`/`RegisterKill()`/`ReachGoal()` hooks the pickup/spawner/trigger systems call, `OnUpdate` checks lose conditions (player `WorldPosition.z < KillZ`, `TimeSince`/timer, lives), fires `OnWin`/`OnLose` (events + `Log.Info` + optional `Scene.LoadFromFile`/respawn). When `placeInScene`, also `scene.CreateObject` + `Components.Create` and return the GUID.
- **Returns:** `{ created:true, path, className, gameObject? }`.
- **Verification:** structural — `get_scene_hierarchy` shows the manager GO; `get_compile_errors` after `trigger_hotload` confirms it compiles. Behavioral win/lose needs play-mode (researched separately).
- **Play-mode guard:** YES.

### F. New system tool: `create_health_system`

- **Params:** `name` (default `Health`), `directory`, `maxHealth` (default 100), `regen` (bool), `respawn` (bool), `hostAuthoritative` (bool, default true), `targetId` (optional GUID to place it on).
- **C# API:** writes a `Component`: `[Property] MaxHealth`, `[Sync] public float Health`, `TakeDamage(float, GameObject attacker=null)`, `Heal(float)`, death → disable/ragdoll hook + optional respawn-at-spawn + notify `ObjectiveManager`. If `hostAuthoritative`, guard mutation with an `IsProxy`/host check (⚠ VERIFY current networking accessor — `Networking.IsHost` "may throw if networking not active" per CLAUDE.md → guard with try/catch or `Networking.IsActive`). If `targetId`, place via the same path as `add_component_with_properties`.
- **Returns:** `{ created:true, path, className, targetId? }`.
- **Verification:** compile check post-hotload; structural read-back if placed.
- **Play-mode guard:** YES.

### G. New system tool: `create_pickup`

- **Params:** `name` (default `Pickup`), `directory`, `action` (`score` | `heal` | `item` | `custom`, default `score`), `amount` (default 1), `filterTag` (default `player`), `placeInScene` (bool), `position` (if placing), `model` (optional model path for a visible pickup), `sound` (optional).
- **C# API:** writes a `Component, Component.ITriggerListener` (mirrors `CreateTriggerZoneHandler`'s trigger pattern): `OnTriggerEnter` → tag check → apply effect (`ObjectiveManager.Instance?.RegisterPickup()`, `Health.Heal`, `Inventory.Add`) → optional sound → `GameObject.Destroy()`. When `placeInScene`: `scene.CreateObject`, add `ModelRenderer` (if model) + `SphereCollider{IsTrigger=true}` + the Pickup component, set position.
- **Returns:** `{ created:true, path, className, gameObject? }`.
- **Verification:** if placed, `screenshot_from id` (a pickup is visible) + read PNG; structural otherwise.
- **Play-mode guard:** YES.

### H. New system tool: `create_interactable`

- **Params:** `name` (default `Interactable`), `directory`, `action` (`door` | `toggle` | `teleport` | `custom`), `inputAction` (default `use` or `interact` per gap-D outcome), `range` (default 120), `placeInScene` (bool), `position`, plus action-specific (`openAngle`/`slideOffset` for door, `destinationId` for teleport).
- **C# API:** writes a `Component`: look-at-or-trigger detection + `Input.Pressed(inputAction)` → perform action (rotate/slide a referenced pivot for `door`, toggle enabled, teleport the player to `destinationId`). Uses `set_component_reference` (the skill wires the pivot/destination after creation). Calls `ensure_input_action` for the verb (or the skill does, before).
- **Returns:** `{ created:true, path, className, gameObject? }`.
- **Verification:** structural + compile; door motion is play-mode (researched separately) — for edit-mode, `screenshot_from` confirms the door GO exists in place.
- **Play-mode guard:** YES.

### I. New system tool: `create_spawner`

- **Params:** `name` (default `Spawner`), `directory`, `prefabPath` (what to spawn), `mode` (`interval` | `wave` | `once`), `interval` (sec), `count`/`maxAlive`, `spawnPointIds` (optional GUIDs), `placeInScene`, `position`.
- **C# API:** writes a `Component` that instantiates `prefabPath` (via `GameObject.Clone`/prefab instantiate — ⚠ VERIFY the runtime prefab-spawn call: `SceneUtility.GetPrefabScene(...).Clone()` vs a `PrefabFile` API; `InstantiatePrefabHandler` already does editor-time instantiation — reuse that pattern's resolved API) at this point or at referenced spawn points, on a timer/wave, capping alive count. Spawn points wired via `set_component_reference` by the skill.
- **Returns:** `{ created:true, path, className, gameObject? }`.
- **Verification:** structural + compile; spawning is play-mode.
- **Play-mode guard:** YES.

### J. New system tool: `create_inventory`

- **Params:** `name` (default `Inventory`), `directory`, `maxSlots` (default 8), `networked` (bool), `targetId` (place on player).
- **C# API:** writes a `Component`: a slot list, `Add/Remove/Has/Count`, optional `[Sync]` list, simple capacity check. Pairs with `create_pickup action=item`.
- **Returns:** `{ created:true, path, className, targetId? }`.
- **Verification:** compile check; structural if placed.
- **Play-mode guard:** YES.

### K. Upgrade existing: `create_player_controller` (+ optional `create_camera_rig`)

- **Why:** today it ignores `type`/`moveSpeed`/`jumpForce`/`sprintMultiplier` and is FP-only. Genre presets need TP and top-down.
- **Param changes (additive/back-compat):** honor existing `type` (`first_person` | `third_person`), add `top_down`; honor `moveSpeed`/`jumpForce`/`sprintMultiplier` (currently dead); add `placeInScene` (bool) + `createCamera` (bool) + `spawnPosition`. When `placeInScene`, create the player GO + `CharacterController` + (FP/TP) a child Camera, set the controller's tunables, and wire the camera. (⚠ Keep the default no-placement behavior identical so existing callers don't break — placement is opt-in.)
- **C# API:** branch the generated controller code by `type` (camera-relative move for TP, screen-relative for top-down, eye-height look for FP). When placing: `scene.CreateObject`, `AddComponent<CharacterController>`, add the generated component (after hotload it exists in `TypeLibrary`; if generated same-call, the skill hotloads first then `add_component_with_properties` — sequencing note for the skill), child Camera GO with `CameraComponent` (⚠ VERIFY the camera component type name via `search_types pattern="*Camera*"` — likely `CameraComponent`).
- **Returns:** `{ created:true, path, className, gameObject? }`.
- **Verification:** `screenshot_from` the player GO; structural read-back of the camera child.
- **Play-mode guard:** YES (already in `_sceneMutatingCommands`).
- **Decision:** prefer **upgrading** `create_player_controller` over a brand-new tool to avoid surface bloat; optionally split camera into `create_camera_rig` if the controller handler gets too large. The skill always *can* substitute an installed controller library (`list_libraries`).

### Registration / plumbing (for every new tool)

- TS wrapper in a new `sbox-mcp-server/src/tools/scaffolds.ts` (group: system scaffolds + the three low-level gap tools), registered in `src/index.ts` like the other `register*Tools`.
- C# handler class `XHandler : IBridgeHandler` in `MyEditorMenu.cs`, registered via `Register("name", () => new XHandler())`.
- Add every scene/file-mutating new command to `_sceneMutatingCommands`.
- Reuse existing helpers: `TryResolveProjectPath`, `SanitizeIdentifier`, `ParseVector3`, `ParseRotation`, `SerializeGo`, the property-coercion switch.
- Bump `BridgeVersion`, the MCP `package.json` version, the plugin's pinned `@version`, and the tool/handler counts in `CHANGELOG.md`/`README.md`/`CLAUDE.md` (version-skew is a documented footgun).

---

## Risks & unknowns (+ API verifications needed)

**Top 3 (highest impact):**

1. **InputAction registration (gap D / tool `ensure_input_action`) is the least-certain capability.** If there's no clean editor API to add an input action, custom verbs won't work and the `.sbproj`-JSON fallback must be proven. **Verify:** `search_types pattern="Input*"`, `describe_type "InputSettings"`, `describe_type "InputAction"`, and inspect a fresh project's `.sbproj` for the actions block + the built-in default action names. **Mitigation:** Phase 1 binds only to confirmed built-in actions (e.g. `jump`, `use`); `ensure_input_action` ships in Phase 2 once verified.

2. **Same-call "generate component → place it on a GO" requires a hotload in between.** A freshly generated C# component type is not in `Game.TypeLibrary` until s&box recompiles, so `add_component_with_properties`/`Components.Create(typeDesc)` will fail (`Component type not found`) if called in the same breath. **Mitigation:** the skill must `trigger_hotload` + confirm compile (`get_compile_errors`) *between* generating a controller/system script and placing it — and the genre presets must order operations accordingly. This is a sequencing constraint, not a blocker, but it's the most likely source of "scaffold half-worked." The system tools that *place* in the same call (pickup, objective) sidestep this only because their generated type still needs a hotload before `Components.Create` resolves — so **placement-in-same-call for newly-generated types is itself an open question**: it may be that "generate file" and "place component" must always be two phases. **Verify at build time** whether `Components.Create(typeDesc)` can find a type generated earlier in the same editor session without a hotload (likely not). Design assumes a two-phase flow (generate+hotload, then place+wire) and the skill encodes that.

3. **Edit-mode verification can't confirm the game is actually playable.** `screenshot_from` proves a player capsule sits on a floor; it cannot prove movement, win/lose, spawning, or door motion — all of which are runtime. Play-mode verification is being researched separately. **Mitigation:** the skill verifies *structure* (hierarchy read-back) + *compile* (`get_compile_errors`) + *static appearance* (screenshot), and explicitly hands the user the "press Play and confirm you can move / reach the goal" step. When play-mode verification lands, the skill's verify section upgrades to actually enter Play, screenshot mid-play, and confirm.

**Other unknowns / verifications:**

4. **Live-scene GameObject references via `SetValue` (tool B).** Very likely works (`SetPrefabRefHandler` already `SetValue`s a GameObject), but confirm for a `[Property] GameObject` on a *scene* object and for Component-typed properties. **Verify:** test against the NPC controller's `[Property] GameObject Target`.
5. **Camera component type name.** `search_types pattern="*Camera*"` — confirm `CameraComponent` and its fields (FOV, etc.) before generating camera rigs.
6. **Runtime prefab spawning API (tool I).** Confirm the runtime instantiate call (vs. the editor-time path `InstantiatePrefabHandler` uses). **Verify:** `describe_type "SceneUtility"` / `describe_type "PrefabFile"` / `describe_type "GameObject"` for a `Clone`/instantiate that works at play time.
7. **Networking accessors in generated code.** `Networking.IsHost` can throw if networking isn't active (per CLAUDE.md) — generated health/spawner code must guard with `Networking.IsActive`/try-catch. Confirm current accessor names with `describe_type "Networking"`.
8. **CharacterController API drift.** The existing template uses `IsOnGround`, `Punch`, `Accelerate`, `ApplyFriction`, `Move` — confirm these still exist (`describe_type "CharacterController"`) before generating TP/top-down variants on top of them.
9. **`is_playing` staleness / play-mode guard interaction.** The guard keys off `Game.IsPlaying`; `is_playing.sessionPlaying` can read stale. Not a blocker for scaffolds (they're edit-mode), but the skill should `stop_play` defensively before a big scaffold if it detects play mode.
10. **Empty/non-empty scene handling.** Scaffolding into a scene that already has content could clobber the user's work. **Mitigation:** the skill checks `get_scene_hierarchy` and asks before mutating a non-empty scene, or scaffolds into a fresh `create_scene`.
11. **Asset choices for the starter level.** Use known-good dev assets (`models/dev/box.vmdl` per CLAUDE.md examples) for floor/platforms/walls to avoid the broken-asset issues noted in memory (e.g. black trees). Confirm a reliable floor/box model via `search_assets`.

---

## Phasing

### Phase 1 — "first-person, one ask, actually playable" (ship first)

The smallest end-to-end slice that delivers the mission for **one** genre, proving the skill+tools pattern:

- **New low-level tools:** `set_component_reference` (B), `add_component_to_new_object` (C).
- **New system tools:** `create_objective_system` (E, the win/lose primitive), `create_health_system` (F), `create_pickup` (G).
- **Upgrade:** `create_player_controller` to honor params + add `placeInScene`/`createCamera` (K), FP path only.
- **Skill:** `sbox-scaffold-game` with the **first_person** preset only, full edit-mode verify loop (structural read-back + `screenshot_from` + compile check), reuse-installed-controller branch, two-phase generate→hotload→place→wire sequencing baked in.
- **Input:** bind only to confirmed built-in actions (no `ensure_input_action` yet).
- **Verification of the feature itself:** scaffold a FP game in an empty project, `screenshot_from` the player on the floor + the goal, read both, confirm structure via `get_scene_hierarchy`, confirm clean compile. Document the "press Play to move" human step.

Rationale: first-person is the highest-demand genre, exercises every new tool, and lets us validate the trickiest constraint (generate→hotload→place sequencing) before fanning out to more genres.

### Phase 2 — breadth + the input gap

- `ensure_input_action` (D) once API-verified; rebind interactables to custom verbs.
- `create_interactable` (H), `create_spawner` (I), `create_inventory` (J).
- Skill presets: **third_person/platformer** and **top_down**; `create_camera_rig` if the controller handler needs splitting.
- Upgrade `create_trigger_zone` with `placeInScene`/`size`.

### Phase 3 — horror + polish + asset-library payoff

- **horror** preset (atmosphere + flashlight + find-item + exit door; optional chaser stub).
- Main-menu wiring (`MenuController` + Play/Quit) as a first-class skill step.
- **Play-mode verification integration** once that research lands: the skill enters Play, screenshots mid-play, confirms movement/win/lose.
- **Asset-library template export:** a headless path that runs the generators to emit downloadable starter `.scene`/`.prefab` templates for `sboxskinsgg.claudebridge` — the content-loop payoff.

---

## Open questions for the user

1. **Genre priority:** is **first-person** the right Phase-1 genre, or would third-person/platformer demo better for your audience? (Phase 1 ships exactly one.)
2. **Reuse vs. generate controllers:** when an installed controller library (`facepunch.playercontroller`, Shrimple `fish.scc`) is detected, should the scaffold **prefer it** (less code, matches community norms) or **always generate** a self-contained controller (better as learning material / asset-library content)? Recommendation: prefer-installed, fall back to generate.
3. **Single-player vs. multiplayer default:** should scaffolds be **single-player by default** (simpler, no lobby) with multiplayer opt-in, or networked-by-default given the bridge's strong networking surface? Recommendation: single-player default, `[Sync]` present but dormant.
4. **Scene safety:** when the active scene is non-empty, should the skill **always create a new scene**, or **ask** each time? Recommendation: ask, default to new scene.
5. **Naming/branding of the deliverable:** skill name `sbox-scaffold-game` + slash command, or fold into the existing `sbox-build-feature` skill as a "scaffold a new game" mode? Recommendation: separate skill (distinct trigger, distinct verify loop).
6. **`ensure_input_action` appetite:** if API verification shows input-action registration is fragile/`.sbproj`-surgery-only, are you OK shipping Phase 1 bound to built-in actions and deferring custom verbs to Phase 2? Recommendation: yes.
7. **Asset-library template export (Phase 3):** is "generate downloadable template scenes for the Asset Library" a goal worth designing the generators around now (shared code path), or purely future? Recommendation: keep generators clean enough to reuse, but don't build the export until Phases 1–2 prove value.
