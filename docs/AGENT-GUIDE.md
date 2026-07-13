# Agent Guide — working the platform

How an AI agent should actually drive the s&box Claude Bridge on the native MCP server.
This is the operating manual behind the [relaunch overview](RELAUNCH.md); the full tool
inventory is **[TOOLSETS.md](TOOLSETS.md)** and the plain-English toolset tour is
**[ECOSYSTEM.md](ECOSYSTEM.md)**.

Everything here uses the **plain tool names** (`find_objects`, `take_screenshot`,
`create_prefab`). On the native server those are invoked through `call_tool` — you never
type an `mcp__sbox__` prefix except for the handful of native entry points the client
surfaces directly.

---

## 1. Discovery — find the tool, then call it

The native server does **not** hand you a flat list of 262 tools. It gives you a few entry
points and lets you search:

| Entry point | Use it to |
|---|---|
| `search_tools "<terms>"` | Find tools by meaning — `search_tools "flicker light"` → `add_flicker_light`. Your primary way in. |
| `list_toolsets` | See the 28 `bridge_*` groups and what each covers. |
| `describe_toolset "<name>"` | List the tools in one group with their descriptions. |
| `call_tool {name, arguments}` | Run one tool. |
| `call_tools [ … ]` | Run several in one round trip (e.g. write script → hotload → check compile). |

**Habit:** when you're unsure a capability exists, `search_tools` for it before writing C#
by hand. The bridge almost certainly has a scaffold or helper — the whole point is to
leverage what's there instead of reinventing it. The description *is* the contract: it tells
you what the tool returns and what to pass where next.

---

## 2. The loop

Inspect → Plan → Checkpoint → Modify → Validate → Test → Summarize. Not every task needs
all seven, but skip a step deliberately, not by accident.

### Inspect
Start read-only. `describe_project` for one-call project orientation; `describe_scene` for
one-call scene orientation (works in play mode too). Then drill in with `get_scene_hierarchy`
(honor `maxDepth` / `rootId` so you don't dump the whole tree), `find_objects` (by name /
component / tag), `get_all_properties` on a specific object.

### Plan
Decide the edits. For any unfamiliar s&box type, **reflect before you write**:
`describe_type "MeshComponent"`, `search_types "loopback"`, `get_method_signature`. s&box's
API shifts between SDK versions — reflection is ground truth, training data is not.

### Checkpoint
Before **anything risky or batch-shaped**, `checkpoint_scene` (returns an `id` — keep it).
If the edit goes wrong, `restore_checkpoint {id}` rebuilds the scene from the snapshot
(guids preserved, so internal references stay wired). This is the agent-side undo — the
engine's own per-edit undo is not reachable from an addon (see §6). The scene *file* on disk
is untouched until you `save_scene`.

### Modify
Author and wire. `create_script` / `write_file` → `trigger_hotload` (a fresh type isn't in
the TypeLibrary until a recompile) → attach with `add_component_with_properties` or
`add_component_to_new_object`. Compose the scene with the `bridge_gameobject`,
`bridge_visuals`, `bridge_world`, and scaffold toolsets.

### Validate
Catch the silent breakage before you screenshot:
- `find_broken_references` — missing models, dead GameObject/Component refs, missing prefab files.
- `scene_validate` — no camera, stray root Rigidbodies, trigger-vs-trace mismatches.
- `networking_lint` / `razor_lint` / `sandbox_lint` — footguns the compiler won't file-locate for you.
- `compile_status` (native built-in) or the lifeline's `get_compile_errors` after any hotload.

### Test
- **`playtest`** — a scripted step list run in play mode with assertions evaluated **in-frame**
  (`start_play` first; poll `playtest_status`; `playtest_abort` if it's stuck). This catches
  transient state (a jump's airborne frame) that a separate call would miss.
- **Screenshots** — `take_screenshot` (main camera / player view), `capture_view` /
  `screenshot_from` (framed object or free camera), `screenshot_orbit` (N angles in one call).
  **They return the PNG inline as an image block — look at it.** There is no file path to read
  back. Guessing about a visual outcome from code alone is the #1 source of long iteration
  loops; seeing it ends them.

### Summarize
Report what changed, what you verified, and — honestly — what still needs a human (feel,
fun, anything the playtest harness can't assert). Separate "wired and compiles" from "plays
well."

---

## 3. Read-only vs mutating semantics

Every tool is one of two kinds, and it matters:

- **`[McpTool.ReadOnly]`** (53 of them) — promises it never changes your project, scene, or
  editor state. Clients may run these **without a permission prompt**. `find_objects`,
  `describe_scene`, `get_property`, `raycast`, `list_prefabs`, all the lints, the read
  screenshots, and more. Lean on them freely — they're cheap and safe.
- **Everything else — mutating.** May change the scene, write files, move the editor camera,
  play audio, install packages. These prompt for permission and are subject to the guards
  below. Note that "read-ish" tools that still touch editor state are **not** read-only —
  `capture_view` moves a temporary camera, `focus_object` changes selection.

**Play-mode guard.** Scene-mutating tools **refuse during play mode** with a clear error
("… mutates the scene and is refused during play mode. Stop play first."). To tweak a live
runtime object without leaving play, use `set_runtime_property` (changes are discarded when
play stops); to persist, `stop_play` (or the native `play_stop`) and use `set_property` in
edit mode.

---

## 4. Dry-run discipline on batch tools

The `bridge_batch` family and a few destructive tools take **`dryRun: true`** — always use
it first on anything you can't eyeball:

- `batch_delete` is **destructive and not undoable** — dry-run reports each object's name and
  child count so you confirm the target list before committing.
- `batch_set_property` dry-run reports each object's *current* value and what would change,
  without applying.
- `batch_add_component` dry-run reports which objects already have the component.
- `batch_reparent` dry-run reports each object's current parent and destination, and guards
  against cycles.

Get the target ids from `find_objects` or `get_selected_objects`, dry-run, read the plan,
then run for real. For genuinely risky sequences, `checkpoint_scene` on top of dry-run.

---

## 5. Error semantics and chaining

**Errors are real.** A failed tool throws a readable tool error (the handler's own message) —
not a `{ error: "..." }` field inside a success payload. So trust a success: if the call
returned without throwing, it did the thing. When it throws, read the message; it's written
for you (e.g. "material path can't be loaded", "no ground was hit below", "'create_gameobject'
… refused during play mode").

**Chaining is by GUID and path.** Tools hand you the handles the next tool needs — chase the
returned fields:

- Creation tools return the new object's **GUID** (`gameObject.id`). Feed it straight into
  `set_transform`, `add_component_with_properties`, `set_tint`, `delete_gameobject`.
- `find_objects` / `get_scene_hierarchy` / `get_selected_objects` are where you *get* GUIDs to
  feed the mutating tools.
- Asset and script tools return **paths**: `create_prefab` → path → `instantiate_prefab`;
  `create_material` → path → `recompile_asset` → `assign_material`; `create_script` → className
  → `add_component_with_properties`.
- `get_bounds` → `center` / `radius` → `capture_view` / `frame_camera` to frame a shot.
- `scatter_props` / `group_objects` / `place_along_path` return a **group/folder GUID**, not the
  individual children — use it with `get_scene_hierarchy {rootId}` to enumerate, or to move/delete
  the whole batch.

Wire cross-component references with `set_component_reference` (validates, can pick a specific
component type off the target), and prefab-asset references with `set_prefab_ref`.

---

## 6. When to use the lifeline

The native server is hosted **inside the editor process and dies with it.** When s&box has
crashed, hung, or won't finish compiling, port 7269 goes silent and every native tool is dead.

That's the **lifeline** — a slim stdio server (`npx -y sbox-mcp-server@2 --lifeline`) exposing
the 7 editor-down tools that run *outside* the editor:

- `read_log` — tail / filter `sbox-dev.log` (e.g. filter `"Error |"` to dig a whitelist
  rejection out of the broken-reference cascade).
- `get_compile_errors` — surface the latest C# compile failure that explains a bad session.
- `search_docs` / `get_doc_page` / `list_doc_categories` — the official Facepunch docs.
- `run_self_test`, `get_bridge_status`.

Reach for it the moment the editor stops answering. Diagnose, fix, relaunch, then go back to
the native server. (For addon-code changes specifically, the reliable recompile loop is *sync
files → `restart_editor` → verify via `handlerCount`* — the Libraries file-watcher is
unreliable; see [BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md) #9.)

Also remember what the harness **cannot** do: there is no analog input synthesis, and no
engine auto-undo for bridge mutations. Checkpoints replace undo; a human replaces the final
feel check.

---

## 7. Worked examples

Real prompts → real tool chains. Names are exactly as they appear in [TOOLSETS.md](TOOLSETS.md).

### A. "Set a horror-night mood in this scene."

```
describe_scene                          # what's here? camera? lights?
checkpoint_scene   → keep id            # lighting edits touch many objects
apply_atmosphere   {mood: "horror"}     # one-call ambient + directional + fog + post-fx
find_objects       {component: "PointLight"}  → light GUIDs
add_flicker_light  {lightId: "<guid>", preset: "Faulty"}   # the biggest atmosphere win per call
set_fog            {type: "gradient", ...}    # deepen the haze if needed
capture_view       {id: "<a landmark>"}  → look at the inline PNG, adjust
```
If the mood reads wrong, `restore_checkpoint {id}` and try a different look
(`apply_post_fx_look "filmic-horror"`).

### B. "Make that crate drivable."

```
search_tools "vehicle"                  → create_vehicle_controller, tune_vehicle, create_seat_system
describe_type "Rigidbody"               # confirm the physics surface before generating
create_vehicle_controller {name: "CarController"}   → writes CarController.cs
trigger_hotload
compile_status                          # (or lifeline get_compile_errors) — clean?
find_objects {name: "crate"}            → crate GUID
batch_add_component {ids:["<crate>"], component:"Rigidbody"}   # + collider + CarController
tune_vehicle {preset: "arcade"}         # arcade / drift / offroad / race by property name
start_play
playtest {steps:[ {action:"Forward", frames:60},
                  {assert:{read:"Displacement", op:">", value:50, desc:"car moved"}} ]}
playtest_status                         # PASS/FAIL transcript
```
The gate proves it **compiles and drives**. Whether it *feels* right is a human playtest —
tune from the inspector while playing (honest limit, [BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md) #1).

### C. "Retint all 40 crates to a warm brown."

```
find_objects {name: "crate"}            → 40 GUIDs
batch_set_property {ids:[...], component:"ModelRenderer", property:"Tint",
                    value:"0.6,0.4,0.2,1", dryRun:true}   # read current values, confirm targets
batch_set_property {ids:[...], ..., dryRun:false}          # apply
screenshot_orbit {id:"<one crate>", angles:4}              # look at every side in one call
```
Dry-run first, every time — `batch_*` touches many objects and (for `batch_delete`) isn't
undoable.

### D. "Give me a first-person controller and prove the player moves."

```
create_player_controller {mode:"first_person", placeInScene:true}   → writes + (post-hotload) places a rig
trigger_hotload
compile_status                          # clean before you play
scene_validate                          # camera present? controller present? no stray root Rigidbody?
start_play
playtest {steps:[
   {move:{x:1,y:0}, frames:60},
   {assert:{read:"Displacement", op:">", value:50, desc:"walked forward"}},
   {jump:"0,0,400"},
   {assert:{read:"PlayerController.IsAirborne", op:"==", value:true, desc:"airborne after jump"}},
   {wait:40},
   {assert:{read:"PlayerController.IsOnGround", op:"==", value:true, desc:"landed"}} ]}
playtest_status                         # verdict + per-step transcript
take_screenshot                         # the live player view, inline — look at it
stop_play
```
Prove movement with `read:"Displacement" op:">"` (facing-independent), and catch transient
state (`IsAirborne`) in the frame right after the action — that's the whole reason the
harness asserts in-frame instead of round-tripping.

---

*See also: [FAQ.md](FAQ.md) for the "can an agent modify my project" protections in plain
terms, and [ADDING-A-TOOL.md](ADDING-A-TOOL.md) if you're extending the surface.*
