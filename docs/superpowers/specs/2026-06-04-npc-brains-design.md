# NPC Brains / Gameplay Logic — Design Spec

> **Status:** DESIGN ONLY. No implementation. Feature wave #3 for the s&box Claude Bridge.
> **Date:** 2026-06-04 · **Targets the bridge in this repository** (MCP server `sbox-mcp-server/`, addon `sbox-bridge-addon/Editor/MyEditorMenu.cs`).
> **Concrete test games:** Sasquatched (asymmetric horror, the `bigfoot` project) and RUN (co-op roguelite).

---

## Goal

Give bridge-built NPCs **actual behavior**. The bridge already has the *movement substrate* — `bake_navmesh` / `get_navmesh_path` (verified `scene.NavMesh.BakeNavMesh()` + `GetSimplePath`) and a thin `create_npc_controller` that only does "if target within `DetectRadius`, `_agent.MoveTo(target)`". This wave adds the **decision layer**: a generated behavior state machine (idle/patrol/chase/flee/wander/search) driven by **perception** (line-of-sight + FOV cone, proximity/hearing, last-known-position memory), plus the **authoring** around it: patrol-waypoint placement, spawners/waves, and target/aggro selection.

The design follows the bridge's existing, proven seam: **generate a C# `Component` script into the project** (exactly like `create_npc_controller` / `create_player_controller` / `create_trigger_zone`), then optionally place & wire GameObjects with edit-mode tools. The generated component is the brain; the edit-mode tools place waypoints/spawners and assign references.

**Non-goal (YAGNI):** a full behavior-tree authoring DSL, GOAP, blackboard sharing across NPCs, squad coordination, or a node-graph editor. A finite state machine covers both target games. We can grow toward BTs later if a game demands it.

---

## Why

### The two test games drive every decision

**Sasquatched** — 1 Sasquatch vs 4–6 campers; the Sasquatch is currently **fully player-controlled** (M1 swipe / M2 pounce / T roar / sprint). The headline use case here is an **AI Sasquatch / "Director"** for solo testing, practice bots, or a future PvE mode: it must **patrol** the campground, **detect** a camper by line-of-sight through trees (the map is a forest — LOS occlusion is the whole game), **chase**, **lose the player** when LOS breaks and fall back to **search** around the **last-known position**, and ideally **ambush** (wait near an objective / RV part). "Where did he go" is an explicit design pillar (10% of the tone) — that is literally *lose-LOS → search → give up → re-patrol*, which is the state machine below. Perception must respect occlusion (trace from an eye height, FOV cone) or the horror reads as cheating.

**RUN** — co-op (≤4) roguelite; the spec doesn't yet have enemies, but the build approach is layered and "maximal," and roguelites of this shape want **chase/swarm** mobs (think: things that wake with the bomb, or hazards in the countryside that converge on the loudest/nearest player). The use case here is **spawners + waves** (spawn N over time at points / in escalating waves) feeding **chase** NPCs with **nearest-player aggro** and **flee** when low (or when the bomb is about to blow). RUN is networked third-person; the brain must be **host-authoritative** (see Risks).

### Why now / why it fits the bridge

- The movement half already exists and is verified. The *brain* is the missing half, and it's the single most-requested "make a game" capability after a player controller.
- Behavior is **code**, and the bridge is "excellent at building game systems through conversation" (CHANGELOG 1.2.0 Honesty Note) — exactly where code-gen tools shine and where the screenshot-can't-see-it weakness doesn't bite (a state machine's *correctness* is checked by structure + play-mode behavior, not by a static screenshot).
- Both games are the user's own and are the stated reason for this direction, so we design to them, not to a hypothetical.

---

## Approach: generated C# component vs. runtime config

**Decision: generate a C# `Component` script into the project (primary), with light edit-mode placement/wiring tools around it.** This matches `create_npc_controller`, `create_player_controller`, `create_game_manager`, `create_trigger_zone`, `create_lobby_manager`, etc. — every gameplay-logic scaffold in the bridge today is code-gen, and the dispatch already treats those commands as scene-mutating (play-mode-guarded).

Why code-gen over a runtime config component (one big `BehaviorController` with `[Property]` enums the bridge sets via `set_property`):

| | Generated C# component (chosen) | Runtime-config component |
|---|---|---|
| Matches existing bridge pattern | Yes — identical to `create_npc_controller` | No — would be the only one |
| User can read/tweak/extend the logic | Yes — it's their source, in their repo | No — opaque addon internals |
| Per-game tuning (Sasquatch ambush vs RUN swarm) | Yes — generate variants / states the game needs | Hard — one component must do everything |
| Ships in the user's game without the addon | Yes — plain `Sandbox.Component` | No — depends on the addon being installed |
| Versioning / API drift | Lower — generated against current SDK, user owns it | Higher — addon must track SDK forever |
| Verifiability | Structure via `read_file`/`find_in_project`/`describe_type`; behavior via play mode | Same behavior story, worse inspectability |

**Hybrid where it earns it:** the *generated component* exposes everything as `[Property]` fields (move speed, sight range, FOV degrees, hearing radius, waypoint list, eye height, give-up time, flee-health, target tag). That means after generation, the bridge can **tune** the brain with the existing `set_property` / `add_component_with_properties` tools and **wire references** (`set_prefab_ref` for a spawn prefab, the new `assign_patrol_route` for waypoints) without regenerating. Best of both: logic is owned code; tuning is data the bridge can drive.

**The states/perception live in ONE generated component** (`NpcBrain`), not five components. A single `enum State` + `switch` in `OnUpdate` is the most navigable, most debuggable shape for both Claude and the user, and avoids cross-component ordering hazards. Perception is a region in that same file (small private helpers), not a separate component, so there's no execution-order coupling.

---

## The behavior state-machine shape (generated component)

`create_npc_brain` generates a `Sandbox.Component` roughly like the sketch below. **This is a design sketch, not final code** — every s&box API marked `‹verify›` must be confirmed at implementation time with `describe_type` / `get_method_signature` (see Risks). The shape, the `[Property]` surface, and the state/transition table are the contract; the exact API calls inside are subject to reflection.

```csharp
using Sandbox;
using System.Linq;

public sealed class NpcBrain : Component   // class name = SanitizeIdentifier(name)
{
    public enum BrainState { Idle, Patrol, Wander, Chase, Search, Flee, Ambush }

    // ── Tunables (all [Property] so the bridge can set_property later) ──
    [Property] public BrainState StartState { get; set; } = BrainState.Patrol;
    [Property] public float MoveSpeed     { get; set; } = 130f;
    [Property] public float ChaseSpeed    { get; set; } = 200f;

    // Perception
    [Property] public float SightRange    { get; set; } = 1500f;
    [Property] public float FovDegrees    { get; set; } = 110f;   // full cone angle
    [Property] public float EyeHeight     { get; set; } = 64f;    // trace origin above feet
    [Property] public float HearingRadius { get; set; } = 600f;   // proximity / noise pickup
    [Property] public string TargetTag    { get; set; } = "player";

    // Memory / timing
    [Property] public float GiveUpTime    { get; set; } = 6f;   // search this long after losing LOS
    [Property] public float SearchRadius  { get; set; } = 400f; // wander radius around last-known
    [Property] public float WaypointStopDistance { get; set; } = 80f;

    // Flee
    [Property] public bool  CanFlee       { get; set; } = false;
    [Property] public float FleeHealthFrac{ get; set; } = 0.25f;

    // Patrol route (placed + assigned by assign_patrol_route, or hand-set in editor)
    [Property] public System.Collections.Generic.List<GameObject> Waypoints { get; set; } = new();

    // ── Runtime state ──
    public BrainState CurrentState { get; private set; }
    private GameObject _target;
    private Vector3 _lastKnownPos;
    private TimeSince _timeSinceSeen;
    private int _waypointIndex;
    private NavMeshAgent _agent;        // ‹verify member surface›

    protected override void OnStart()
    {
        _agent = GetOrAddComponent<NavMeshAgent>();   // ‹verify NavMeshAgent exists + ctor side effects›
        CurrentState = StartState;
    }

    protected override void OnUpdate()
    {
        // Host-authoritative: in a networked game only the owner/host should think.
        if ( IsProxy ) return;                         // ‹see Risks: networking›

        Perceive();          // updates _target / _lastKnownPos / _timeSinceSeen
        Think();             // transition table
        Act();               // drive the agent for CurrentState
    }

    // ── Perception (detail in the Perception section) ──
    private void Perceive() { /* LOS cone + proximity → sets _target, _lastKnownPos, _timeSinceSeen */ }

    // ── Transition table (data-driven, readable) ──
    private void Think()
    {
        bool canSee = _target.IsValid() && _timeSinceSeen < 0.1f;

        if ( CanFlee && ShouldFlee() ) { CurrentState = BrainState.Flee; return; }

        switch ( CurrentState )
        {
            case BrainState.Idle:
            case BrainState.Patrol:
            case BrainState.Wander:
            case BrainState.Ambush:
                if ( canSee ) CurrentState = BrainState.Chase;
                break;

            case BrainState.Chase:
                if ( !canSee && _timeSinceSeen > 0.25f ) CurrentState = BrainState.Search;
                break;

            case BrainState.Search:
                if ( canSee ) CurrentState = BrainState.Chase;
                else if ( _timeSinceSeen > GiveUpTime ) CurrentState = StartState;  // give up → resume
                break;

            case BrainState.Flee:
                if ( !ShouldFlee() ) CurrentState = StartState;
                break;
        }
    }

    private void Act() { /* per-state: MoveTo waypoint / target / lastKnown / away-from-threat */ }
    private bool ShouldFlee() { /* health-comp lookup ‹verify› or external flag */ return false; }
}
```

**State catalogue (what each does, why each game needs it):**

| State | Behavior | Sasquatched | RUN |
|---|---|---|---|
| **Idle** | Stand still, perceive only | Ambusher waiting | Dormant mob |
| **Patrol** | Walk `Waypoints` in order/ping-pong | Sasquatch sweeping the campground | Patrolling hazard |
| **Wander** | Pick random navmesh points near home | "He's roaming" feel | Aimless drifters |
| **Chase** | `MoveTo(target)` at `ChaseSpeed` while LOS holds | The hunt | The swarm |
| **Search** | Go to `_lastKnownPos`, then wander `SearchRadius` until `GiveUpTime` | "Where did he go" pillar | Mob investigates last noise |
| **Flee** | Move away from threat (low health / bomb timer) | rare | wounded/panicked mobs |
| **Ambush** | Idle near an assigned point until target enters `SightRange`, then Chase | Wait by an RV part | Camp a chokepoint |

The `behavior` tool param selects a **preset** that sets `StartState` and toggles features, so non-coders get the right brain in one call: `patrol`, `guard` (= Ambush around spawn), `hunter` (patrol→chase→search, Sasquatch), `swarm` (wander/idle→chase nearest, RUN), `skittish` (chase but flee on low health). The generated file is the same; the preset just changes defaults and which states are reachable.

---

## Perception design

Perception is the part that must be **correct**, **occlusion-aware**, and **cheap** (runs every `OnUpdate` per NPC). It lives as private helpers in the generated component.

**1. Candidate gathering.** Find potential targets by tag. Design: `Scene.GetAllComponents<…>()` filtered by `GameObject.Tags.Has(TargetTag)`, OR a proximity prefilter via the same trace machinery `physics_overlap` uses (`scene.Trace.Sphere(HearingRadius, pos, pos).RunAll()`). `‹verify›` the cheapest correct way to enumerate tagged players from a component at runtime — likely `Scene.GetAllComponents<T>()` then a `.Tags` filter, but confirm with `describe_type "Scene"` / `describe_type "GameObject"`.

**2. Line-of-sight (the core).**
- Origin = `WorldPosition + Vector3.Up * EyeHeight`. Target point = candidate eye/center.
- **FOV cone gate first (cheap):** `Vector3.Dot( WorldRotation.Forward, (targetPos - eye).Normal ) >= cos(FovDegrees/2 in rad)`. Use **`MathX`**, never `MathF`/`System.Math` (sandbox forbids them — CLAUDE.md). Need `‹verify›` the exact `MathX` trig available (`MathX.DegreeToRadian`? otherwise multiply by `(MathF…)` — no, use the constant `MathX` exposes or `* 0.0174533f`). Confirm with `describe_type "MathX"`.
- **Range gate:** distance ≤ `SightRange`.
- **Occlusion trace (authoritative):** `scene.Trace.Ray(eye, targetPos).Ignore(self).Run()` (the verified `RaycastHandler` pattern: `.Hit`, `.GameObject`, `.HitPosition`). If the trace hits the target's GameObject (or nothing between) → visible; if it hits world/tree first → blocked. `‹verify›` the right `.Ignore(...)` / `.WithoutTags(...)` builder on `SceneTrace` so the NPC doesn't occlude itself, via `describe_type "SceneTrace"`.

**3. Hearing / proximity.** Two flavors:
- **Passive proximity:** any candidate within `HearingRadius` is "heard" regardless of LOS (sets `_lastKnownPos` but NOT `_target`-as-seen — so the NPC investigates, doesn't instantly aggro). This is what makes Search feel smart.
- **Active noise events (stretch, Phase 2):** a static `NpcBrain.ReportNoise(Vector3 pos, float radius)` the *game* calls (Sasquatch hears a flashlight click / RV engine; RUN mobs hear a gunshot/vehicle). NPCs within `radius` set `_lastKnownPos = pos` and switch to Search. This is a tiny, high-leverage hook for both games and is pure C# (no new bridge tool needed to *use* it — the user/Claude wires the call where the noise happens). Document it; don't build extra tooling.

**4. Last-known-position memory.** On LOS true: `_target = candidate; _lastKnownPos = candidate.WorldPosition; _timeSinceSeen = 0`. On LOS false: keep `_target` ref but stop refreshing `_lastKnownPos`; `_timeSinceSeen` grows. Search drives to `_lastKnownPos`; after `GiveUpTime` with no re-acquire, drop target and resume `StartState`. This single `_lastKnownPos` + `TimeSince` pair is the entire "lose the player" mechanic.

**Tunable contract (so the bridge can adjust perception without regenerating):** `SightRange`, `FovDegrees`, `EyeHeight`, `HearingRadius`, `TargetTag`, `GiveUpTime`, `SearchRadius` are all `[Property]`. A `tune_npc_perception` convenience tool (below) is just a friendly wrapper over `set_property` on these fields, with sane bundles.

---

## Tool-by-tool design

New MCP module: **`sbox-mcp-server/src/tools/npcbrains.ts`** → `registerNpcBrainTools(server, bridge)`, added to `index.ts` imports + the registration block (mirrors `registerNavigationTools`). New C# handlers in `MyEditorMenu.cs`, each `Register(...)`'d in `RegisterHandlers()`, and every **scene-mutating** one added to `_sceneMutatingCommands` (so play mode is refused with the standard message). Each TS tool follows the house shape: `server.tool(name, desc, zodParams, async p => { const res = await bridge.send(name, p); if(!res.success) return errText; return json(res.data); })`.

> **Verification convention used below.** "Structural (edit mode)" = the generated file exists / compiles / properties read back, checkable with `read_file`, `find_in_project`, `get_compile_errors`, `describe_type` on the new type, `get_all_properties`, `get_navmesh_path`. "Behavioral (play mode)" = the NPC actually chases/loses/searches — only observable by entering play mode and watching (`start_play`, then `get_runtime_property` on `CurrentState`, and screenshots over time). The bridge **cannot see real chasing in a single static screenshot**; flag this everywhere it matters.

---

### Phase 1 (the high-value subset)

#### 1. `create_npc_brain`  *(code-gen; scene-mutating)*
The headline tool. Generates the `NpcBrain` component above.

**Params**
- `name?: string` — class/file name (default `NpcBrain`). Sanitized via `SanitizeIdentifier`.
- `directory?: string` — subdir under project root (default `Code`), path-guarded via `TryResolveProjectPath`.
- `behavior?: enum("patrol","guard","hunter","swarm","skittish")` — preset (default `hunter`). Sets `StartState` + reachable states + flee toggle.
- `targetTag?: string` (default `"player"`).
- `moveSpeed?`, `chaseSpeed?`, `sightRange?`, `fovDegrees?`, `eyeHeight?`, `hearingRadius?`, `giveUpTime?`, `searchRadius?: number` — initialize the matching `[Property]` defaults in the emitted file.
- `canFlee?: boolean`, `fleeHealthFrac?: number`.
- `networked?: boolean` (default `true`) — when true, emit the `if ( IsProxy ) return;` guard and a `// host-authoritative` note (correct for Sasquatched/RUN); when false, omit it for solo/edit-scene testing.

**C# handler shape** — `CreateNpcBrainHandler : IBridgeHandler`, byte-for-byte the structure of `CreateNpcControllerHandler`: resolve path → refuse if `File.Exists` → `Directory.CreateDirectory` → `className = SanitizeIdentifier(...)` → build the source string from a template with the preset/param substitutions → `File.WriteAllText` → return `{ created:true, path, className, behavior, states:[…], properties:[…] }`. The template is the sketch above with the `switch` arms and perception helpers filled in.

**Returns** `{ created, path, className, behavior, statesIncluded[], propertyNames[], note:"add NavMeshAgent is automatic via GetOrAddComponent; bake_navmesh + a navmesh-walkable scene required for movement; assign Waypoints with assign_patrol_route; enter play mode to see chase/search" }`.

**Verification** — Structural: `read_file` the path; `get_compile_errors` after `trigger_hotload`; `describe_type "<className>"` to confirm the `[Property]` surface and `CurrentState`. Behavioral: see `simulate_npc_perception` (#7) for an **edit-mode** sanity check, and play mode for the real thing.

**Play-mode guard** — Yes (writes a file). Add `"create_npc_brain"` to `_sceneMutatingCommands`.

---

#### 2. `place_patrol_route`  *(scene-mutating)*
Place a set of waypoint GameObjects (empties, tagged) and group them under a parent, so a patrol route is authorable in one call.

**Params**
- `points: Vector3[]` — ordered world positions (required, ≥2).
- `name?: string` (default `PatrolRoute`).
- `tag?: string` (default `waypoint`).
- `snapToGround?: boolean` (default `true`) — drop each point onto the surface via the verified raycast-down pattern (same as `snap_to_ground`/`RaycastHandler`), so waypoints sit on the navmesh, not floating.
- `parentId?: string` — existing parent GUID; else create a `PatrolRoute` empty as parent.

**C# handler** — `PlacePatrolRouteHandler`: for each point, `scene.CreateObject(true)`, set `Name`, `WorldPosition` (optionally a downward `scene.Trace.Ray(pt+up, pt-down).Run()` to snap), add the tag, `SetParent(routeGo)`. Returns the route parent GUID + ordered child GUIDs.

**Returns** `{ routeId, waypointIds:[…], count }`.

**Verification** — Structural: `get_scene_hierarchy rootId=routeId`; `screenshot_from` the route centroid to eyeball spacing; `get_navmesh_path` between consecutive waypoints to **prove the route is navmesh-connected** (high-value: catches "patrol point in a wall"). Behavioral: only matters once assigned + playing.

**Play-mode guard** — Yes (creates objects). Add to `_sceneMutatingCommands`.

---

#### 3. `assign_patrol_route`  *(scene-mutating)*
Wire a placed route (or an arbitrary GUID list) into an `NpcBrain.Waypoints` list on a target NPC.

**Params**
- `npcId: string` — GUID of the GameObject holding the `NpcBrain` (or any component with a `List<GameObject>` waypoint property).
- `waypointIds: string[]` — ordered GUIDs (e.g. from `place_patrol_route`), **or**
- `routeId: string` — a route parent GUID whose children (in hierarchy order) become the waypoints.
- `property?: string` (default `Waypoints`) — the list property name, for non-default brains.

**C# handler** — `AssignPatrolRouteHandler`: resolve the NPC GO + its component (find the component exposing `property` as `List<GameObject>` via `Game.TypeLibrary` like the inline `set_prefab_ref` does), resolve each waypoint GUID via `scene.Directory.FindByGuid`, build a `List<GameObject>`, `prop.SetValue(comp, list)`. This is the list-of-GameObject-references case that plain `set_property` can't express — same justification `set_prefab_ref` had for single refs.

**Returns** `{ assigned:true, npcId, property, count }`.

**Verification** — Structural: `get_all_properties` on the NPC's component to read back the count (refs may serialize as GUIDs/handles — `‹verify›` how `List<GameObject>` round-trips through `get_property`; if opaque, return the count from the handler and rely on play-mode). Behavioral: play mode — NPC walks the points.

**Play-mode guard** — Yes (mutates a component). Add to `_sceneMutatingCommands`.

---

#### 4. `create_npc_spawner`  *(code-gen; scene-mutating)*
Generate a spawner `Component` that instantiates an NPC prefab over time / in waves at points. This is RUN's "swarm" backbone and Sasquatched's "spawn the AI Sasquatch at round start."

**Params**
- `name?: string` (default `NpcSpawner`), `directory?`.
- `mode?: enum("continuous","waves","burst")` (default `waves`).
- `count?: number` — per-wave or total (default 5).
- `interval?: number` — seconds between spawns (continuous) or between waves (default 8).
- `waveCount?: number` — number of waves (waves mode; default 3).
- `waveGrowth?: number` — multiply `count` each wave (default 1.0; >1 = escalating).
- `radius?: number` — random spawn radius around each spawn point (default 200).
- `maxAlive?: number` — cap concurrent NPCs (default 12) — important so RUN doesn't melt.
- `networked?: boolean` (default `true`) — emit `go.NetworkSpawn()` (host-only, try/catch — the verified solo-safe idiom from the networking note) vs a plain spawn.

**Generated component shape** — `[Property] GameObject NpcPrefab; [Property] List<GameObject> SpawnPoints;` + the tunables above; `OnStart`/`OnUpdate` (or `OnFixedUpdate`) with a `TimeSince _lastSpawn`, a live-count check (`Scene`-query or a tracked list with `OnDestroy` decrement), and `Clone`/`NetworkSpawn` of `NpcPrefab` at a random point within `radius`. `IsProxy`/host guard for networked. `‹verify›` `GameObject.Clone(...)` overloads and `NetworkSpawn()` signature via `describe_type "GameObject"`.

**C# handler** — `CreateNpcSpawnerHandler`, same code-gen skeleton as `CreateNpcControllerHandler`.

**Returns** `{ created, path, className, mode, propertyNames:["NpcPrefab","SpawnPoints",…], note:"set NpcPrefab via set_prefab_ref; add spawn points with place_patrol_route (reused) or set SpawnPoints; networked spawns are host-only" }`.

**Verification** — Structural: `read_file` + `get_compile_errors`. Behavioral: play mode — count GameObjects over time (`get_scene_hierarchy` deltas), watch `maxAlive` hold.

**Play-mode guard** — Yes (writes a file). Add to `_sceneMutatingCommands`.

---

### Phase 2 (after Phase 1 proves out)

#### 5. `tune_npc_perception`  *(scene-mutating; convenience over `set_property`)*
Friendly bundle for adjusting a brain without regenerating. Accepts any of `sightRange`/`fovDegrees`/`eyeHeight`/`hearingRadius`/`giveUpTime`/`searchRadius`/`moveSpeed`/`chaseSpeed`/`targetTag`/`canFlee`/`fleeHealthFrac` and applies each as a `set_property` on the NPC's `NpcBrain`. Also accepts `preset?: enum("keen","oblivious","relentless","skittish")` that sets a coherent bundle (e.g. `keen` = wide FOV + long range + short give-up; `oblivious` = narrow FOV + short range + long give-up — good for a comedic Sasquatch). **Handler:** thin loop over the inline `set_property` machinery. **Returns** `{ updated:[field…], npcId }`. **Verification:** `get_all_properties` reads the new values back (numbers/strings round-trip cleanly, unlike GameObject refs). **Play-mode guard:** yes.

> *Rationale for Phase 2, not 1:* it's pure ergonomics — `create_npc_brain` already takes all these as generation params, and `set_property` already exists. Ship the brain first; add the sugar once we know which knobs people actually turn.

#### 6. `set_npc_target`  *(scene-mutating; aggro/targeting authoring)*
Force or constrain target selection. **Params:** `npcId`, plus one of: `targetId` (lock to a specific GameObject — e.g. always hunt the host), `mode: enum("nearest","nearest_visible","by_tag")` (default `nearest_visible`), `targetTag?`. Writes `Target`/targeting `[Property]` on the brain (the generated component gets an optional `[Property] GameObject ForcedTarget` and a `[Property] TargetMode Mode`). **Why:** Sasquatched might want "hunt whoever has the RV part"; RUN wants "nearest player." Nearest-player logic lives *in* the component (`Perceive` already enumerates candidates — pick min-distance among visible). This tool just sets the policy + optional hard target. **Handler:** `set_property` on `Mode` and `set_prefab_ref`-style ref-set on `ForcedTarget`. **Verification:** structural read-back of `Mode`; behavioral in play. **Play-mode guard:** yes.

#### 7. `simulate_npc_perception`  *(READ-ONLY; the verifier)*
The answer to "the bridge can't see a chase in a screenshot." A **read-only edit-mode** query that evaluates the perception math *right now* without play mode: given an `npcId` (reads its `NpcBrain` `SightRange`/`FovDegrees`/`EyeHeight`/`TargetTag` + transform) and either a `targetId` or a `point`, it runs the **same LOS check the component would** — FOV dot-product gate + range + `scene.Trace.Ray(eye, target).Ignore(npc).Run()` — and reports the result and *why*. **Returns** `{ canSee:bool, inRange:bool, inFov:bool, losBlocked:bool, blockedBy:{id,name}?, distance, angleDeg, eye:{x,y,z} }`. **Why this is the keystone verifier:** it makes the perception layer **checkable in edit mode** (no play mode, no flaky screenshot timing) — Claude can place the Sasquatch, place a "camper" behind a tree, and *confirm the tree blocks LOS* structurally. It reuses the exact verified trace + the same FOV math the generator emits, so a green here means the generated brain will agree. **Handler:** `SimulateNpcPerceptionHandler` — pure query, mirrors `RaycastHandler`/`PhysicsOverlapHandler`. **Verification:** it *is* verification. **Play-mode guard:** none (read-only; safe in play too, like `raycast`).

> *Rationale:* Phase 2 only because it depends on the brain's property names existing; but it's the highest-value verification tool in the wave and should land immediately after #1.

---

### Tools considered and CUT (YAGNI)

- **Behavior-tree / node-graph authoring** — out of scope; FSM covers both games. Revisit only if a game needs interrupts/parallels an FSM can't express.
- **`create_npc_brain` as a runtime-config component** — rejected (see Approach table).
- **Squad/blackboard coordination, flanking, cover-point selection** — neither game needs it yet; large surface, high API risk.
- **A dedicated `add_navmesh_agent` tool** — unnecessary; the generated brain does `GetOrAddComponent<NavMeshAgent>()`, and `add_component_with_properties` already exists for manual cases.
- **A separate `hearing_event` tool** — the static `NpcBrain.ReportNoise(...)` hook is pure C# the game calls directly; documenting it beats wrapping it.

---

## Risks & unknowns (+ API verifications needed at implementation time)

These are the must-confirm-with-`describe_type` items. **None should be guessed from training data** — the bridge's own rule (CLAUDE.md: "reflection is the source of truth").

1. **`NavMeshAgent` member surface — HIGH.** The existing `create_npc_controller` already calls `GetOrAddComponent<NavMeshAgent>()` and `_agent.MoveTo(target.WorldPosition)`, so the type and `MoveTo` are *probably* real — but the brain needs more: setting **desired speed** (does `NavMeshAgent` own `MaxSpeed`/`Acceleration`, or do we set the GO velocity?), reading whether it **reached** a destination (a `Velocity`/`IsNavigating`/distance check?), and **stopping**. Verify with `describe_type "NavMeshAgent"` + `get_method_signature` for `MoveTo`. *Fallback if `NavMeshAgent` is thin:* drive movement directly from `scene.NavMesh.GetSimplePath(pos, dest)` (already verified via `GetNavMeshPathHandler`) and step `WorldPosition` along the returned points — slower but fully verified today.

2. **Networking authority for the brain — HIGH (correctness, both games).** Sasquatched and RUN are networked; if every client's NPC copy "thinks," they fight. The generated brain guards with `if ( IsProxy ) return;` — but `IsProxy` is `true` for everything in a **no-session solo playtest** (the documented gotcha), so a solo test of the brain would do nothing unless a session exists (`NetworkHelper`/`CreateLobby`). Design response: `networked:false` generation option (no guard) for edit-scene/solo iteration; document that networked brains need a host session; consider `[Sync] CurrentState` so proxies *animate* correctly while only the host decides. `‹verify›` `IsProxy`, `[Sync]`, and `NetworkSpawn()` semantics (the RUN note already confirms these idioms compile — re-confirm signatures).

3. **NavMesh exists only in play/baked context + edit-mode movement is invisible — MEDIUM.** `bake_navmesh` works in edit mode (verified), and `get_navmesh_path` queries it — but a `NavMeshAgent` actually *moving* an NPC is a **runtime** behavior. So "does it chase" is **play-mode-only** and **not screenshot-friendly in a single frame**. Mitigations baked into this spec: (a) `simulate_npc_perception` makes the *perception* half verifiable in edit mode; (b) `get_navmesh_path` between waypoints / NPC→target proves *reachability* in edit mode; (c) `get_runtime_property "CurrentState"` + `screenshot_orbit`/timed `screenshot_from` during `start_play` for the behavior half. Set expectations: structural green is achievable headless; behavioral confirmation needs play mode and the user's eyes for the "feel."

4. **Candidate enumeration cost & API — MEDIUM.** Running `Scene.GetAllComponents<T>()` + tag filter every `OnUpdate` per NPC could be wasteful with many NPCs (RUN swarms). `‹verify›` the right enumeration API (`Scene.GetAllComponents` vs `Scene.GetAll` vs a tag index) and whether to throttle perception (e.g. every N ticks via `TimeSince`). Design already favors a `HearingRadius` sphere prefilter (trace) before LOS traces.

5. **`MathX` trig / angle constants — MEDIUM.** FOV needs `cos(half-angle)` and degree→radian. `MathF`/`System.Math` are banned in the sandbox. `‹verify› describe_type "MathX"` for the available trig/clamp/conversion (`MathX.DegreeToRadian`? `MathX.Clamp` is confirmed). Worst case, multiply by the literal `0.0174532925f` and compare dot-products without an explicit acos (compare `dot >= cos(half)` — only needs `cos`, and we can precompute `cos` from a small helper or compare via the half-angle differently). Keep the math to dot-products to minimize trig dependence.

6. **`SceneTrace` builder surface (`Ignore`/`WithoutTags`/`UseHitboxes`) — MEDIUM.** The LOS trace must ignore the NPC's own colliders and ideally hit characters' hitboxes. `RaycastHandler` only uses `.Ray(a,b).Run()`. `‹verify› describe_type "SceneTrace"` for `.IgnoreGameObject`/`.Ignore`/`.WithoutTags`/`.WithTag`. Without self-ignore, the NPC may "see" itself.

7. **`List<GameObject>` property round-trip through the bridge — LOW/MEDIUM.** `assign_patrol_route` sets a `List<GameObject>`; reading it back via `get_property`/`get_all_properties` may serialize as handles/GUIDs or as `[unsupported]`. `‹verify›` how the existing property serializer handles GameObject refs and lists; if unreadable, the handler returns the assigned count and we lean on play-mode + `get_scene_hierarchy` for confirmation (acceptable).

8. **Health/flee coupling — LOW.** `ShouldFlee()` needs a health source. Games differ (Sasquatched campers have HP components; RUN TBD). Design keeps `Flee` driven by a generic `[Property] float CurrentHealthFrac` the game sets, OR an overridable virtual `ShouldFlee()` — *don't* hard-couple to any project's health component. Document the seam.

9. **Edit-scene `OnUpdate` execution — LOW.** Generated brains run in **play mode**; in the edit scene, components don't tick (which is why perception is verified via the *separate* `simulate_npc_perception` query, not by watching the component). No risk if we don't claim edit-mode ticking.

---

## Phasing

**Phase 1 — the brain + authoring (ship together):**
1. `create_npc_brain` (the FSM component with perception baked in) — the centerpiece.
2. `place_patrol_route` + 3. `assign_patrol_route` — make patrol authorable end-to-end.
4. `create_npc_spawner` — RUN's swarm / Sasquatched round-start spawn.

Phase 1 delivers a complete vertical slice for **both** games: generate a `hunter` brain for the Sasquatch, place a patrol route through the campground, assign it, bake navmesh, play, and watch patrol→chase→search; and generate a `swarm` brain + a `waves` spawner for RUN. Verifiable structurally end-to-end; behaviorally in play.

**Phase 2 — verification + tuning ergonomics:**
7. `simulate_npc_perception` (land this *first* in Phase 2 — it's the edit-mode verifier that de-risks everything) → 5. `tune_npc_perception` → 6. `set_npc_target`.

**Phase 3 — only if a game asks:** noise-event tooling beyond the documented `ReportNoise` hook; per-state animation hooks tying `CurrentState` to `set_animgraph_param` (v1.6.0); behavior-tree escalation. Not committed.

**Implementation order within Phase 1 (de-risk first):** before writing the generator, run the `describe_type` checks in Risks #1, #5, #6 against the live editor and **pin the confirmed API into the template**. Generate the simplest `hunter` brain, `trigger_hotload`, `get_compile_errors` until clean, then `start_play` and confirm chase/search by eye. Only then add the spawner and presets.

---

## Open questions for the user

1. **Is the AI Sasquatch a real product goal** (PvE mode / practice bots / solo testing) or only a dev convenience? It changes how much polish the `hunter`/`ambush` states deserve and whether `[Sync] CurrentState` (for client-side animation of the AI) is in-scope now.
2. **RUN enemies — are they in the near-term plan?** RUN's current spec has no mobs. If swarms are coming, `create_npc_spawner` + `swarm` brain are Phase 1; if not, we could defer the spawner and ship just the brain + patrol for Sasquatched first.
3. **Animation coupling now or later?** Should the brain drive Citizen animation per state (idle/walk/run/attack) via v1.6.0 `set_animgraph_param` in Phase 1, or keep the brain movement-only and wire animation separately? (Recommend: movement-only first; animation hook in Phase 3.)
4. **Attack/damage in scope?** The brain decides *to* chase; does this wave also generate an **attack** action (melee trace on contact, à la Sasquatched's swipe) or stop at positioning and leave the hit to the game's existing combat? (Recommend: positioning only this wave; reuse the game's attack.)
5. **Health/flee source:** OK to expose `Flee` via a generic `CurrentHealthFrac` `[Property]` (game sets it) plus an overridable `ShouldFlee()`, rather than integrating any specific health component?
6. **Patrol style default:** ping-pong (A→B→C→B→A) or loop (A→B→C→A) as the default for `place_patrol_route`/the brain? (Recommend: loop; expose a `[Property] bool PingPong`.)
7. **Should `create_npc_brain` also create+place the NPC GameObject** (spawn a Citizen, add the brain, add a collider) as a one-shot "make me an enemy," or stay a pure script generator (current `create_npc_controller` behavior) and let the user compose? (Recommend: pure generator + a documented 3-call recipe; optionally a thin `spawn_npc` composite later.)
