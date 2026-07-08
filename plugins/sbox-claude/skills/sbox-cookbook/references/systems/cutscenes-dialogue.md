# Cutscenes, Cinematics & Dialogue (bridge scaffolds)

Staged story beats, camera flythroughs, and talking NPCs — with the bridge's own scaffolds. Two of these are HAND-AUTHORED, no-asset C# components you generate and tune in the inspector; one is the keyframed `Sandbox.MovieMaker` path that plays a `.movie` clip you author in the editor. Use this whenever you need an intro cinematic, a scripted camera move, a VN-style conversation, or a cutscene triggered from gameplay.

Everything here is grounded in two bridge handler files, not mined games:
`sbox-bridge-addon/Editor/CinematicsHandlers.cs` (`create_cutscene_director` + `create_dialogue_system`) and `sbox-bridge-addon/Editor/MovieMakerHandlers.cs` (`list_movies` / `add_movie_player` / `play_movie` / `stop_movie`, first shipped in the 2026-07-08 build — verified live via `search_types`; absent 2026-07-02).

## What it IS (and when)

There are **two cutscene paths**, and picking the wrong one wastes a lot of time:

1. **Hand-authored camera-shot cutscene** (`create_cutscene_director`) — a sealed `Component` that flies the main camera between poses you type into parallel inspector lists. **Zero assets**, full C# control, plays on today's engine. Reach for it for an intro flythrough, a "look at the boss door" beat, an establishing pan — anything you'd rather script than keyframe.
2. **Keyframed `.movie` clip** (`Sandbox.MovieMaker`) — a `MoviePlayer` component bound to a `.movie` `GameResource` you author in the editor's **Movie Maker dock** (Window → Movie Maker). Reach for it when you want a real timeline with tracks (multiple objects, properties, sound) rather than a camera-only fly-between.

And a third, orthogonal piece: **dialogue** (`create_dialogue_system`) — a typewriter Razor HUD for NPC/story lines that composes with either cutscene path.

**When to use which cutscene path**

| Want… | Use | Why |
|---|---|---|
| Camera-only flythrough, no assets, scripted from code | `create_cutscene_director` | Author shots as inspector lists; `Play()` from any game code |
| Multi-track timeline (several objects/properties/sound animated) | `Sandbox.MovieMaker` + `add_movie_player` | The `.movie` clip carries keyframed tracks the director can't |
| Cutscene RIGHT NOW with nothing authored yet | `create_cutscene_director` | The MovieMaker path needs a `.movie` authored first (bridge won't keyframe one) |
| You (or a designer) already authored a `.movie` in the dock | `add_movie_player` + `play_movie` | Wire the existing clip and play it |

## Path A — hand-authored camera-shot cutscene (`create_cutscene_director`)

`create_cutscene_director` writes a sealed `Component` (class name defaults to `CutsceneDirector`; pass `name` to rename). It needs **no `.movie` asset** — you fill parallel shot lists in the inspector and it takes over `Scene.Camera` to fly between them (`CinematicsHandlers.cs:63`, `:165`).

**The shot lists** (parallel, one entry per shot, indexed by `ShotPositions.Count`):

- `ShotPositions : List<Vector3>` — camera world position per shot.
- `ShotAngles : List<Angles>` — camera pitch/yaw/roll per shot. `Angles`, not `Rotation`, because quaternions are miserable to hand-edit; converted at runtime via `Rotation.From(Angles)`.
- `ShotHoldSeconds : List<float>` — hold on the shot after the blend completes.
- `ShotBlendSeconds : List<float>` — blend INTO the shot from the previous pose (0 = hard cut).
- `ShotLookAt : List<GameObject>` — optional per-shot aim target; when set, **overrides `ShotAngles`** for that shot (`Rotation.LookAt`).

Every list access is length-safe, so ragged lists don't throw; missing entries fall back to `DefaultHoldSeconds` (2f) / `DefaultBlendSeconds` (1f) (`CinematicsHandlers.cs:351-372`).

**Playback mechanics** (all in `CinematicsHandlers.cs`):
- Takes over the camera in **`OnPreRender` only while playing** — sets `Scene.Camera.WorldPosition`/`WorldRotation` (`:315`). `OnPreRender` runs *after* controllers/animation position the camera, so the takeover usually wins.
- Blend is **smoothstep-eased** (`t*t*(3-2t)`) — `MathX` has no `Ease`/`Smoothstep` on this SDK, so it's hand-rolled (`:387`). Position is `Vector3.Lerp`, rotation is `Rotation.Slerp`.
- **Captures the pre-cutscene camera transform on start and restores it exactly on finish** (`StartCutscene` `:270`, `Finish` `:334`), so a static camera is left precisely as it was and a controller resumes next frame.
- **Skip:** `SkipAction` (default `"jump"`, set via `skipAction` param) ends it early. The skip is read via `Input.Pressed` **before** input is cleared, so a locked cutscene is still skippable (`:288`).
- **Input lock:** `LockInput` (default `true`, set via `lockInput` param) zeroes all action state each frame via `Input.ClearActions()`, and calls `Input.ReleaseActions()` on finish (`:290`, `:345`).
- **Attach it anywhere** — it drives `Scene.Camera` itself, so it does **not** need to live on the camera.

**Entry points & hooks:**
- `CutsceneDirector.Play()` — plays the first director in the scene.
- `CutsceneDirector.Play("intro")` — plays the one whose `CutsceneName` matches (case-insensitive).
- `static event Action OnCutsceneFinished` — fires (on every client that ran it) when the cutscene ends, **skipped or completed** (`:204`, `:346`).
- `static bool IsCutscenePlaying` — gate HUD/game logic on it (`:207`).

**Optional letterbox** (`letterbox:true`): generates a `*Letterbox` Razor `PanelComponent` + SCSS whose black bars slide in (height `0 → 12%` via a CSS transition on `.active`) while `IsCutscenePlaying`. Host it under a `ScreenPanel` (`add_screen_panel`) (`CinematicsHandlers.cs:396`, `:419`).

## Path B — keyframed `.movie` clip (`Sandbox.MovieMaker`)

The MovieMaker family wires and plays a `Sandbox.MovieMaker.MoviePlayer` bound to a `MovieResource` (`.movie`, a `GameResource`). **Movies are authored in the editor's Movie Maker dock** — the bridge wires and plays them, it **does not author keyframes** (`MovieMakerHandlers.cs:29`).

**The four tools:**
- **`list_movies`** — file-scans the assets tree for `.movie` resources; returns `{ path, name, loadable, hasCompiledClip }` per movie. `loadable` = `ResourceLibrary.Get` succeeded; `hasCompiledClip` = `res.Compiled != null` (`:51`). When there are none, the response tells you to author one in the dock.
- **`add_movie_player`** — `GetOrAddComponent<MoviePlayer>` on `id` (or a new `"Movie Player"` object); optionally sets `Resource` from `moviePath`, plus `isLooping`, `timeScale`, `createTargets`, and `playOnStart` (sets `IsPlaying`) (`:94`). Scene-mutating.
- **`play_movie`** — resolves the player (explicit `id`, else the first `MoviePlayer` in the scene), optionally loads `moviePath`, seeks `positionSeconds`, sets `timeScale`/`isLooping`, then calls `player.Play()` (`:180`).
- **`stop_movie`** — sets `IsPlaying = false`, optionally rewinds `PositionSeconds` to 0 with `rewind:true` (`:223`).

**`MoviePlayer` API surface** (verified live via `describe_type`, `MovieMakerHandlers.cs:20-26`):
- `Resource : IMovieResource` (writable — a `MovieResource` satisfies it)
- `IsPlaying` / `IsLooping : bool` (writable)
- `TimeScale : float`
- `Position : MovieTime`, `PositionSeconds : float`
- `Play()` / `Play(MovieResource)` / `Play(IMovieClip)`, `UpdateTargets()`
- `CreateTargets : bool`, `Binder : TrackBinder` (read-only)

**Playback honesty (carry this through):** real playback **only advances in PLAY MODE**. In edit mode `play_movie` just sets state (the Movie Maker dock previews it), and the response says so (`:210`). To actually verify a clip: `start_play` → `play_movie` → `capture_view`/`take_screenshot`. And you can only verify a *real* clip — if `list_movies` returns nothing, there's nothing to play until someone authors one in the dock.

## Dialogue (`create_dialogue_system`)

`create_dialogue_system` writes a sealed `Component` (default `DialogueSystem`) **plus a paired Razor `PanelComponent` HUD** (`DialogueSystem` + `Panel`) that renders the current line with a typewriter reveal (`CinematicsHandlers.cs:461`).

- **Lines** are `List<string>` in the `"Speaker: text"` convention — text before the first colon is the speaker, the rest is the line (`ParseLine` `:669`). Editable in the inspector.
- **Reveal** is time-based off a `TimeSince` (`VisibleText` reveals `(int)(sinceLine * CharsPerSecond)` chars) — **not** a fire-and-forget async loop, so there's no mid-reveal cancellation footgun (`:652`). `CharsPerSecond` defaults to 40 (`charsPerSecond` param, floored at 1).
- **Advance** (`AdvanceAction`, default `"use"`): first press snaps the whole line into view (`_forceComplete`), a second press moves to the next line; the last line ends the conversation (`:623`).
- **The HUD auto-binds** to `DialogueSystem.Current` (the most-recently-enabled instance) — no wiring. Host it under a `ScreenPanel` (`add_screen_panel`). `VisibleText` is folded into `BuildHash` so the panel re-renders as characters appear (`:713`).

**Entry points & hooks:**
- `DialogueSystem.StartDialogue(new[]{ "Guide: Welcome.", "Guide: Press use to continue." })` — sets `Lines` on `Current` and begins; or set `Lines` in the inspector and call instance `Begin()`.
- `static event Action<int,string> OnLineShown` — `(lineIndex, speaker)` as each line begins (`:565`).
- `static event Action OnDialogueFinished` — when the last line is dismissed (`:562`).

**Pairings** (from the scaffold's own `nextSteps`, `CinematicsHandlers.cs:518`):
- **`add_lipsync`** — subscribe `OnLineShown` to drive a talking face; `add_lipsync` wires `Sandbox.LipSync` (a `SkinnedModelRenderer` + a `SoundPointComponent`) so facial morphs animate while the line's audio plays.
- **`create_interactable` / `add_interaction_station`** — trigger `StartDialogue` from an `IPressable` press instead of proximity, so the player *chooses* to talk.
- **`create_cutscene_director`** — bracket a conversation with staged camera beats (dialogue for the words, the director for the shots).

## Multiplayer semantics

Both the cutscene director and the dialogue HUD are **LOCAL / visual-only** — no `[Sync]`, each client runs its own camera view and its own HUD (`CinematicsHandlers.cs:126`, `:519`). Two consequences:

- **Trigger playback inside an `[Rpc.Broadcast]`** so every client starts the same cutscene/conversation. Calling `Play()` / `StartDialogue()` on one machine only plays it there. If the *decision* matters (which line everyone hears, whether the cutscene fired at all), drive it from a host-authoritative source and broadcast — don't trust a client-only reveal.
- **Camera-takeover caveat.** `OnPreRender` runs after controllers position the camera, so the director's takeover usually wins — but a player controller that *also* writes the camera in **its own `OnPreRender`** can fight it. Disable that controller for the duration and re-enable it on `OnCutsceneFinished` (`CinematicsHandlers.cs:125`). Because the director restores the exact pre-cutscene transform on finish, re-enabling the controller then hands control back cleanly.
- **MovieMaker** playback is a per-view action too; treat it as local, broadcast the trigger, and verify per-client with `capture_view` in play mode. The handlers assert playback timing (play mode only), not cross-client replication — don't assume a `MoviePlayer` syncs on its own.

## Recipes

### 1. Intro cinematic on round start (director → unlock input)

Compose with `create_round_state_machine`. In the opening state's `Begin`, broadcast the cutscene; wire `OnCutsceneFinished` to hand control back and advance the round.

```csharp
// In your round state's Begin (host authoritative), tell everyone to play it:
[Rpc.Broadcast]
void PlayIntro() => CutsceneDirector.Play( "intro" );   // LOCAL per client

protected override void OnEnabled()
    => CutsceneDirector.OnCutsceneFinished += OnIntroDone;
protected override void OnDisabled()
    => CutsceneDirector.OnCutsceneFinished -= OnIntroDone;

void OnIntroDone()
{
    // LockInput auto-released in Finish(); now unlock gameplay / advance the round.
    RoundManager.Current?.BeginPlay();
}
```

`LockInput=true` freezes input during the fly-through (`Input.ClearActions` each frame) and `Finish()` calls `Input.ReleaseActions()`, so `OnCutsceneFinished` is exactly the "players can move now" signal. Gate any HUD on `CutsceneDirector.IsCutscenePlaying` (add `letterbox:true` for black bars).

### 2. NPC conversation (interactable → dialogue → reward)

Trigger dialogue off an `IPressable` (`create_interactable`) or a claimed `add_interaction_station`, then grant on completion.

```csharp
// On the interactable's Press (IPressable). Broadcast so co-op players see it too.
[Rpc.Broadcast]
void TalkToNpc() => DialogueSystem.StartDialogue( new[]
{
    "Hermit: You made it.",
    "Hermit: Bring me three relics and I'll open the gate."
} );

protected override void OnEnabled()
{
    DialogueSystem.OnLineShown     += (i, speaker) => Lipsync?.Speak( speaker ); // pair with add_lipsync
    DialogueSystem.OnDialogueFinished += GrantQuest;
}
void GrantQuest() { /* give quest / reward on the last line */ }
```

`OnLineShown(index, speaker)` is the per-line hook for `add_lipsync` (talking face) or a voice blip; `OnDialogueFinished` fires once the player dismisses the final line — the clean place to grant the quest or reward.

### 3. Keyframed MovieMaker cutscene from a trigger zone

For a real timeline clip, author it first, then fire it from a volume.

1. **Author** the clip in the Movie Maker dock (Window → Movie Maker) and save it as a `.movie`. `list_movies` should then show it with `loadable:true` / `hasCompiledClip:true`.
2. **Wire** it: `add_movie_player` with `moviePath` set to that `.movie` (optionally `isLooping`, `timeScale`).
3. **Trigger** it: a `create_trigger_zone` whose enter callback plays the clip. In game code the trigger gets the player and calls `Play()`; broadcast so every client's view plays it:

```csharp
void ITriggerListener.OnTriggerEnter( Collider other )
{
    if ( !other.GameObject.Tags.Has( "player" ) ) return;
    if ( _fired ) return; _fired = true;      // one-shot
    PlayClip();
}

[Rpc.Broadcast]
void PlayClip() => Components.Get<Sandbox.MovieMaker.MoviePlayer>()?.Play();  // LOCAL per client
```

To test the wiring from the bridge without gameplay: `start_play` → `play_movie` → `capture_view`. Remember real playback only advances in play mode — `play_movie` in edit mode only sets state (the dock previews it).

## Gotchas

- **The MovieMaker path can't start from nothing.** The bridge does **not** author keyframes — `add_movie_player`/`play_movie` need a `.movie` that already exists in the dock. `list_movies` returning `count:0` means "go author one," not "the tool failed" (`MovieMakerHandlers.cs:78`). If you want a cutscene with zero authored assets, use `create_cutscene_director`.
- **`.movie` playback only advances in PLAY MODE.** `play_movie` in edit mode sets `IsPlaying` but the clip won't move except in the dock preview — verify with `start_play` then `capture_view`, never from an edit-mode screenshot (`:210`).
- **`hasCompiledClip:false` won't play.** A `.movie` that isn't compiled/loadable (`loadable:false` or `hasCompiledClip:false` from `list_movies`) has nothing to play — re-save it in the dock.
- **A player controller can fight the camera takeover.** The director wins in `OnPreRender` *usually*, but a controller writing the camera in its own `OnPreRender` overrides it. Disable that controller during playback, re-enable on `OnCutsceneFinished`.
- **Cutscene + dialogue are LOCAL — one machine playing it isn't everyone.** Wrap `Play()` / `StartDialogue()` in `[Rpc.Broadcast]`; don't assume the director or the HUD replicates (they carry no `[Sync]`).
- **`ShotLookAt` silently overrides `ShotAngles`.** If a shot won't obey your typed angles, check whether that index has a `ShotLookAt` target set — the look-at wins (`CinematicsHandlers.cs:354`).
- **The dialogue HUD must live under a `ScreenPanel`.** Both scaffolds' panels (`*Panel`, `*Letterbox`) render nothing unless hosted under a `ScreenPanel` (`add_screen_panel`) — an easy silent no-op.
- **Dialogue lines need a colon to name a speaker.** `"Speaker: text"` splits on the *first* colon; a line with no colon renders with an empty speaker (`ParseLine` `:669`) — fine for narration, surprising if you meant a name.

## Grounded in

- **`create_cutscene_director` / `create_dialogue_system`** — `sbox-bridge-addon/Editor/CinematicsHandlers.cs` (the hand-authored, no-asset path; both LOCAL/visual-only).
- **`list_movies` / `add_movie_player` / `play_movie` / `stop_movie`** — `sbox-bridge-addon/Editor/MovieMakerHandlers.cs` (the keyframed `Sandbox.MovieMaker` path; new in the 2026-07-08 build).

---
**Verify live:** the installed SDK is authoritative — the MovieMaker surface in particular is new and drifting. Confirm members before coding with the bridge's reflection tools: `describe_type Sandbox.MovieMaker.MoviePlayer`, `search_types MovieResource`, `describe_type Sandbox.UI.PanelComponent`, `describe_type CameraComponent`, `search_types Input`. Reflection beats any snippet here if the API has moved.

**See also:** `references/systems/dialogue.md` (the game-mined dialogue/quest/VN patterns — typewriter triggers, modal confirms, branching narrative, per-player objectives), `sbox-api` (exact signatures for `PanelComponent`, `CameraComponent`, `GameResource`, `Rotation`), and `sbox-build-feature` (the screenshot-driven loop to wire and SEE a cutscene run in play mode).
