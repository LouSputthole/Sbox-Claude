---
name: sbox-scaffold-game
description: Use when the user asks for a whole playable game in one go through the s&box Codex Bridge — "make me a first-person game", "scaffold a game", "give me something I can press Play on". Orchestrates the existing bridge tools plus gameplay scaffolds into a first-person starter you can enter, move through, and win or lose. Handles generate/edit/hotload/place sequencing and visual plus automated runtime verification.
---

# Scaffold a Playable s&box Game

## Codex MCP tool mapping

The plugin registers the enabled-by-default `sbox` server and the optional, disabled-by-default `sbox-lifeline` server. Codex exposes native-server wrappers such as `mcp__sbox__search_tools`, `mcp__sbox__call_tool`, and `mcp__sbox__call_tools`. Bridge names such as `get_bridge_status`, `describe_type`, and `capture_view` are arguments passed through `call_tool`; they are not assumed to be standalone host tools. If Codex exposes a slightly different normalized tool name, trust the current tool inventory.

Turn one request into a scene the user can play: a first-person controller, simple
room, visible goal, and working win/lose state. This skill currently ships one genre:
`first_person`. For another genre, offer this starter or a hand-built workflow with
`$sbox-build-feature`.

Inherit the build skill's two disciplines: verify unfamiliar SDK APIs through live
reflection, and inspect screenshots before claiming a visual result works. Open the
`$sbox-build-feature` skill's `references/gotchas.md` before building.

**Invocation (v2, native MCP):** find bridge tools with `search_tools`, call them by
plain name through `call_tool`, and batch fixed sequences with `call_tools`.

## The sequencing constraint

A newly generated custom component is not in `Game.TypeLibrary` until s&box
recompiles. The current `create_player_controller` can still create a rig immediately
because `GameObject`, `CharacterController`, and `CameraComponent` are built-in; it
returns the player GameObject but leaves the new controller unattached.

Use two phases:

1. **Phase A — generate and edit every custom script.** When generating the
   controller, also let the tool create its built-in rig and camera. Then hotload and
   prove the compile.
2. **Phase B — attach the now-loaded custom components, build the rest of the scene,
   and wire references.**

Never attach `ObjectiveManager`, `GoalTrigger`, or a generated controller before the
Phase-A compile succeeds.

## Step 0 — Confirm the bridge and protect the scene

```
get_bridge_status
is_playing
```

If the editor is playing, call `stop_play` before mutation. Inspect the scene:

```
get_scene_hierarchy   maxDepth=1
```

If it contains user work, ask whether to scaffold into it or create a fresh scene;
default to a fresh scene with `create_scene name="FirstPersonStarter"`. If the user
was vague, state that this skill builds a first-person starter and proceed unless they
choose another direction.

## Step 1 — Choose the controller path

```
list_libraries
```

- Prefer an installed first-person controller such as `facepunch.playercontroller`
  or `fish.scc`. Use `describe_type` to identify the exact component and movement
  properties. You will build its rig in Phase B.
- Otherwise generate `FpController` in Phase A. Its current template supports
  `first_person`, `third_person`, and `top_down`; this workflow requests
  `first_person` explicitly.

Record the chosen controller type as `<controllerType>` and state which path you took.

## Phase A — Generate, edit, hotload, compile

Generate the win/lose manager without placing it:

```
create_objective_system   objective="reach_goal" loseOn="fall" killZ=-1000 placeInScene=false
```

Generate the goal trigger now, before the one Phase-A hotload:

```
create_trigger_zone      name="GoalTrigger"
```

The current generator returns `{ created, path, className }`; its `action` and
`filterTag` arguments are not applied to generated code. Use the returned `path` with
`edit_script` before hotload. Supply one operation with `type="replace"`, using this
exact generated method as `find`:

```csharp
private void OnPlayerEnter( GameObject player )
{
	Log.Info( $"[GoalTrigger] {player.Name} entered trigger" );
}
```

and this method as `replacement`:

```csharp
private void OnPlayerEnter( GameObject player )
{
	ObjectiveManager.Instance?.ReachGoal();
	Log.Info( $"[GoalTrigger] {player.Name} reached the goal" );
}
```

The wrapper schema is `edit_script path=<returnedPath> operations=[...]`; a missing
exact `find` is an error, not a successful edit. Inspect the returned operation result.

If no library controller is being reused, generate the controller and built-in rig:

```
create_player_controller name="FpController" type="first_person" placeInScene=true createCamera=true spawnPosition="0,0,50"
```

This call returns `{ path, className, gameObject, note, ... }`. Record
`gameObject.id` as `<playerId>`. The rig already contains a tagged player root,
`CharacterController`, and child camera; only `FpController` remains unattached until
hotload. If `gameObject` is null, fix the missing active scene instead of inventing an
ID.

Generate optional health or pickup scripts only when the requested design needs them.
Do not add features merely to exercise generators.

After every custom script exists and `GoalTrigger` has the win call, compile once:

```
trigger_hotload
compile_status
```

If the optional lifeline is enabled, use `get_compile_errors` if the native server stalls. Fix any current
error in the generated files and repeat hotload/status. Do not start Phase B until
`ObjectiveManager`, `GoalTrigger`, and any generated controller resolve through
`describe_type`.

## Phase B — Build, attach, and wire

### B1. Build a simple room

Use known local/core geometry:

```
create_gameobject   name="Floor" position={x:0,y:0,z:0} scale={x:20,y:20,z:1}
assign_model        id=<floorId> model="models/dev/box.vmdl"
add_collider        id=<floorId> type="box"
```

Create four tall, thin wall boxes at the room edges with the same
create/assign/collider sequence. Batch those independent repetitions.

### B2. Finish the player rig

For the generated-controller path, use the player ID returned in Phase A:

```
add_component_with_properties id=<playerId> component="FpController"
```

Do not create another camera: `createCamera=true` already built the child camera.
Set `<controllerType>` to `FpController`.

For a reused library controller, build the rig now with its already-loaded type:

```
add_component_to_new_object    name="Player" component="CharacterController" position={x:0,y:0,z:50} tags=["player"]
add_component_with_properties  id=<playerId> component=<verifiedControllerType>
add_component_to_new_object    name="Camera" component="CameraComponent" parentId=<playerId>
set_transform                  id=<cameraId> local=true position={x:0,y:0,z:64}
```

Use `describe_type` before setting any library-specific follow, look, speed, or input
properties.

### B3. Create and attach the goal

Choose and record a goal position; the minimal room uses `0,900,20`:

```
create_gameobject               name="Goal" position={x:0,y:900,z:20} scale={x:2,y:2,z:2}
assign_model                    id=<goalId> model="models/dev/box.vmdl"
set_tint                        id=<goalId> color="0,1,0,1"
add_collider                    id=<goalId> type="box" isTrigger=true
add_component_with_properties  id=<goalId> component="GoalTrigger"
```

`GoalTrigger.OnStart` also calls `GetOrAddComponent<BoxCollider>()` and marks it as a
trigger, so attaching it to the visible goal reuses or guarantees the trigger collider.
The player root must retain the `player` tag used by the generated trigger filter.

### B4. Place and wire the objective manager

```
add_component_to_new_object name="ObjectiveManager" component="ObjectiveManager"
set_component_reference id=<objectiveManagerId> component="ObjectiveManager" property="Player" targetId=<playerId>
get_property id=<objectiveManagerId> component="ObjectiveManager" property="Player"
```

Require the read-back to resolve the player target. This reference drives the
lose-on-fall check.

### B5. Add only the UI the starter needs

Optionally generate a minimal HUD that says "Reach the green goal" and reads the
manager state. A generated Razor component needs the same generate → hotload → attach
sequence; do not hide a working gameplay loop behind optional UI work.

### B6. Keep input on built-in actions

The generated controller uses `Input.AnalogMove` and built-in jump/run actions. Do not
add a custom action unless the design needs it: input configuration requires a project
reload before the new action is available in play mode.

## Step 2 — Save and inspect the edit scene

```
save_scene
compile_status
get_scene_hierarchy   maxDepth=2
screenshot_from       id=<playerId>
screenshot_from       id=<goalId>
```

Require the hierarchy to show the room, tagged player with controller and camera,
goal with trigger plus `GoalTrigger`, and `ObjectiveManager`. Inspect both inline PNGs.
Fix floating/sunk geometry, missing models, bad scale, or unreadable goal contrast and
capture again. `screenshot_from` takes `id`, not `target`.

## Step 3 — Automate runtime verification

Save first, then enter play and rediscover IDs from the active play scene:

```
start_play
get_scene_hierarchy   maxDepth=2
```

Record `<runtimePlayerId>` and `<runtimeObjectiveManagerId>` from this hierarchy.
Do not assume edit-scene IDs remain valid in play mode.

### Prove controller movement when the controller exposes a drive member

The current `playtest` movement driver supports controller shapes exposing
`WishVelocity`, `AnalogMove`, `MoveInput`, or `WishMove` (and can disable
`UseInputControls` when present). Confirm the installed/reused controller shape with
`describe_type`, then run:

```text
playtest id=<runtimePlayerId> component=<controllerType> steps=[
  {"move":{"x":1,"y":0},"frames":60},
  {"assert":{"read":"Displacement","op":">","value":25,"desc":"player moved more than 25 units"}},
  {"capture":"after-move"}
]
```

Poll `playtest_status` until `finished:true`; require a `PASS` transcript and inspect
`capture_view id=<runtimePlayerId>` after the job. Movement is verified only when the
displacement assertion passes.

The generated `FpController` currently reads `Input.AnalogMove` directly but exposes
none of the writable drive members above. The bridge cannot inject analog input into
`Input.AnalogMove`, so do not mislabel a harness failure as a broken controller. For
that controller, leave movement explicitly human-unverified and ask for one WASD check
after the automated win/lose tests. This is the honest fallback until the generator or
playtest driver gains a compatible hook.

### Prove the win state

Use a second playtest job to move the player into the exact goal position recorded in
Phase B. `WorldPosition` is inherited by the controller component and is the harness's
documented robust positioning fallback:

```text
playtest id=<runtimePlayerId> component=<controllerType> steps=[
  {"set":{"component":"<controllerType>","property":"WorldPosition","to":"0,900,20"}},
  {"wait":10}
]
```

Poll `playtest_status` until finished, then require:

```
get_runtime_property id=<runtimeObjectiveManagerId> component="ObjectiveManager" property="IsWon"
```

The value must be `true`. If the goal position was themed or moved, use that recorded
position instead of `0,900,20`.

### Prove the lose state in a fresh run

Winning locks the manager against a later loss, so restart play and rediscover runtime
IDs:

```
stop_play
start_play
get_scene_hierarchy   maxDepth=2
```

Run a playtest that moves the fresh player below `KillZ` (the default uses `-1200`,
below the scaffolded `-1000` threshold):

```text
playtest id=<freshRuntimePlayerId> component=<controllerType> steps=[
  {"set":{"component":"<controllerType>","property":"WorldPosition","to":"0,0,-1200"}},
  {"wait":10}
]
```

Poll status, then require:

```
get_runtime_property id=<freshRuntimeObjectiveManagerId> component="ObjectiveManager" property="IsLost"
```

The value must be `true`. Finish with `stop_play`.

If runtime teleport succeeds but the trigger does not fire, inspect the runtime goal
and player tags/components, confirm `GoalTrigger` compiled and is enabled, and capture
the goal in play mode. If a controller does not expose a component-level
`WorldPosition` property to the harness, use `set_runtime_property` with the same
runtime player ID, controller component, property, and vector value, then read the
manager state. Ask the human to verify only a capability the current runtime tools
cannot drive; do not replace all automation with a generic "press Play and tell me."

## Step 4 — Hand off only what was proved

Tell the user what exists, how to play, and the verification state:

- **WASD** moves, mouse looks, **Space** jumps, and the green goal wins.
- Falling below the room loses.
- Report edit-scene screenshots, compile result, movement transcript, `IsWon`, and
  `IsLost` separately.
- If the generated `FpController` required the movement fallback, say exactly:
  "Win and loss were automated; WASD movement still needs one human check because the
  current controller template exposes no bridge-driveable analog member."

Never turn a partial transcript into "fully verified."

## Adapt without gold-plating

- Reuse installed libraries after live type verification.
- Keep the same structure when changing names, theme, geometry, or goal position.
- Ship the minimal playable loop before optional health, pickups, hazards, or HUD
  polish.
- When placement says a component type is missing, return to the Phase-A compile gate;
  do not keep retrying scene mutation.
- When a reference fails, inspect `get_all_properties`; use
  `set_component_reference` only for GameObject/component references and
  `set_property` for primitive/value properties.
