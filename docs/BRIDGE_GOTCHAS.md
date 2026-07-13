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

### 6e. Razor panels compile into a **folder-derived namespace** — sibling `.cs` classes don't

**Symptom:** `search_types` returns a panel type **twice**, or code that references a
generated panel by its bare class name fails to resolve — while the plain `.cs` class
sitting *next to it* in the same folder resolves fine.

**Why:** The Razor transpiler namespaces a panel by its **folder path** (e.g. a panel in
`Code/UI/` becomes `Sandbox.UI.MyPanel`-style — `Sandbox.<Folder>.<Class>`), while sibling
`.cs` classes stay in the **global namespace**. Two naming schemes in one folder — so type
searches can surface the panel under both spellings, and a bare-name reference from global
code may miss it. (Verified live 2026-07-12 while wiring generated HUDs.)

**Fix:** Razor panels compile into a **folder-derived namespace**
(`Sandbox.<Folder>.<Class>`) in current builds — live-verified 2026-07-12; plain sibling
`.cs` classes stay global. Resolve generated panel types through `TypeLibrary` /
`search_types` and use the **fully-qualified** name it reports, or add **`@namespace`** at
the top of the `.razor` to pin one explicitly. When `search_types` shows the same panel
twice, they're the same type seen through both spellings — not a duplicate-class compile
problem.

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

---

## 10. `describe_type` is blind to STATIC members and constructors

**Symptom:** `describe_type "Game"` or `describe_type "Stats"` comes back essentially
**empty** — no members — even though the type is obviously loaded and code against it
compiles. Or a type's known **public fields** (not properties) never show up, e.g.
`Leaderboards+Entry`.

**Why:** `describe_type` reports **instance properties and methods**. It does not report
**static** members, **constructors**, or **public fields** — so static-API types
(`Game`, `Sandbox.Services.Stats`, `Leaderboards`) and field-based DTOs look empty or
incomplete. (Verified live 2026-07-12 across the Services surface.)

**Fix:** For a specific member, use **`get_method_signature`** — and note its parameter
is **`type`**, not `name`. For a full static surface, run reflection through
`execute_csharp` (`typeof(T).GetMembers(BindingFlags.Static | ...)`). Don't conclude an
API is missing because `describe_type` didn't list it.

**The companion blind spot — read `hasDefault` before calling a param "required"
(2026-07-13):** reflection shows you a method's parameter *list*, but a listed parameter
is not necessarily one you must pass — `get_method_signature` reports **`hasDefault`**
per parameter, and you must read it before documenting a parameter as required. Three
shipped gotchas overstated requirements exactly this way (`Stats.SetValue`'s trailing
`null, null`, `Board2.Refresh`'s CancellationToken, `SoundHandle.Stop`'s fadeTime — all
defaults, all legally callable with fewer args; see #11 and #15). And the static
blindness above compounds it: a static member the docs document
(`Stats.LocalPlayer`, `MovieRecorderOptions.Default`) is invisible here — "reflection
didn't show it" ≠ "the docs are wrong."

---

## 11. `Sandbox.Services` API shapes that reflection folklore gets wrong

**Symptom:** Generated Services code fails to compile or misbehaves: `Stats.SetValue`
"missing overload", `Stats.LocalPlayer` doesn't exist, a leaderboard has no
`SetFriendsOnly`/`SetAggregationMin`, an `Entry`'s values read as missing.

**Why (verified live 2026-07-12; "required param" claims corrected against
`get_method_signature` `hasDefault` + the official docs 2026-07-13):**
- `Stats.SetValue(name, amount, context = null, data = null)` — the trailing parameters
  have **defaults**, so **2-arg calls are legal**. (An earlier version of this gotcha
  claimed a missing 2-arg overload — that was the #10 `hasDefault` blind spot, not the SDK.)
- `PlayerStats` / `PlayerStat` are **nested types** (`Stats+PlayerStats`); read values
  with `.Get(name).Value`. The official docs (services/stats.md) document
  **`Stats.LocalPlayer.Get("stat")`** — `Stats.LocalPlayer` is a **static**, so it's
  reflection-invisible (#10), which is where our earlier "no LocalPlayer" claim came
  from. Prefer it over `GetLocalPlayerStats(ident)` after one compile-probe confirms it
  on your build.
- `Leaderboards.GetFromStat` has **both** `(statName)` and `(packageIdent, statName)`
  overloads and returns **`Board2`** — the *configurable* board (`SetFriendsOnly` /
  `SetAggregationMin` / `SetSortAscending` / `CenterOnMe` / `FilterByDay`). The plain
  `Get` path returns `Board`, which **lacks** those.
- `Board2.Refresh(CancellationToken cancellation = default)` — bare `Refresh()` is legal.
- `Leaderboards+Entry` exposes public **FIELDS** — invisible to `describe_type`'s
  property list (see #10), but real.

**Fix:** Code against the shapes above; when something Services-flavored "doesn't exist",
suspect a nested type, a field, or a static member before suspecting the SDK — and check
via `get_method_signature` (reading `hasDefault` before calling anything "required") /
`execute_csharp` reflection, not `describe_type` alone.
The shipped stats/leaderboard scaffolds (`add_leaderboard_stat`,
`create_speedrun_leaderboard`, `add_steam_stat_currency`) bake all of this in.

---

## 12. `GameResourceAttribute` is `[Obsolete]` — use `[AssetType]`, and mind the extension

**Symptom:** A custom GameResource decorated `[GameResource(...)]` compiles with an
obsolete warning (or fails under warnings-as-errors) — or your custom asset type
registers strangely / collides with a built-in.

**Why:** The SDK deprecated `GameResourceAttribute`; the current attribute is
**`[AssetType(Name = …, Extension = …, Category = …)]`**. Separately, the asset system
matches extensions loosely enough that an extension which is a **suffix of a built-in
one** confuses registration. (Verified live 2026-07-12 building `create_loot_table_resource`
— its `.loot` extension was chosen to dodge every built-in.)

**Fix:** Decorate custom resources with `[AssetType(...)]`, and pick an extension that is
**not a suffix of any built-in extension**.

---

## 13. `MovieRecorder.Start()` AUTO-advances — pumping it yourself DOUBLE-COUNTS

**Symptom:** A recorded gameplay clip runs roughly **twice as long** as the wall-clock
time you recorded (verified: 5.55 s of play → a 10.80 s clip). Or a recorder built from a
bare `new MovieRecorderOptions()` produces **zero tracks**.

**Why:** In play mode, `MovieRecorder.Start()` hooks game time and **auto-advances +
auto-captures every frame on its own**. Calling `Advance`/`Capture` manually as well means
every frame is counted twice. Separately, a bare `new MovieRecorderOptions()` captures
nothing **by design** — you must add capture sources. (An earlier version of this gotcha
called `WithCaptureAll<Component>()` "inert" — that was a **misdiagnosis**: it works when
paired with a matching `ComponentCapturer`, or start from **`MovieRecorderOptions.Default`**,
a **static** — invisible to `describe_type`, see #10 — that captures all
Renderers/Cameras/SoundPoints/particles. It's a record type, so `with` syntax works, and it
compiles in sandboxed game code. Official reference: movie-maker/recording-api.)

**Related surface, all verified live (2026-07-13):**
- **`BufferDuration` is a TRUE rolling buffer** — 8.72 s recorded → exactly a 3.00 s clip,
  re-based to 0 (the last-N-seconds killcam primitive; `create_killcam` builds on it).
- `Compiled.MovieClip` exposes `Tracks` + `Duration` but **no `TimeRange`**.
- `MoviePlayer` surface: `Play(IMovieClip)` / `CreateTargets` / `UpdateTargets` —
  `UpdateTargets` applies clip state **even in EDIT mode** (how `author_movie_clip`
  verifies without playing).
- `WithCaptureGameObject` alone captures 5 transform tracks but **NOT camera properties** —
  add `WithCaptureComponent(cam)` for `FieldOfView` etc. (verified: FOV decodes exactly).

**Fix:** In play mode, start the recorder and **monitor only** — never pump
`Advance`/`Capture` while play mode runs (manual `Advance`/`Capture` is the **edit-mode
bake** idiom, which `author_movie_clip` uses). Target with `WithCaptureGameObject`
(+ `WithCaptureComponent` for camera props) or capture the whole scene via
`WithDefaultCaptureActions()` / `MovieRecorderOptions.Default`. The bridge's
`record_gameplay_clip` / `stop_gameplay_recording` / `gameplay_recording_status` encode
exactly this.

---

## 14. `JsonSerializerOptions { WriteIndented = true }` throws in editor context

**Symptom:** Serializing to pretty-printed JSON from **editor-side** code throws
`must specify a TypeInfoResolver` — the same code that works fine in game code.

**Why:** The editor context runs System.Text.Json with reflection-based resolution
unavailable by default, so constructing custom `JsonSerializerOptions` (e.g. for
`WriteIndented`) demands an explicit `TypeInfoResolver`. (Hit live 2026-07-12 writing
`.movie` resources.)

**Fix:** Use plain `ToJsonString()` / `JsonSerializer.Serialize` **without** custom
options. Give up the pretty-printing; take the working serialization.

---

## 15. `Sandbox.LipSync` cannot consume a TTS `SoundHandle`

**Symptom:** You generate speech with `Sandbox.Speech.Synthesizer` and try to drive a
character's mouth with `LipSync` — but there's no way to hand the TTS audio to the
LipSync component.

**Why:** `LipSync` consumes a **`Sound` (a `BaseSoundComponent`)** plus a Renderer — it
has no input for a raw **`SoundHandle`**, which is what TTS returns. Related handle facts
(verified live 2026-07-12): `SoundHandle.Stop(float fadeTime = 0)` — the `fadeTime` has a
**default**, so parameterless `Stop()` *calls* are valid (an earlier version of this
gotcha said otherwise — the #10 `hasDefault` blind spot); and `SoundHandle.LipSync` is a
real accessor (`Enabled` / `Visemes` / `FrameNumber`).

**The viseme surface, verified live (2026-07-13):**
- `SoundHandle.LipSync.Visemes` is an **`IReadOnlyList<float>`** in the engine's
  **15-viseme Oculus order**: `viseme_sil, PP, FF, TH, DD, KK, CH, SS, NN, RR, AA, E, I,
  O, U` (read from `Sandbox.LipSync`'s private `VisemeNames`).
- **`Model.GetVisemeMorph(visemeName, morphIndex)`** exposes the per-model **baked
  viseme→morph matrix** (Citizen ships it — e.g. `viseme_AA` → `openjawL/R`).
  **ARG ORDER MATTERS**: swapped arguments return silent zeros, not an error.
- `Synthesizer.OnVisemeReached` is **`Action<int, TimeSpan>`** — CONFIRMED (the older
  "delegate arg types can't be confirmed" note is stale).

**Fix:** Treat TTS as **audio-only** at the `LipSync`-component level (that's why
`add_tts_voice` is designed that way). If you need mouth movement, drive morphs from the
viseme stream yourself — `handle.LipSync.Visemes` (exposed by `add_tts_voice`'s
`enableVisemeData`) multiplied through `Model.GetVisemeMorph` — which is exactly what
**`generate_lipsync_dialogue`** generates for you — or route pre-authored `.sound` assets
through a `SoundPointComponent` where `add_lipsync` works normally.

---

## 16. The SDK moves under you — e.g. CameraComponent now ships `AddShake` / `AddPunch` / `AddTilt`

**Symptom:** You hand-roll (or scaffold) a camera effect the engine turns out to provide
natively — duplicated behavior, or two systems fighting over the camera.

**Why:** The SDK adds surface between releases. Case in point (verified 2026-07-12):
`CameraComponent` now has **built-in `AddShake` / `AddPunch` / `AddTilt`** effects that
didn't exist when `create_camera_shake` shipped. Neither is *wrong* — the bridge's trauma
model is still the richer pattern — but you should know both exist before stacking them.

**Fix:** Before building any camera/feel effect, `describe_type "CameraComponent"` (and
friends) to see what the engine already ships. Reflection over folklore — the standing
rule, applied to *additions*, not just removals.

**The verified surface (2026-07-13):** every effect returns a
**`Sandbox.CameraEffectSystem.BaseEffect`** — a **nested** type `search_types` won't
surface (see #10): `Stop()` / `IsDone` / `TimeAlive` / `Duration`, plus **`Epicenter`
(`Vector3?`) + `Radius`** for distance falloff. Defaults: `AddPunch` frequency = 1,
duration = 0.3, fovAmplitude = 0; `AddTilt` easeTime = 0.2; `AddShake` has no defaults.
The official docs (scene/components/reference/camera-effects) also document **`EnvShake`,
`ShakePhysics`, controller `Rumble`, and the `ICameraModifier` extension point** — surface
the bridge does not yet cover. `create_camera_effects` wraps the covered set;
`create_camera_shake` remains the continuous trauma model — the two compose, but don't
fire both for the same event.

---

## 17. Hotload can transiently corrupt `MovieRecorder` GLOBALLY

**Symptom:** Right after a hotload, `MovieRecorder.Start()` throws an NRE — from **every**
assembly (bridge and sandboxed game code alike), on recorder code that worked minutes
earlier and is unchanged.

**Why:** Observed once (2026-07-13), not reproduced since: a hotload left the engine's
MovieMaker recording machinery in a broken **global** state — every `Start()` NRE'd until
the editor was restarted. Engine-internal state; nothing the addon can reach or patch.

**Fix:** **`restart_editor`.** The corruption clears on a clean relaunch. If `Start()`
suddenly NREs everywhere right after a hotload, don't debug your code first — restart,
then retry.

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
| `describe_type` on `Game`/`Stats` comes back empty | it doesn't report statics, constructors, or public fields | `get_method_signature` (param is `type`, not `name`) or `execute_csharp` reflection; read **`hasDefault`** before calling a param "required" |
| Services code won't compile / members "missing" | nested `Stats+PlayerStats`; `Board2` vs `Board`; `Entry` uses fields; `Stats.LocalPlayer` is a reflection-invisible static | code the verified shapes (#11) — trailing params have defaults (`SetValue` 2-arg and bare `Refresh()` are legal); the shipped stats scaffolds bake them in |
| `[GameResource]` obsolete warning | attribute deprecated | `[AssetType(Name/Extension/Category)]`; extension must not suffix a built-in |
| Recorded clip is ~2× wall time / recorder captures 0 tracks | `MovieRecorder.Start()` auto-advances; bare `new MovieRecorderOptions()` captures nothing by design | in play mode monitor only — never pump Advance/Capture; `WithCaptureGameObject` (+ `WithCaptureComponent` for camera props), `WithDefaultCaptureActions()`, or `MovieRecorderOptions.Default` |
| Editor-side JSON throws `must specify a TypeInfoResolver` | custom `JsonSerializerOptions` need an explicit resolver in editor context | plain `ToJsonString()` / `JsonSerializer.Serialize`, no options |
| Can't lipsync TTS audio | `LipSync` wants a `BaseSoundComponent`, not a `SoundHandle` | drive morphs yourself: `handle.LipSync.Visemes` × `Model.GetVisemeMorph` (what `generate_lipsync_dialogue` generates) |
| Panel type resolves oddly / appears twice | Razor panels get a folder-derived namespace; sibling `.cs` stays global | use the fully-qualified name from `search_types`, or pin one with `@namespace` |
| Hand-rolled camera effect fights a built-in | SDK added `AddShake`/`AddPunch`/`AddTilt` on CameraComponent | `describe_type` before building feel effects; `create_camera_effects` wraps the built-ins — don't stack unknowingly |
| `MovieRecorder.Start()` NREs from every assembly after a hotload | hotload transiently corrupted the recording machinery globally (seen once) | `restart_editor`, then retry — don't debug your code first |
