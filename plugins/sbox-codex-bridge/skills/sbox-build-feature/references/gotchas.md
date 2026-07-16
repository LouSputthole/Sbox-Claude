# s&box Bridge Gotchas

Read this before implementing a non-trivial feature or diagnosing a bridge result that
looks successful but behaves differently at runtime. Live reflection and current tool
results still win when an SDK or wrapper has changed.

| Gotcha | What to do instead |
|---|---|
| Which math is sandbox-safe is NOT "MathX only" | `MathX` is always safe but small: it has `Clamp`/`Lerp`/`LerpInverse`/`Remap`/`Floor`/`FloorToInt`/`ExponentialDecay`, but no `Abs`/`Min`/`Max`/`Sin`/`Cos`/`Atan2`/`Sqrt`/`Pow`/`PI`. Many projects also whitelist `System.Math` and `System.MathF`. Prefer `MathX` for what it has; use `System.Math`/`MathF` only after reflection or a compile check confirms the project allows them. |
| Cloud-only assets such as `Cloud.Model("foo")` do not persist across project restarts | Use local files or core engine assets. |
| s&box does not support `.mp3` | Convert it to `.wav`. |
| A saved `[Property]` value is deserialized over the field initializer | Treat field initializers as defaults that the inspector may override. |
| `TimeSince` fields start at zero, so cooldowns may fire immediately on spawn | Initialize cooldown timers high, for example `private TimeSince _sinceX = 100f;`. |
| Hotload can become stale after repeated iterations | Touch and hotload again; if that fails, restart the project. |
| `Cloud.Model(variable)` fails | The source generator requires a string literal; inline the asset identifier. |
| Citizen bone names are case-sensitive | Use `TryGetBoneTransform("head")` with lowercase `head`. |
| `CitizenAnimationHelper.IkRightHand` is a writable `GameObject` | Assign an IK target GameObject at runtime. |
| `set_property` for `Color` expects a value string | Pass `"r,g,b,a"`, then read the property back. |
| `Vector3` property writes used to report success while leaving the old value | Current `set_property` and `set_runtime_property` handlers explicitly coerce comma strings and JSON object/array forms into `Vector3`. Read the property back and treat a tool error or mismatched value as failure. |
| `take_screenshot` renders only the main-camera view | Use `capture_view`/`screenshot_from` with `id`, or `screenshot_orbit`; inspect the inline PNG result. |
| Runtime `ParticleEffect` helpers such as `spawn_particle`, `add_trail`, and `add_beam` do not render through the bridge | Use `spawn_vpcf` with a compiled `.vpcf` and `LegacyParticleSystem`. |
| Play-state diagnostics can disagree | Trust `is_playing.isPlaying`; treat `gameFlag` and `sessionPlaying` as diagnostics. |
| A placed `EnvmapProbe` captures nothing until baked | Call `bake_reflections`. |
| Scene-mutating tools are refused during play mode | Stop play, mutate the edit scene, then start play again. |
| `play_animation` is overridden by a Citizen animgraph | Use `set_animgraph_param` for Citizens; reserve `play_animation` for raw-sequence models. |
| Code generators write C# strings that can still fail compilation | Hotload and verify with `compile_status`; when the optional lifeline is enabled, use `get_compile_errors`; `describe_type <Class>` should resolve after a successful compile. |
| `trigger_hotload` may not recompile an externally edited project `.cs` file | Enter play with `start_play` to force a project compile, then stop play before scene edits. Addon changes under `Libraries/` require `restart_editor`. |
| Generated game code runs in the sandbox | Use only allowed BCL APIs. Add `using System;` when required, and compile-check any `System.Math`/`MathF` or `Random.Shared` usage. |
| Asset and object reference properties need real typed values | Current `set_property`/`add_component_with_properties` handlers coerce asset paths and GameObject/component GUIDs; an unresolved value is a tool error. Read back critical wiring. |
| A Citizen attack needs an animgraph trigger | Set `helper.HoldType = HoldTypes.Punch`, call `helper.Target.Set("b_attack", true)`, and reset the hold type after about 0.4 seconds. |
| Sound asset paths are project-root relative | Use `Sound.Play("sounds/<name>.sound", worldPos)` with no `Assets/` prefix. |
| `Sandbox.LipSync` drives facial morphs only while its sound plays | Use `add_lipsync` to wire a renderer and sound component, then verify with `capture_view` in play mode. |
| `CitizenAnimationHelper.ProceduralHitReaction` can throw on a freshly spawned Citizen | Build a `DamageInfo` with the available attacker/damage/position/origin data, wrap the cosmetic reaction defensively, and apply gameplay damage/knockback separately. |
| Child GameObjects inherit parent tags | Filter AI targets by a meaningful component, not a tag alone. |
| Real players and test dummies may use different health component types | Resolve the supported health component(s) before applying AI damage. |
| `facepunch.playercontroller` no longer exposes `PlayerController.AnimationHelper` | Trust the loaded assembly and runtime log, not a stale library cache. Drive a separate `CitizenAnimationHelper` from `CharacterController` velocity/look data when animation is needed. Do not confuse the library controller with the engine's Rigidbody-based `Sandbox.PlayerController`. |
| Razor state does not rerender unless `BuildHash()` changes | Fold every value read by the markup into `BuildHash()`. Use `@ref` plus imperative `Style`/text updates only when the render state cannot be expressed by a hash. |
| Absolutely positioned HUD children anchor to their nearest positioned ancestor | Put screen-anchored overlays at the root instead of inside another absolutely positioned panel. |
| Runtime-spawned GameObjects use runtime IDs | Rediscover IDs from `get_scene_hierarchy`/`find_objects` in the active play scene, then use those IDs with `get_runtime_property`, `set_runtime_property`, or other runtime tools. Edit-scene and play-scene IDs can differ. `get_scene_hierarchy` honors `maxDepth` and `rootId`; use them to limit traversal. |
| A round restart can overwrite `[Sync]` values changed for a fast-forwarded test | Disable the round-ending AI and automatic restart before setting flags, or the reset path may clear them. |
| A runtime system cannot be added to a shared or locked scene | Use `GameObjectSystem<T>` so the engine constructs one per live `Scene`. Guard `scene is null || scene.IsEditor`, make startup idempotent for hotload, and create the host object/component from the system constructor. |
| A Razor `PanelComponent` cannot be referenced by its bare class name | Razor panels can compile into a folder-derived namespace. Use the fully qualified type reported by `search_types`, or pin the namespace with `@namespace` and a matching C# namespace. |
| `Light` has no `Brightness` property | Scale the HDR magnitude of `LightColor`. For night scenes, also tune the directional light's sky fill, `AmbientLight`, `SkyBox2D` tint/indirect lighting, and `GradientFog`. |
| Non-positional ambience, heartbeat, or UI audio falls off in 3D | After `Sound.Play`, set `ListenLocal = true` and `SpacialBlend = 0f`. Re-trigger a one-shot when its handle finishes if a looping asset is unavailable. |
| Floating world text does not require a `WorldPanel` | Add `TextRenderer` and set text/color/font size/weight/scale. Leave nested alignment enums at defaults unless the installed SDK confirms their exact names. |
