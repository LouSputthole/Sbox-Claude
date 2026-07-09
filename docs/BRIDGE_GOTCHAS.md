# Bridge Gotchas — engine limitations & workflow lessons

These are the gotchas that are **not code-fixable**. They come from how the s&box
editor and engine behave, not from bugs the bridge can patch. No bridge release will
make them go away — they are baked into the engine, the asset pipeline, the Razor/UI
transpiler, the API whitelist, or the render/GPU path. Learn them once so you stop
losing sessions to them.

For *operational* failures the bridge **can** help with (IPC dir mismatch, stale
addon, compile errors, screenshot angle, version drift), see **`TROUBLESHOOTING.md`**.
This file is the complement: the things you have to work *around*, not *fix*.

Each entry below is **Symptom → Why → Fix/Workaround**. The fix is usually a habit, a
restart, or a different tool — not a code change.

---

## 1. The bridge cannot synthesize gameplay input — "compiles + zero exceptions" ≠ "playable"

**Symptom:** The code compiles, play mode starts, no exceptions in the log — but you
have no idea whether the gameplay loop actually *works*. You press a control via
`simulate_input` and nothing happens: the shovel never equips, the jump never fires,
the player never moves.

**Why (engine limitation, not fixable):**
- `simulate_input` calls `Sandbox.Input.SetAction(action, down)` **once**. The bridge
  runs each handler to completion inside a **single editor frame**, so the action is
  flipped for ~one frame. Any control that reads the **rising edge** —
  `Input.Pressed("x")` — frequently **misses** it: by the time the controller's
  `OnUpdate` samples input, the press+release have collapsed into the same frame and the
  edge never registers. (Confirmed live: `ShovelEquipped` stayed `false` after both a
  press *and* a 500 ms hold.)
- There is **no analog injection at all.** `Input.AnalogMove` / `Input.AnalogLook` are
  engine-driven; `Sandbox.Input` exposes no setter for them. WASD-style movement and
  mouse-look **cannot** be synthesized through `SetAction`.

The consequence: **a clean compile and an empty exception log do not mean the game is
playable.** The bridge can author and wire systems, but it cannot *play* them. Any
real gameplay loop — movement, combat, interaction, traversal, a full objective run —
**needs a human at the keyboard** to confirm it feels right and actually fires.

**Partial workaround — the play-input driver (`drive_player`, EXPERIMENTAL):** instead
of relying on `Input`, this drives the active `PlayerController` **directly** across N
frames while play mode runs. It is a *partial* answer, not a replacement for a human:
- It sets `EyeAngles` for look (absolute target or per-frame `lookDelta`), bypassing
  `Input.AnalogLook` entirely.
- It feeds analog movement by writing the controller's wish/move state
  (`AnalogMove`/`MoveInput`/`WishMove`, or a synthesized `WishVelocity` in the
  controller's facing frame) — resolved by **reflection**, so it works for the built-in
  `PlayerController` and most bridge-generated `…Controller` components.
- It **also holds a named `action` DOWN every frame** for the whole duration, which is
  what finally gives `Input.Pressed` an edge to catch (frame N `false` → frame N+1
  `true`).
- It runs **async across frames and returns immediately** — poll `drive_player_status`
  for which members it actually wrote and why it ended, then verify the effect with
  `capture_view` / `get_runtime_property`.

Limits of the workaround, all inherent:
- **Reflection-based and EXPERIMENTAL** — controller field names vary by SDK/project. If
  a controller exposes none of the known members, `drive_player` reports "no movement
  member could be written" and you must drive via held actions or inspect the controller
  with `describe_type`.
- It drives **one controller** at a time; it does not exercise UI mouse input, multiple
  inputs in precise sequence, or analog feel/timing the way a player would.
- It still can't tell you if the loop is *fun* or *correct*, only that members changed.

**v1.17.0 — the `playtest` harness builds on this.** `drive_player` drives the controller;
`playtest` wraps the same input model in a scripted step runner that **asserts the result
in-frame** — `move` → assert `Displacement` rose, `jump` → assert `IsAirborne` the next
frame, `action` → assert a component/state change — and returns a pass/fail transcript
(plus a `capture` step for screenshots). It's the same engine-limited input
(controller-specific, best-effort), so it verifies that mechanics *fire*, not that the game
*feels* right.

**Bottom line:** use `playtest` / `drive_player` to verify controls are wired and mechanics
fire, but for *feel* and *fun* on any real gameplay loop, **a human playtest** is still the
final word.

---

## 2. "Default Surface not found" thrown on every `Scene.Trace`

**Symptom:** After a long or messy session, **every** `Scene.Trace` / raycast / physics
query starts throwing `Default Surface not found` (or surface-related errors), and it
won't stop — every trace-using tool fails.

**Why:** The editor's surface/physics asset registry has gotten into a bad state for
this session. It's a runtime-state corruption inside the editor, not in your project
files — nothing you wrote is wrong.

**Fix:** **`restart_editor`.** A clean editor relaunch re-registers the default surface
and traces work again. Don't try to patch it from code — there's nothing to patch.

**v1.10.0 — now auto-detected:** `raycast` / `raycast_terrain` catch this specific failure
and return `{ recoverable: true, recovery: "restart_editor" }` with a plain-English message
instead of a raw exception, so the next step is unambiguous (restart, then retry the trace).

---

## 3. Newly-added local-library `PackageReference`s need a real restart, not `trigger_hotload`

**Symptom:** You added a `PackageReference` to a **local library** (another
`Libraries/…` project) in a `.sbproj`/`.csproj`, then called `trigger_hotload`. The code
that uses the new package **still won't compile** — the types from the referenced
library aren't found, as if the reference isn't there.

**Why:** `trigger_hotload` recompiles *changed C#* against the **already-resolved**
assembly/reference graph. A newly-added local-library reference changes that graph
itself, and the graph is only re-resolved on a full editor launch. Hotload can't pull in
a reference that wasn't part of the project when the editor started.

**Fix:** Add the reference, then **`restart_editor`** (a real restart) so s&box
re-resolves the project graph and compiles against the new package. After that, normal
`trigger_hotload` works again for ordinary code edits.

**v1.10.0 — now warned proactively:** `install_asset` returns `restartRecommended: true`
(with a note), and `trigger_hotload` includes a `packageNote`, so a newly-added package
reference no longer silently fails to resolve — the bridge tells you to restart.

---

## 4. Asset pipeline — corpus/community model paths render as the giant ERROR mesh

**Symptom:** You `assign_model` / `spawn_model` with a model path copied from the corpus,
a community game, or a docs example, and instead of the model you get the huge magenta/
checkerboard **ERROR mesh** filling the view. The path looked right; the model just
isn't there.

**Why:** That `models/…/foo.vmdl` exists in *someone else's* project or in a package, not
in **yours**. s&box resolves model paths against your project's compiled assets. If the
asset (and everything it pulls in) isn't in your project, the loader falls back to the
ERROR mesh. A model is **never** a single file — it drags a dependency chain:
materials (`.vmat`), textures (`.vtex`), sometimes physics/anim assets.

**Fix:** To use a community/corpus model, **copy the model *and its full dependency
chain* into your project's `Assets/`** — either the source set (`.vmdl` + every `.vmat`
+ every `.vtex` it references, recursively) or the compiled `_c` chain
(`.vmdl_c` + `.vmat_c` + `.vtex_c` …). Copying only the `.vmdl` gives you a model with
missing materials (often still the ERROR look). The reliable path is: install the whole
package, or copy the complete tree.

**Exception — engine built-ins do NOT need copying.** Anything that ships with s&box —
`materials/default/*`, stock shaders, `models/dev/box.vmdl`, etc. — resolves globally
and is always available. Only **project/community** assets need to be brought local.
When in doubt, a `models/dev/*` or `materials/default/*` path is safe; a
`models/<somegame>/…` path is not until you've copied it in.

---

## 5. Copying a model that SHADOWS a core asset → endless recompile-stall loop

**Symptom:** After copying a model into your `Assets/` whose path **collides with a core
engine asset** — most often anything under `models/citizen/**` — the editor drops into a
**never-ending recompile / asset-processing loop.** It churns, never settles, and the
bridge stops responding because frames never free up.

**Why:** Your copied asset now **shadows** a built-in one at the same virtual path. s&box
sees two definitions for the same asset path and gets stuck reconciling/recompiling them
— a feedback loop it can't exit on its own. This is a pipeline footgun, not a bridge bug.

**Fix / avoid:**
- **Never** copy a model into a path that shadows a core tree (`models/citizen/**`,
  `materials/dev/**`, stock shader paths, etc.). Put community models under a
  **project-namespaced** path (e.g. `models/<yourproject>/…`) so there's no collision.
- If you're already stuck in the loop: **`restart_editor`**, then **delete the shadowing
  copy** from `Assets/` before launching again. The engine asset will resolve normally
  once the duplicate is gone.

---

## 6. Razor / UI transpiler quirks

The Razor → C# transpiler and the UI runtime have a handful of behaviors that look like
your code is broken when it isn't. None are bridge-fixable.

### 6a. A `PanelComponent` renders nothing without a sibling `ScreenPanel`

**Symptom:** Your `@inherits PanelComponent` Razor UI compiles and the component is on a
GameObject, but **nothing shows on screen.**

**Why:** A `PanelComponent` only draws into a root UI surface. Without a **`ScreenPanel`**
(or `WorldPanel`) component to host it, it has nowhere to render.

**Fix:** Put a **`ScreenPanel`** component as a sibling (same GameObject, or a parent the
panel lives under). Use `add_screen_panel` (or `add_world_panel` for in-world UI). Then
the panel content appears.

### 6b. Emoji in `@code`, or `switch`-expressions in `@code`, can crash the transpiler

**Symptom:** A Razor file that "should" compile throws an opaque transpiler/parse error,
often with **no useful line** — and the offending code is perfectly valid C#.

**Why:** The Razor transpiler chokes on certain constructs in `@code` blocks:
**emoji / non-ASCII literals** and **`switch` *expressions*** (`x switch { … }`) are two
confirmed triggers.

**Fix:** Keep `@code` boring. Use **plain markup** and **`if`/`else`** instead of
switch-expressions; move emoji/symbols out of `@code` (put them in markup text or load
them as data). When a Razor file errors mysteriously, suspect the transpiler before your
logic.

### 6c. A root **type-selector** SCSS rule is silently skipped

**Symptom:** You wrote a top-level rule keyed on the **component/type name** (e.g.
`MyPanel { … }` or a bare element-type selector) in the `.razor.scss`, and it has **no
effect** — the styles just don't apply.

**Why:** The UI stylesheet engine skips a **root type-selector** rule. It's not an error;
the rule is simply ignored.

**Fix:** Use **class selectors** (`.my-panel { … }`) and put the class on the element.
Class-based rules apply normally.

### 6d. "Error opening stylesheet `*.razor.scss` (File not found)" is **harmless**

**Symptom:** The log shows `Error opening stylesheet <name>.razor.scss (File not found)`
and you go looking for a missing file.

**Why:** It's a **probe**. The UI system speculatively checks for a co-located stylesheet
for every Razor component; if you didn't author one, it logs this and moves on. Nothing
is broken.

**Fix:** **Ignore it** when you intentionally have no `.razor.scss`. Don't create an empty
file to silence it and don't treat it as the cause of a real UI bug.

---

## 7. Whitelist-blocked APIs at compile — masked by the broken-reference cascade

**Symptom:** A compile fails, but the error you see is the generic broken-reference /
`tool.frame` wrapper. The **real** error is a whitelist rejection like
`System.Array.Clone() is not whitelisted` — and it's shown **with no file path**, so you
can't tell *which* file or line tripped it.

**Why (two engine behaviors stacking):**
1. s&box runs sandboxed game code against an **API whitelist.** Plenty of ordinary BCL
   members are **not** whitelisted (e.g. `System.Array.Clone()`), and using one is a hard
   compile error — not something the bridge can permit.
2. When game code fails to compile, the editor assembly fails too, producing a
   **broken-reference cascade** (`Broken Reference: package.local.X`, `tool.frame`
   spam) that **masks** the underlying whitelist message and strips its file path.

**Fix:**
- **Read the log with a filter** to dig the real error out of the cascade:
  `read_log` with filter **`"Error |"`** (the `Error |` log prefix) surfaces the actual
  whitelist rejection lines that `get_compile_errors`' summary or the wrapper hides.
- Then swap the blocked API for a whitelisted equivalent. Known case:
  **`array.Clone()` → `array.ToArray()`.** (General rule: prefer LINQ / s&box-provided
  helpers over reflection-ish or low-level BCL calls.)
- **Whitelist update (verified live 2026-06-09):** `System.Math` and `System.MathF` now
  COMPILE in game code on the current SDK — the old "MathX only" advice is stale.
  `Array.Clone()` is still rejected (confirmed via a deliberate live compile:
  `System.Array.Clone() is not allowed when whitelist is enabled`). `GameObject.Clone()`
  is a different API and fine. The `sandbox_lint` tool reflects this: it flags `.Clone()`
  as advisory and no longer flags Math/MathF.
- Several `System.Net` types remain blocked — same whitelist mechanism.

---

## 8. `take_screenshot` 30 s timeout usually means a GPU/render stall

**Symptom:** `take_screenshot` (or `screenshot_from`) hangs and times out at ~30 s. Other
tools may also feel sluggish.

**Why:** The capture path needs the renderer to produce a frame. If the GPU/render
pipeline has stalled, the frame never completes and the call blocks until timeout. s&box's
**`ToolsStallMonitor`** firing in the log around the same time is the tell — the editor's
render/tools loop is wedged. This is a driver/GPU/engine stall, not a bridge logic bug.

**Fix:** **`restart_editor`.** A relaunch clears the stalled render state and screenshots
work again. **Your saved scene survives** the restart, so you lose nothing as long as you
saved (`save_scene`) before — make saving a habit precisely so a stall costs you only the
restart, not your work. After restarting, re-take the screenshot.

---

## 9. Libraries file-watcher is unreliable — externally-edited addon code may never recompile

Copying/editing `.cs` files under `Libraries/<addon>/Editor/` from OUTSIDE the editor
(scripts, `cp`, git checkout) sometimes triggers a recompile and sometimes does nothing —
observed on 26.07.08b: two syncs recompiled within seconds, then every later sync (cp,
append, touch) was ignored until an editor restart, including on a freshly-booted editor.
Assume external addon edits DON'T hotload.

- **Reliable loop:** sync files → `restart_editor` (works over the native server too:
  `call_tool {name:"restart_editor"}`) → wait for the heartbeat (~90-150 s) → verify.
- **Assembly fingerprint:** successful compiles log NOTHING (only failures log
  `Compile of '<addon>' Failed:`), so don't read silence as staleness — check
  `status.json`'s `handlerCount` (or a version marker) to know which assembly is live.
- Project `Code/` edits via `write_file`/`create_script` still hotload normally — this
  gotcha is specifically the `Libraries/` editor-assembly path.

## Quick reference

| Symptom | Not fixable because… | Do this |
|---|---|---|
| Controls don't fire from `simulate_input`; no analog move/look | engine: single-frame `SetAction`, no `AnalogMove`/`AnalogLook` setter | `playtest` (assert a loop in-frame) / `drive_player`; **human playtest** for feel |
| `Default Surface not found` on every trace | editor surface registry corrupted for the session | `restart_editor` |
| New local-library `PackageReference` won't compile | hotload reuses the resolved reference graph | `restart_editor` (not `trigger_hotload`) |
| Community model = giant ERROR mesh | asset + dependency chain not in your project | copy `.vmdl` **+ full `.vmat`/`.vtex` chain** (or `_c` chain) into `Assets/` |
| Endless recompile loop after copying a model | copy **shadows** a core asset path (`models/citizen/**`) | `restart_editor` + delete the shadowing copy; namespace your paths |
| `PanelComponent` shows nothing | needs a host UI surface | add a sibling `ScreenPanel` |
| Razor file errors with no clear cause | transpiler chokes on emoji / `switch`-expr in `@code` | plain markup + `if`/`else` |
| Root type-selector SCSS does nothing | engine skips root type-selectors | use **class** selectors |
| `Error opening stylesheet … (File not found)` | harmless existence probe | ignore it |
| Compile fails, no file path, generic wrapper | whitelist rejection masked by broken-reference cascade | `read_log` filter `"Error \|"`; `array.Clone()` → `.ToArray()` |
| `take_screenshot` times out at 30 s | GPU/render stall (`ToolsStallMonitor`) | `restart_editor` (saved scene survives) |
