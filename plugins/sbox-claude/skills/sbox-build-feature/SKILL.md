---
name: sbox-build-feature
description: Use when building, modifying, or polishing any feature in an s&box game project through the Claude Bridge — gameplay systems, UI panels, character abilities, animation, world generation, anything that produces a visible or runtime change. Codifies the screenshot-driven iteration workflow that prevents the "guess-and-check" loop the bridge is most susceptible to.
---

# Building s&box Features Through the Bridge

This skill is the workflow you follow whenever you're about to make non-trivial changes to a player in an s&box project via the bridge. It exists because the bridge gives Claude a lot of power but **no eyes** — without discipline, sessions devolve into guessing about how things look. These steps prevent that.

**Pair this with the `sbox-api` skill** — that's the *brain* (how to write correct s&box C#: the Unity→s&box translation table, the Ten Rules, and component/UI/networking/physics references). This `sbox-build-feature` skill is the *hands + eyes* (drive the editor, screenshot, verify live). Write it right with `sbox-api`; then build it, run it, and SEE it with the bridge. And the bridge's live reflection (`describe_type`/`search_types`/`get_method_signature`) is the authoritative signature check for your installed SDK.

**Invocation (v2, native MCP):** the tool names below are the plain bridge tool names, served by s&box's native editor MCP server. Find them with `search_tools "<terms>"`, run them with `call_tool {name, arguments}` (batch several in one round trip with `call_tools`), and browse groups with `list_toolsets` / `describe_toolset`. Six names — `spawn_model`, `list_scenes`, `save_scene`, `undo`, `redo`, `remove_component` — are served by the native server's own built-ins: same names, same semantics, Facepunch's implementations.

## Hard rule: never declare a visual feature "working" without seeing it

If your change affects anything visual (a model, a position, an animation, a UI panel, a particle, a light), you **must** see it before saying the work is done. You're a multimodal model — you can read PNGs.

**Aim the camera, or you'll screenshot the wrong thing.** `take_screenshot` renders the **main camera** view (the player's view in play mode) — one angle that usually isn't pointed at what you just changed. Use **`capture_view`** (or its historical alias `screenshot_from`) to frame your target object/point, or `screenshot_orbit` for several angles in one call. Every screenshot tool returns the PNG **inline in the tool result** — the image is right there in the response; there is no file to hunt down. This is the single highest-leverage habit in the whole workflow.

## The Workflow — six steps, in order

### 1. Confirm the bridge is alive

```
get_bridge_status
```

If the native server doesn't answer: s&box isn't running, the editor is mid-compile/relaunch, or the MCP server is off — check **Editor → Preferences → MCP Server** (on by default at `http://127.0.0.1:7269/mcp`). If the editor log says `[MCP] Couldn't start MCP server on port 7269`, a stale HTTP.sys registration from a dying editor instance is holding the port — restart the editor once the stale process exits. Don't go further until it responds.

### 2. Brainstorm before code (for non-trivial features)

If the feature is more than a one-line tweak — anything that involves:
- A new state machine
- Animation, IK, or camera work
- A new component or system
- Anything where you can't predict the visual outcome with confidence

…invoke `superpowers:brainstorming` first. Don't skip this. The brainstorming skill exists because the cost of designing wrong is much higher than the cost of designing slowly.

### 3. Research the s&box API before guessing

Before writing code that calls a type or method you haven't verified exists in the current SDK build:

```
describe_type           name="CitizenAnimationHelper"
search_types            pattern="*Renderer"
get_method_signature    type="GameObject" method="AddComponent"
```

s&box's API changes between versions. Reflection is the source of truth, not your training data. Look it up.

If you need broader documentation (animation graph, IK setup, rendering pipeline), use WebFetch on https://wiki.facepunch.com/sbox/ or search Discord.

### 4. Implement with bite-sized edits

- One change per `Edit` call. Don't batch unrelated edits.
- Keep changes scoped to one file at a time when possible.
- For the bridge addon specifically: **never copy `claudebridge.sbproj` from the repo into a project's `Libraries/`** — the repo version has `Org: sboxskinsgg` (for asset library publish) and a project's working copy must stay `Org: local`. Mixing these causes a compiler-name collision that prevents the project from loading.

### 5. Hotload and verify compile

```
trigger_hotload
```

Then tail the log:

```bash
LOG="A:/SteamLibrary/steamapps/common/sbox/logs/sbox-dev.log"
tail -30 "$LOG" | grep -iE "Compile of 'local\.<projectname>.*Failed|Error \|"
```

**`Compile of 'local.X' Failed`** lines from earlier hotloads can be stale and survive in the log — what matters is whether the line timestamp is recent and the error message mentions YOUR file. If there's a real error: fix it, re-hotload, re-check.

### 6. Screenshot and look at it

For any visual change, **aim the camera at the thing you changed**:

```
capture_view       id=<object GUID>          # auto-frame an object (screenshot_from is the same tool, older name)
capture_view       position=… lookAt=…       # free camera at any angle
screenshot_orbit   id=<object GUID>          # several angles in one call
```

The PNG comes back **inline in the tool result** — there is no file path to read back; the image is right there in the response. Plain `take_screenshot` renders the main camera's view (the player's view in play mode) — fine if it already frames your subject, useless otherwise. `capture_view` shoots from a temporary camera (auto-framed on an object, or free position+lookAt) and cleans up after itself; `screenshot_orbit` orbits an object and returns every angle at once. **Look at the image.** If it doesn't match the design, iterate. Don't declare the feature done based on the code looking right.

The verify loop is exactly: **make the change → call `take_screenshot` (or `capture_view` / `screenshot_orbit` for framed or multi-angle shots) → look at the returned image.**

For diagnosing a compile/runtime failure, you don't need the editor to respond: `get_compile_errors` and `read_log` live on the **lifeline** server (`npx -y sbox-mcp-server@2 --lifeline`) and read `sbox-dev.log` directly — the native server dies with the editor; the lifeline doesn't.

For timing-sensitive captures (e.g. a 0.20s animation phase), coordinate with the user: "press the action and tell me 'go' the moment you do" — fire `take_screenshot` immediately, the round-trip captures roughly the right window.

## Seeing & driving the RUNNING game (play mode)

The bridge can verify *gameplay*, not just the edit scene — but the play-mode tools behave differently from the edit-mode ones:

- **Seeing the running game:** `take_screenshot` in play mode IS the player's POV (the live main camera). `capture_view` renders the *active* scene from any viewpoint — no args = the live main camera; pass `position`/`id` for a temp camera at any angle. Either way the PNG arrives **inline in the tool result** — look at the returned image, there's no path to read back.
- **`capture_view` sees *through* screen-space menus.** It renders the world + **world-space** UI but NOT fullscreen **screen-space** panels (lobby/title `ScreenPanel`s) — so a fullscreen lobby overlay won't black it out, but it also won't show screen-space HUD. `take_screenshot` (literal viewport) is the screen-space-UI complement.
- **To start a match / fire game logic / get past an in-game menu, use `invoke_button`.** It calls *any parameterless public method* on a component (not just `[Button]`s) — e.g. `invoke_button` with `component="SasquatchedGame" method="StartGame"` leaves the lobby. The bridge can't synthesize a UI click; this is how you drive game state.
- **Networked components are proxies in a no-session solo playtest** — a host-authoritative component (`if (IsProxy) return;`) won't run solo. Generate NPC brains etc. with `networked:false` to iterate solo, or start a host session.
- **Prove runtime behavior with `get_runtime_property`** (an NPC's `CurrentState`, a health value, etc.) — unambiguous, and it works even under a menu overlay.
- **Play-state is reliable now:** `stop_play` actually stops (symmetric `EditorScene.Stop`) and `is_playing.isPlaying` is authoritative.

## Multi-agent work: the bridge drives ONE editor

The bridge connects to a single running s&box editor. **Multiple agents cannot drive play-mode / screenshots / scene mutation concurrently** — they'd fight over the same editor, the same play session, the same Main Camera, and the shared `.scene` file. The pattern that works for a parallel build:

- **Parallel agents AUTHOR on disjoint files only** — each owns a non-overlapping set of `.cs`/`.razor`/`.scss` files and writes them with the normal file tools (Edit/Write), **no play-mode, no scene edits, no screenshots.** This is exactly why `GameObjectSystem<T>` self-bootstrapping (see the gotchas table) matters: a runtime system that needs no `.scene` entry lets several agents add gameplay/atmosphere/UI in parallel without anyone touching the locked scene file.
- **One orchestrator verifies serially** — after the authors finish, a single agent (or the human) hotloads, drives play-mode, takes screenshots, and reads them. Don't have two agents call `start_play`/`take_screenshot` against the same editor.
- If two things genuinely must run at once and both need the editor, they need **separate s&box instances each with their own bridge** — not the default setup.

## Common s&box gotchas (so you don't re-discover them)

| Gotcha | What to do instead |
|---|---|
| Which math is sandbox-safe is NOT "MathX only" | `MathX` is **always** safe but is small — it has `Clamp`/`Lerp`/`LerpInverse`/`Remap`/`Floor`/`FloorToInt`/`ExponentialDecay` etc. but **NO `Abs`/`Min`/`Max`/`Sin`/`Cos`/`Atan2`/`Sqrt`/`Pow`/`PI`**. Many projects (incl. bigfoot) **also whitelist `System.Math` + `System.MathF`**, so `Math.Clamp`/`Math.Max`, `MathF.Abs`/`MathF.Sin`/`MathF.Atan2`/`MathF.Sqrt`/`MathF.PI` compile there. **Prefer `MathX` for what it has; fall back to `System.Math`/`MathF` for the rest — but verify the project allows it** (`describe_type "System.MathF"` resolves, or just compile-check). For a trig-free oscillation you don't have to risk it at all: use a phase accumulator + triangle wave (`var tri = 1f - Abs(phase*2f - 1f);` advance `phase += Time.Delta*rate`, wrap with `phase -= MathX.Floor(phase)`). |
| Cloud-only assets (`Cloud.Model("foo")`) don't persist across project restarts | Use local files or core engine assets |
| `s&box doesn't support .mp3` | Convert to `.wav` (ffmpeg is your friend) |
| Setting `[Property]` on a saved component overwrites the deserialized value | Use field initializers as defaults that can be overridden in the inspector |
| `TimeSince` fields default to 0 → cooldowns fire immediately on spawn | Initialize to a large value: `private TimeSince _sinceX = 100f;` |
| Hotload cache gets stuck after multiple iterations | Restart the project or touch+hotload |
| `Cloud.Model(variable)` fails | The source generator requires string literals — always inline |
| Citizen "head" bone exists but bone names are case-sensitive | `TryGetBoneTransform("head")` — lowercase |
| `CitizenAnimationHelper.IkRightHand` is a writable GameObject — IK works at runtime | Set it to a target GO to drive the hand via IK |
| `set_property` for `Color` wants `"r, g, b, a"` as a string, not a JSON object | Format the value as a comma-separated string |
| `take_screenshot` renders the **main camera** view only (the player's view in play mode) | Use `capture_view`/`screenshot_from` to frame your target, or `screenshot_orbit` for multi-angle; the PNG arrives **inline in the tool result** — no file to read back |
| Runtime `ParticleEffect` tools (`spawn_particle`, `add_trail`, `add_beam`) don't render through the bridge | Use `spawn_vpcf` (compiled `.vpcf` + `LegacyParticleSystem`) |
| Play-state flags | `is_playing.isPlaying` is authoritative (`gameFlag‖tracked`); `sessionPlaying` is diagnostic-only and can read stale |
| A placed `EnvmapProbe` captures nothing until baked | Call `bake_reflections` |
| Scene-mutating tools refused during play mode (v1.2.0+) | Stop play, mutate, restart play |
| `play_animation` is overridden by the animgraph on a Citizen | Use `set_animgraph_param` (`duck`, `move_x`/`move_y`, …) for Citizens; `play_animation` is for raw-sequence models |
| Code-gen tools (`create_npc_brain`, `create_*_system`, `create_player_controller`) write game-code *strings* — inspection misses compile errors | Always compile-verify the generated component: `describe_type <Class>` resolves only if it compiled (or scan the log for `error CS`) |
| `trigger_hotload` doesn't reliably recompile externally-edited `.cs` | Entering play (`start_play`) forces the project recompile; **addon** changes need a full `restart_editor` |
| Generated game code runs in the sandbox | Only sandbox-allowed BCL compiles. `using System;` is needed for things you'd assume are global — e.g. **`Random.Shared.Float`/`.Int`** is `System.Random.Shared` + a Sandbox extension, so a missing `using System;` fails with "name 'Random' does not exist". (For math specifically, see the MathX/Math/MathF row above — it's nuanced, not a blanket ban.) |
| `set_property`/`add_component_with_properties` now coerce asset + reference props | You can set `Model`/`Material`/`GameObject`/`Component` props by path/GUID; an unresolvable value fails with a real tool error (no more silent null) |
| Trigger a Citizen attack/one-shot animation | `helper.HoldType = HoldTypes.Punch` + `helper.Target.Set("b_attack", true)` (one-shot bool trigger). Reset `HoldType` after ~0.4s or the pose sticks |
| Play a sound in code | `Sound.Play("sounds/<name>.sound", worldPos)` — paths resolve case-insensitively, no `Assets/` prefix |
| New `Sandbox.LipSync` component (2026-07-01 engine update) drives facial morphs from audio | Use `add_lipsync` to wire it to a citizen/model — `SkinnedModelRenderer` + a `SoundPointComponent` bound to a `.sound` path (or an existing sound component); morphs only animate while the sound plays, so verify with `capture_view` in **play mode**, not an edit-mode screenshot |
| Hit reaction / knockback | `CitizenAnimationHelper.ProceduralHitReaction(new DamageInfo{ Attacker=…, Damage=…, Position=…, Origin=… }, scale, force)` (no factory — object initializer) poses the target's OWN helper; add to `CharacterController.Velocity` for a stagger. ⚠️ can throw an internal NRE on a freshly-spawned Citizen (unset `DamageInfo.Hitbox`/`Shape`) — wrap it in try/catch; the flinch is cosmetic, you apply damage/knockback separately anyway |
| Child GameObjects INHERIT parent tags | a child (e.g. a flashlight beam) carries the parent's `player` tag — filter AI targets by an actual component (a health comp), not tag alone |
| Spawned player-camper has `HealthComponent`, the dummy has `CamperHealth` | when an AI deals damage, try both health component types so it works on real players, not just test dummies |
| `facepunch.playercontroller` dropped `PlayerController.AnimationHelper` | the property is GONE — get AND set throw `MissingMethodException` at runtime. (The on-disk `Libraries/…/PlayerController.cs` is a STALE cache that still shows it; the LOADED assembly is truth. `describe_type "PlayerController"` resolves the bare name to the wrong `Sandbox.PlayerController` — read the runtime log for the real error.) The LIBRARY's `PlayerController` still drives movement via `CharacterController` but no longer animates the Citizen — and don't conflate it with the ENGINE's built-in `Sandbox.PlayerController`, which is **Rigidbody-based** (per the official docs), not CharacterController-based. Fix: drive a `CitizenAnimationHelper` yourself each frame (`WithVelocity`/`WithWishVelocity`/`IsGrounded`/`MoveStyle`/`WithLook` from `CharacterController.Velocity`) — the same pattern an AI brain uses |
| s&box Razor `@if`, root-`class="@(…)"`, and `@Prop`-as-element-text bindings do NOT re-render reliably | This is the #1 UI trap, and the PRIMARY fix is **fold every piece of render state into `BuildHash()`** — a panel only re-renders when its `BuildHash` changes (official docs: ui/razor-panels), so override it to combine everything the markup reads (`protected override int BuildHash() => HashCode.Combine(Health, IsVisible, RevealedChars);`). The bridge's own `razor_lint` enforces exactly this, and `add_panel_buildhash` patches it into an existing panel. FALLBACK (only when a hash genuinely can't express it): hold `@ref` handles and push values imperatively in `OnUpdate` — `Panel.Style.Display = DisplayMode.Flex/None` for show/hide, `X.Text = …` for text. Either way, **STATIC** markup text and **style-ATTRIBUTE interpolation** (`style="width:@(P)%"`, `style="opacity:@(o)"`) update fine, as does setting `Style.*` imperatively. |
| Absolutely-positioned HUD elements anchor to their nearest positioned ancestor | A `position:absolute` cue with `top/left` placed INSIDE another `position:absolute` panel (e.g. a bottom-left `.hud`) positions relative to THAT box, not the screen — put screen-anchored overlays at `<root>` level. |
| `set_property`/`set_runtime_property` on a `Vector3` silently no-ops | Returns `set:true` but reads back the default; `bool`/`float`/`string` persist fine. Use a source default or a float knob for vector-ish config. |
| Runtime-SPAWNED GameObjects aren't GUID-addressable via the bridge | `set_runtime_property`/`set_enabled` resolve only SAVED-scene objects by GUID ("GameObject not found" otherwise). Drive spawned objects via a `[Button]`/method on a scene component. Also: `invoke_button`'s param is `button` (method/label name); `get_scene_hierarchy` ignores `maxDepth` (grep the saved `.scene` for GUIDs). |
| A round-restart that re-runs a "reset match state" wipes `[Sync]` values you poked in | When fast-forwarding via the bridge (set flags → trigger), disable the AI that ends the round + set `AutoRestartMatch=false`, or the auto-restart's reset clears your flags mid-test. |
| You need a runtime system but **can't add it to the scene** (shared/locked `.scene`, or parallel agents must not touch it) | Use **`GameObjectSystem<T>`** — the engine constructs exactly one per live `Scene` automatically, no scene-file edit. Spawn your host GameObject/components in `public MySystem(Scene scene) : base(scene)`. **Guard `if (scene is null \|\| scene.IsEditor) return;`** (never run in the edit scene) and make it **idempotent** (`if (scene.GetAllComponents<MyHostComp>().Any()) return;`) so a hotload — which re-constructs systems — doesn't spawn a second copy. Ideal for atmosphere/director/ambience systems. Working reference: `Libraries/extended.extendednetworking/Code/AdminSystem.cs`. |
| A Razor `PanelComponent` you can't reference from other C# ("type or namespace not found") | Razor panels compile into a **folder-derived namespace** (`Sandbox.<Folder>.<Class>`) in current builds — live-verified 2026-07-12; plain sibling `.cs` classes stay global, so a bare-name reference between the two schemes misses. Resolve via the fully-qualified name `search_types` reports, or add `@namespace Foo` at the top of the `.razor` (matching the C# `namespace Foo;` that needs to `AddComponent<ThePanel>()` or hold a typed ref) to pin it explicitly. |
| There is no `Light.Brightness` | Light **intensity is the HDR magnitude of `LightColor`** — scale its RGB (channels may exceed 1) for brighter/dimmer; a small-magnitude colour = dim. Scale by hand (`new Color(c.r*k, c.g*k, c.b*k, c.a)`) rather than trusting a `Color`×float operator that can differ between SDK builds. **Night/atmosphere recipe (all runtime, no scene edit):** retune the scene `DirectionalLight` (`LightColor` + its `SkyColor` fill + a low raked `WorldRotation`), drop `AmbientLight.Color`, darken `SkyBox2D.Tint` and set `SkyBox2D.SkyIndirectLighting = false` (stops the bright sky acting as a giant fill light), then add a `GradientFog` (`Color`/`StartDistance`/`EndDistance`/`FalloffExponent`/`Height`). |
| Non-positional sound (ambience, heartbeat, UI) plays in 3D / falls off with distance | After `var h = Sound.Play(evt);` set `h.ListenLocal = true; h.SpacialBlend = 0f;` (fully 2D, same on every client — these are client-local, no networking). To **loop a one-shot `.sound`** without a looping asset, re-`Sound.Play` whenever `!h.IsValid() \|\| h.Finished \|\| h.IsStopped`, and push `h.Volume`/`h.Pitch` every frame so `set_property` live-tuning takes effect. |
| Floating 3D text without a WorldPanel | `go.AddComponent<TextRenderer>()` (`Sandbox.TextRenderer`) renders text as a world quad — set `.Text`/`.Color`/`.FontSize`/`.FontWeight`/`.Scale`. ⚠️ Its `HorizontalAlignment`/`VerticalAlignment` are a **nested enum the bridge's `describe_type` may not resolve by name** — leave alignment at defaults in code (the label still renders, only its anchor differs) and set it in-editor if needed, rather than guessing an enum literal that won't compile. |

## The bridge map (knowledge graph)

The bridge ships a graphify map of itself at **`docs/graph/`** — every tool maps to its C#
`IBridgeHandler` and to the docs. **Before adding or changing a bridge tool, consult
`docs/graph/graph.json` or `docs/graph/graph.html` to see what connects to what.** It CAN GO STALE
(check the date in `GRAPH_REPORT.md`), and **maintainers must regenerate it as part of every
release** (`scripts/regen-graph.ps1`, or re-run `/graphify` for the full doc-inclusive graph). See
`docs/graph/README.md`.

## Project-level CLAUDE.md

If the project you're working on has its own `CLAUDE.md`, **read it first**. It captures project-specific decisions (input bindings, sound files, role assignment, scene layout) that this skill can't know about.

## The thing that always works

When you're stuck, in a loop, or about to make your fifth guess at a visual offset:

1. Take a screenshot of the current state
2. Read it yourself
3. Describe to the user exactly what you see vs. what should be there
4. Propose a specific adjustment (with magnitude) rather than another guess

The screenshot loop closes faster than the guess loop. Use it.
