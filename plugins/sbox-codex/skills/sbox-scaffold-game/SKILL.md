---
name: sbox-scaffold-game
description: Use when the user asks for a whole playable game in one go through the s&box Codex Bridge — "make me a first-person game", "scaffold a game", "give me something I can press Play on". Orchestrates the existing bridge tools plus the gameplay scaffolds (objective/health/pickup/interactable/loot-table/save-system + wallet/phase-machine/day-night-clock + round-state-machine/interaction-station/event-director/save-slots + set_component_reference + add_component_to_new_object) into a first-person starter you can enter play mode in and move around, see a level, and win or lose. Handles the generate→hotload→place sequencing and the screenshot verify loop.
---

# Scaffold a Playable s&box Game

This skill turns ONE ask ("make me a first-person game") into a scene the user can
press **Play** on and actually *move around, see a level, and win or lose* — not a
folder of `.cs` files they still have to wire up by hand.

Phase 1 ships **one genre: `first_person`**. It exercises every new gameplay tool
and the trickiest constraint (generate→hotload→place sequencing). Other genres are
future waves — if the user asks for third-person / top-down / horror, say it's not
in this skill yet and offer the first-person starter or a hand-built approach via
`sbox-build-feature`.

This skill is a sibling of `sbox-build-feature` and inherits its disciplines:
**you have no eyes without a screenshot, and reflection is the source of truth, not
your training data.** Read that skill's gotcha table too.

---

## The one constraint that breaks scaffolds: two-phase sequencing

A freshly generated C# component type is **not in `Game.TypeLibrary` until s&box
recompiles**. So you cannot generate a script and place that component in the same
breath — `add_component_to_new_object` / `add_component_with_properties` /
`create_*`'s own placement will fail with "Component type not found".

**Therefore every scaffold runs in two phases:**

1. **Phase A — GENERATE all scripts**, then `trigger_hotload`, then confirm a clean
   compile with `get_compile_errors`.
2. **Phase B — BUILD + PLACE + WIRE the scene** (create objects, attach the now-loaded
   components, set references).

Do not interleave them. If `get_compile_errors` shows an error after Phase A, fix the
generated file and re-hotload before touching the scene. The system scaffolds
(`create_objective_system`, `create_pickup`, `create_health_system`) each report a
`note` telling you when they generated the file but could **not** place the component
yet — that note is your signal to hotload and place in Phase B.

---

## Step 0 — Bridge alive, scene safe

```
mcp__sbox__get_bridge_status
```
If it times out: s&box isn't running or the **Claude Bridge dock is closed** (the
dock must stay visible for the frame loop). Stop until it responds.

Defensively make sure you're not in play mode (scene-mutating tools are refused
during play):
```
mcp__sbox__is_playing        # trust the gameFlag field, not sessionPlaying
mcp__sbox__stop_play         # if it reports playing
```

**Scene safety.** Check what's there before mutating:
```
mcp__sbox__get_scene_hierarchy   maxDepth=1
```
- If the scene is **empty** (or just a default camera/light), proceed.
- If it has the user's **existing work**, ASK: scaffold into a fresh scene, or into
  this one? Default to a fresh scene to avoid clobbering anything:
  ```
  mcp__sbox__create_scene   name="FirstPersonStarter"
  ```

Confirm the genre. If the user was vague ("make me a game"), tell them Phase 1 builds
a **first-person** starter and proceed unless they want something else.

---

## Step 1 — Reuse an installed controller, or generate one

```
mcp__sbox__list_libraries
```
- If `facepunch.playercontroller` (or another community FP controller like
  `fish.scc`) is installed, **prefer it** — it matches community norms and is less
  code. You'll add its player component in Phase B via `add_component_with_properties`
  instead of generating one. Use `mcp__sbox__describe_type` to confirm the component
  type name and its move/jump property names before relying on them.
- Otherwise, **generate** a self-contained first-person controller (Phase A below).
  A generated controller doubles as learning material, which is the point.

State which path you took.

---

## Phase A — Generate all scripts, hotload, verify compile

Generate everything you'll place, BEFORE touching the scene.

1. **Objective system (the win/lose brain)** — for first-person, default to
   reach-a-goal with fall-out-of-world as the lose path:
   ```
   mcp__sbox__create_objective_system   objective="reach_goal"  loseOn="fall"  killZ=-1000  placeInScene=false
   ```
   Generate with `placeInScene=false` here — you'll place it in Phase B after the
   hotload so the singleton attaches cleanly.

2. **Player controller** (only if NOT reusing an installed one):
   ```
   mcp__sbox__create_player_controller   name="FpController"
   ```
   The current generator writes a WASD + jump controller that drives a
   `CharacterController` and reads the built-in `jump` action (see the input note
   below). It does NOT place itself — you'll attach it in Phase B.

3. *(Optional)* **Health** and **a pickup type**, if the design wants a hazard or
   collectibles. For a minimal reach-the-goal starter you can skip these:
   ```
   mcp__sbox__create_health_system   maxHealth=100
   mcp__sbox__create_pickup          action="score"   filterTag="player"
   ```

Now recompile and CHECK:
```
mcp__sbox__trigger_hotload
mcp__sbox__get_compile_errors
```
`get_compile_errors` reads `sbox-dev.log` directly — it works even if the editor is
busy. If there's a real, recent error mentioning one of YOUR generated files, fix the
file (`edit_script`) and re-hotload. **Do not proceed to Phase B until the compile is
clean** — placing a component whose type failed to compile will fail.

---

## Phase B — Build, place, and wire the scene

Now the generated types are in the TypeLibrary. Build the playable scene.

### B1. Floor (so you don't fall forever)

Use a known-good dev asset for geometry (avoid the broken cloud/tree assets noted in
`sbox-build-feature`). A big flat box works:
```
mcp__sbox__create_gameobject        name="Floor"   position={x:0,y:0,z:0}   scale={x:20,y:20,z:1}
mcp__sbox__assign_model             id=<floorId>   model="models/dev/box.vmdl"
mcp__sbox__add_collider             id=<floorId>   type="box"
```
For first-person, add 4 walls the same way (boxes at the room edges, tall and thin)
so the room reads as enclosed in the screenshot. Keep it simple.

### B2. Player + camera (atomic, with the now-loaded controller)

Create the player body with its `CharacterController` and the controller component in
one atomic call, then child a camera at eye height.

```
# Player root: capsule collider + character controller + the controller component
mcp__sbox__add_component_to_new_object   name="Player"   component="CharacterController"   position={x:0,y:0,z:50}   tags=["player"]
# (returns the player GUID; the CharacterController is now on it)
mcp__sbox__add_component_with_properties  id=<playerId>   component="FpController"
# If REUSING an installed controller, add its component here instead (verified type name).
```
Add the eye-height camera as a child:
```
mcp__sbox__add_component_to_new_object   name="Camera"   component="CameraComponent"   parentId=<playerId>
mcp__sbox__set_transform                 id=<cameraId>   local=true   position={x:0,y:0,z:64}
```
`CameraComponent` is the verified camera type (confirm fields with
`mcp__sbox__describe_type name="CameraComponent"` if you need FOV etc.).

> Sequencing note: once the deferred `create_player_controller` upgrade lands
> (`placeInScene` + `createCamera`), B2 collapses to a single `create_player_controller`
> call after the Phase-A hotload. Until then, place the body + camera manually as above.

### B3. The goal (win trigger) at the far end

Make a visible goal pad and a trigger volume on it. Generate a trigger-zone script in
Phase A if you want a custom on-enter; for the simplest path, place a pickup-style
trigger or use `create_trigger_zone` and call the objective on enter. Minimal version
using the objective + a trigger:
```
mcp__sbox__create_gameobject        name="Goal"   position={x:0,y:900,z:20}   scale={x:2,y:2,z:2}
mcp__sbox__assign_model             id=<goalId>   model="models/dev/box.vmdl"
mcp__sbox__set_tint                 id=<goalId>   color="0,1,0,1"        # green pad
mcp__sbox__add_collider             id=<goalId>   type="box"   isTrigger=true
```
Wire the goal to call the win. The cleanest Phase-1 path: generate a tiny
`create_trigger_zone` (Phase A) whose on-enter you edit to call
`ObjectiveManager.Instance?.ReachGoal()`, attach it to the goal here, OR use a
`create_pickup` placed on the goal and wire its `OnCollected` to the objective.

### B4. Objective singleton + wire the player reference

Place the objective manager (now loaded) and point it at the player so its
lose-on-fall check works:
```
mcp__sbox__add_component_to_new_object   name="ObjectiveManager"   component="ObjectiveManager"
mcp__sbox__set_component_reference       id=<objMgrId>   component="ObjectiveManager"   property="Player"   targetId=<playerId>
```
`set_component_reference` is the tool that wires a LIVE scene object into a component
property — this is how the objective knows which object to watch fall, how a camera
gets its follow target, how a spawner gets its spawn point. Verify the wire with
`mcp__sbox__get_property id=<objMgrId> component="ObjectiveManager" property="Player"`
(the tool also echoes the resolved `targetName` in its own result).

### B5. Minimal HUD (optional but nice)

```
mcp__sbox__create_razor_ui     name="GameHud"   panelType="hud"
mcp__sbox__add_screen_panel    name="HUD"
```
Bind the HUD to `ObjectiveManager.Instance` / `Health` in the generated `.razor`. Keep
it minimal for Phase 1 (e.g. "Reach the green pad").

### B6. Input — bind to built-in actions only (Phase 1)

Phase 1 does **not** register custom input actions. The generated controller uses
`Input.AnalogMove` (WASD, always works) and the built-in `jump` action. Do not author
custom verbs like `interact` yet — there is no `ensure_input_action` tool in this
phase, so a custom verb would silently do nothing in Play. If the design needs an
interact key, bind it to a confirmed built-in (e.g. `use`) and tell the user.

---

## Step 2 — Save, then VERIFY with your own eyes

```
mcp__sbox__save_scene
```

**Structural check** — confirm the expected objects/components exist:
```
mcp__sbox__get_scene_hierarchy   maxDepth=2
```
You should see: Floor (+walls), Player (CharacterController + your controller) with a
child Camera (CameraComponent), Goal (BoxCollider trigger), ObjectiveManager.

**Visual check** — aim the camera and READ THE PNG (you're multimodal):
```
mcp__sbox__screenshot_from   target=<playerId>     # player capsule sits ON the floor
mcp__sbox__screenshot_from   target=<goalId>       # the green goal is visible
```
List the newest file in `<sbox-install>/screenshots/` and `Read` it. **Look at the
images.** If the player is floating, sunk into the floor, or the goal is missing or
mis-coloured, fix the transforms/models and re-shoot. Don't declare it done from the
code looking right — the screenshot loop closes faster than the guess loop.

**Compile check** (belt and suspenders after all the wiring):
```
mcp__sbox__get_compile_errors
```

---

## Step 3 — Hand it back in plain language

Tell the user, in non-coder terms, exactly what was built and **how to play it**:

> Built a first-person starter you can play now:
> - A floored room with walls, a player you control, and a green goal pad at the far end.
> - **Press Play, move with WASD, look with the mouse, jump with Space, and reach the
>   green pad to win.** Fall off the edge and it's game over.
> - The win/lose logic lives in `ObjectiveManager`, your movement in `<controller>.cs`
>   — both are plain, commented C# you can read and tweak.

Then note the **human verification step**: edit-mode screenshots prove the scene is
laid out right, but they can't prove movement or the win actually fires — that's
runtime. Ask the user to press Play and confirm they can move and reach the goal.
(Play-mode auto-verification is being researched separately; when it lands this skill
will enter Play, screenshot mid-play, and confirm win/lose itself.)

---

## Adapt, don't hardcode

- **Library reuse:** always `list_libraries` first; prefer an installed controller,
  fall back to generating one. Confirm any reused component's type + property names
  with `describe_type` before wiring.
- **Theme/name:** if the user gave a theme, name the scene/objects accordingly and
  pick fitting dev geometry; the structure is identical.
- **Don't gold-plate:** a playable reach-the-goal room beats a half-wired epic. Ship
  the minimal playable loop, then offer to extend (pickups, health/hazard, HUD polish)
  as follow-ups using the same tools.

## When something half-works

The usual failure is the two-phase ordering. If a placement returns "Component type
not found" or a scaffold's `note` says the type isn't loaded: you skipped or raced the
hotload. Re-run `trigger_hotload` → `get_compile_errors` (confirm clean) → retry the
placement. If a reference won't set, `get_all_properties` on the component to confirm
the property name and that its type is a GameObject/Component (set_component_reference
only wires object/component references, not primitives — use `set_property` for those).
