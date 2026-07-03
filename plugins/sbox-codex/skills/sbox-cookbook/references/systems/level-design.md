# Level Design & Mapping (modern s&box)
How 27 shipped modern s&box games actually build their playspaces — the transferable techniques for composing, lighting, collisioning, and budgeting a level via the GameObject/Component/Scene model (no Hammer required).

## Mental model: four ways to source a level's geometry
Every game picks **one container** for its static world, then layers gameplay on top of it. Pick by how much hand-authored geometry you truly need:

| Container | What it is | Best for |
|---|---|---|
| **Prefab-kit scene graph** | A small kit of reusable `.prefab` pieces instanced 100s of times via `__Prefab` refs | Arenas, courses, RP maps, "ship N maps from one kit" |
| **MeshComponent brushwork** | `Sandbox.MeshComponent` (PolygonMesh/CSG) boxes drawn + textured *in-editor*, collision baked in | One-off interiors, same-y corridors, fast blockout |
| **Compiled Hammer `.vmap`** | `Sandbox.MapInstance { MapName, EnableCollision, UseMapFromLaunch }` loads a baked map; scene carries only the gameplay overlay | Fixed maps wanting baked lighting/GI |
| **Runtime-generated** | Scene is a *bootstrap* (a controller + generator component); code builds geometry from data (voxels, WAD, noise) | Voxel/dig worlds, ports, procedural roguelites |

**The universal split:** static *shell* and interactive *overlay* are independent layers, versioned separately. The map can be a black box (Hammer or generator); doors/economy/spawns/hazards are prefabs/components you author on top (rp-urban: swap the `.vmap`, keep the overlay).

### How a level decomposes into the scene graph
A clean level is a shallow tree of named groups:
```
Scene
├─ Environment   (DirectionalLight "Sun" + SkyBox2D + fog + EnvmapProbe)
├─ LEVEL/Manager (one controller Component per level/room — the game brain)
├─ Geometry      (MeshComponent blockout  OR  MapInstance  OR  prefab-kit instances)
├─ Props         (ModelRenderer/Prop kit, hand-placed, named "thing (3)")
├─ Gameplay      (trigger Components, spawn markers, checkpoints — NOT meshes)
└─ Spawns        (SpawnPoint markers)
```
**Never build a room inside a `.scene` you intend to reuse — build it as a `.prefab` and reference it** (social-hub: each room is one `prefabs/levels/*.prefab`, the scene is just a harness). This makes rooms diffable, swappable, and lets the shared shell stay identical everywhere. Ship a "duplicate-this" blockout scene so every new room starts identical.

### Three structural patterns worth stealing
- **One shell, many swappable rooms** (social-hub elevator): a persistent hub shell + a library of self-contained level prefabs, each with **one controller Component** for its rules, all sharing the round framework. Adding a room = drop a prefab, register it, copy the template scene. The cheapest way to ship variety: theme = single prop pack + single post-FX toggle per room.
- **Per-map data-tuning Component** (survival goders): one reusable Component on the scene root holds the map's difficulty knobs (`FinalFloodHeight`, `CustomFloodSpeed`, `MaxVolcanoAngle`); the same codebase reskins difficulty by map. Steal the *pattern* — scene-level tuning data, not hardcoded constants.
- **Kit-catalog / test-assemble scene** (obstacle-course): a dedicated sandbox scene that lays out + hero-lights every kit module in isolation, used to preview pieces before they go into levels. Clone it as your kit's living catalog.

## Composition & blockout
**Decompose, don't model the whole world.** The highest-leverage pattern is the **prefab kit**: organize `prefabs/objects/{size}/{category}/` (construction/fences/containers/nature/decals…), instance heavily, reuse across maps (rp-urban: `fence_0` appears 97× / 66× / 15× across three maps). For repeated runs (floor/wall strips) author **macro-prefabs** (a `_x5` prefab bundling 5 tiles) so the scene tree stays shallow.

**Two-tier kit = greybox then reskin.** Ship a stripped grey kit *and* a pretty art kit that share the **same behavior components**; blockout the whole space with the grey kit first, swap meshes later — behavior never changes (obstacle-course: a 6-piece `platform_modules` grey kit mirrors the detailed art kit). Where no kit exists yet, the proven prototype is **`models/dev/box.vmdl` + a BoxCollider** on a terrain/plane (rp-urban SandboxWars ships a real arena as literally 53 dev-boxes + a manager).

**Modular sets follow base + edge + corner.** Streets/floors are a tile kit (`_100/_200/_400` length variants + edge caps + corners) snapped on a grid — not a heightmap (shop-interiors: a sidewalk kit instanced thousands of times). Author by snapping tiles end-to-end on a fixed grid step; this maps directly onto the bridge's `grid_duplicate` / `place_along_path` / `distribute_objects` tools.

**MeshComponent is the in-editor blockout workhorse.** For an interior, draw Floor/Wall/Roof as `MeshComponent` and texture per-face — it ships `Collision: "Mesh"` for free and needs no DCC round-trip (shop-interiors EMG; backrooms is *111* MeshComponents + one tiling material = the whole "endless identical room" with zero model kit). CSG "Block" meshes are also the cheapest way to carve a playable volume with instant collision before any props go in (social-hub KOTH/glass-bridge are almost pure Block).

```csharp
// Behavior is a tiny, stateless, data-driven Component placed on the kit mesh.
// Endpoints/pivots are EMPTY marker GameObjects — drag-to-author in the editor.
public sealed class MovingPlatform : Component
{
    [Property] public GameObject PointA { get; set; }   // marker GO, not world coords
    [Property] public GameObject PointB { get; set; }
    [Property] public float MovementSpeed { get; set; } = 130f;
    [Property] public float PauseTime { get; set; } = 1f;
}
```
This mesh-art / stateless-component split is why one component type recurs dozens of times in a level with different tuning (obstacle-course: a conveyor component ×17 in a single level). Reference endpoints/pivots/shot-origins as **empty marker GOs**, never baked coordinates.

## Lighting & atmosphere
Modern s&box leans on **`SkyBox2D` with `SkyIndirectLighting = true` as the ambient/bounce source — no baked lightmaps required.** The reusable outdoor recipe, copyable verbatim:

```csharp
// Environment node, authored once, dropped in every scene.
var sun = env.AddComponent<DirectionalLight>();
sun.LightColor = new Color(6f, 4.94f, 3.66f);   // HDR >1, WARM
sun.SkyColor   = new Color(0.42f, 0.60f, 0.76f); // COOL fill — the warm-sun/cool-shadow split
sun.Shadows = true;                              // ShadowCascadeCount 4, SplitRatio ~0.91
sun.FogMode = FogMode.Enabled;                   // sun drives the fog (no separate volume needed)

var sky = env.AddComponent<SkyBox2D>();          // + SkyIndirectLighting = true
```
- **Warm key + cool sky = free color contrast**, used by nearly every outdoor game (tycoon, rp-urban, obstacle-course).
- **Distance fog**: `CubemapFog` pointing at the *same skybox material* so aerial perspective matches the sky (rp-urban: `EndDistance 22000`, height fog via `HeightStart`). `GradientFog` for height-banded murk (survival sdiver: deep-navy `0.012,0.051,0.149,0.9`, End 2048 — sells "deep underwater" in one component).
- **Indoor stack** is point/spot-light-heavy + baked GI: SkyBox2D (warm tint) + a "Sun" DirectionalLight (fog on, 4 cascades) + `VolumetricFogVolume` (god-rays) + `IndirectLightVolume` (baked DDGI under `scenes/<scene>_data/ddgi/`) + `EnvmapProbe` + many small Point/Spot lights for local pools (shop-interiors). **Author one light, duplicate it down the corridor** — params are copy-pasted identically (social-hub: a spotlight repeated 210×).
- **Reflections need a bake.** A placed `EnvmapProbe` (`Projection: Box`, `UpdateStrategy: OnEnabled`) captures *nothing* until baked — bake once for a static hub since geometry never moves (tycoon). Same for `IndirectLightVolume`. Via the bridge: `add_envmap_probe` then `bake_reflections`.
- **Atmosphere is a swappable post-FX dial.** Standardize ONE camera post-FX chain (Bloom + Tonemapping[HableFilmic] + Sharpen) and vary mood by toggling passes — add a PSX shader per horror room, FilmGrain+Vignette for grade, an Underwater/Wobble shader scoped to a water `PostProcessVolume` (social-hub / survival).
- **Time-of-day is a component, not a bake** — a `DayNightCycle` (`UseCurveDrivenSky`, `DayDurationSeconds`) drives the sky on a curve at runtime (tycoon, shop-interiors).

**Perf trade-off:** real-time point/spot lights are cheap to author but each casts/shadows per-frame — keep them small-radius and few, lean on the SkyBox for fill, and add an `AmbientLight` (~`0.5,0.5,0.5`) floor so unlit corners aren't pure black instead of farming more lights. Reserve baked GI (DDGI/EnvmapProbe) for static interiors where the one-time bake pays off; keep `VolumetricFogVolume` **disabled by default** and enable per-mood (rp-urban).

## Collision & physics props
**Split collision from rendering, and keep the world static by default.**
- **Static props** = `Prop`/`ModelRenderer` + a `ModelCollider` reusing the prop's own `.vmdl`, `Static: true`. No `Rigidbody` on scenery (tycoon: 118 static ModelColliders, only 2 Rigidbodies).
- **Blockers / triggers / walk-surfaces** = `BoxCollider`. Crucially, **don't trust mesh collision on decorative kit** — lay simple invisible BoxColliders as the *actual* play surface over pretty art (obstacle-course: 103 hand-placed box colliders as the walk surface).
- **Interior shell** = the `MeshComponent` itself (`Collision: "Mesh"`) — no separate collider.
- **Physics is opt-in and rare** — a `Rigidbody` only on the handful of objects that actually knock around (shop-interiors: 2 in a shop, 23 in a horror kiosk full of gibs). Adding `Rigidbody` to scenery is the classic perf mistake.
- **Destructibles are pre-fractured assemblies**, not single meshes: build a wall as a cluster of `dev/box` blocks wired by `FixedJoint`s so floods/explosions shatter it into physics debris (survival goders). Reach for this before a fracture tool. Bridge: `add_joint`.
- **Harvestables make their collider the hit-probe** — the resource's `HitCollider` points at its own `ModelCollider`, doubling as "what did the axe hit" (tycoon).

**Density limit:** most of a shipped level is *static colliders without rigidbodies*. Voxel/deformable worlds **budget colliders explicitly** — rebuild on a frame budget and cull by distance (`EnableVoxelColliders`, `DisableDistance 4000`, `MinColliderRebuildInterval`) so the chunk mesh doesn't tank physics (tycoon s_miner).

## Triggers, checkpoints, spawns
**Gameplay flow is wired with Components and marker GameObjects, not geometry.**
- **Triggers** = `BoxCollider { IsTrigger = true }` + a behavior Component reading overlaps (`CashRegister`, `SellStation`, `KioskTrigger`). Engine-stock `Sandbox.AchievementTrigger` / `TagApplyTrigger` / `KillPlane` are free generic "enter this box → fire" volumes (obstacle-course, social-hub). Bridge: `create_trigger_zone`.
- **The "course loop"** is a clean copyable set (obstacle-course): a `LevelDefinition` on the root with metadata + **gold/silver/bronze time tiers**, a start `SpawnPoint`, per-stage `Checkpoint` components (each owning its own respawn-point GO + gib config), an `OutOfBounds` kill-trigger routing back to the last checkpoint, and a `Finish` trigger.

```csharp
// A checkpoint = a trigger volume + a respawn marker GO. Modern overlap hook:
public sealed class Checkpoint : Component, Component.ITriggerListener
{
    [Property] public GameObject RespawnPoint { get; set; } // empty marker GO
    public void OnTriggerEnter( Collider other )
    {
        if ( other.GameObject.Root.Components.Get<PlayerProgress>() is { } p )
            p.LastCheckpoint = RespawnPoint;   // route death back here
    }
}
```
- **Spawns are layered markers** — raw `SpawnPoint`s plus distinct `PlayerSpawnDestination` "where the round drops you" markers, often under an `INITIAL SPAWN` group (social-hub). Mode-specific spawn *types* (`PlayerSpawnTDM`, per-role `*_spawnpoint.prefab`) let one map serve many game modes (rp-urban). Bridge: the spawn recipe is in `references/engine/player-controller.md` + `add_network_helper`.
- **Spawn density/contents as data** — place a *zone* (a Bounds box), let a `.loot`/`.clutter`/curve table fill it: `TreasureSpawnGroup` + depth-keyed loot tables, `OreGenerator` with a `DepthDistribution` curve (survival/tycoon). Place fixtures by hand; **runtime-spawn restockable/consumable items** (a `ShelfableSpawner` drops product onto shelves) rather than hand-placing every can.

## Navmesh
Bake the navmesh **only if you have AI** — player-only economies/arenas ship with `NavMesh.Enabled: false` (tycoon, survival-with-scripted-hazards). When you do need it:
- Configure `SceneProperties.NavMesh` with the s&box defaults (`AgentHeight 64`, `AgentRadius 16`, `AgentStepSize 18`, `AgentMaxSlope 40`, `IncludeStaticBodies: true`), bake it, and ship the `.navdata` (social-hub bakes per-scene). Bridge: `bake_navmesh` then `get_navmesh_path` to verify a route.
- Carve no-go zones with `NavMeshArea { IsBlocker = true }` (under counters, back rooms); add `NavMeshLink` / `NavTraversalDoor` so bots cross gaps and open doors (shop-interiors, social-hub).
- **Prototype AI routing in an isolated `nav.test.scene`** before wiring it into the real map (shop-interiors Doner).
- **Freeze lesson:** baking a navmesh over an **over-large terrain** (e.g. a 16384-unit `Sandbox.Terrain`) will hang/freeze the editor — bound the bake to the actual playable area (a `MapVolume`/Bounds), or skip the navmesh for the open-terrain regions entirely. Never bake the full terrain extent.

## Performance & budgets
- **Object-count shape:** a real level is *mostly cheap static art, a thin collision skeleton, sparse logic* — e.g. ~1583 ModelRenderers but only ~105 colliders and a handful of behavior components in one shipped level (obstacle-course). Aim for that ratio; logic count should be tiny.
- **Instancing over placement:** use macro-prefabs (`_xN`) and **Clutter** for density. `Sandbox.Clutter.ClutterComponent` (`Mode: Volume` + a Bounds box + a `.clutter` def + a fixed `Seed`) scatters hundreds of grass/seaweed/pebble instances deterministically — the answer to "I need 500 props without placing 500 objects" (rp-urban, survival, social-hub). Place the rocks you walk *on* as prefabs; use Clutter for the fine ground cover.
- **Streaming is rare** — most worlds are sized to fit in memory (one scene + nested prefabs/one `.vmap`); "endless" is faked with a tileable repeating room, not real streaming (survival backrooms). Voxel streaming exists but is the heaviest option (tycoon s_miner: `RenderDistanceChunks 2`, `StreamInterval 0.2`, `MaxChunkCreatesPerTick 1`).
- **Tier generators instead of one giant noise function** — drop sibling generator components (`Level2/Level3/Level4`) each owning the ore table + cave shape for its Z-band; new biome = new component, no scene code (tycoon).
- **Freeze-class gotchas:** (1) navmesh bake over a huge terrain (above); (2) un-budgeted voxel collider rebuilds — cap rebuilds-per-frame and batch multiplayer edits into RPCs (`MaxVoxelEditsPerRpc`, `BacklogDirtyChunkRebuildBudgetMs`); (3) `Rigidbody` on hundreds of static props — keep scenery static; (4) a single slow per-frame handler blocks the whole editor frame (the bridge drains its whole queue each frame). See `references/engine/performance-threading.md` for the host-authority + frame-budget rules these all stem from.

## Verify live
Author with `create_gameobject` / `create_prefab` / `instantiate_prefab` / `grid_duplicate` / `scatter_props`, light with `add_light` + `apply_atmosphere` + `set_skybox`, then **`screenshot_from` aimed at the changed area and read the PNG yourself** — `take_screenshot` only shoots the one fixed Main Camera, so a level edit elsewhere is invisible to it. Confirm collision with `raycast` / `physics_overlap`, and a nav route with `bake_navmesh` + `get_navmesh_path` before claiming the layout works.

---
See also: `sbox-api` (type/method reflection — verify any `MapInstance`/`Clutter`/`Terrain`/`NavMeshArea` member, API drifts per SDK), `sbox-build-feature` (the screenshot-driven iteration loop), `references/engine/worldgen-rendering.md` (SDF/voxel/runtime mesh + host-authoritative spawn placement), `references/engine/performance-threading.md` (object-count budgets, frame-budget + RPC-batching rules behind the freeze-class gotchas).

## Corpus refresh (2026): more reference implementations

### Weighted spawn nodes with anti-repeat + occupancy guard (despawn.murder)

`Components/LootSpawnPoint.cs` + `Systems/Rounds/RoundDirector/RoundDirector.Spawning.cs`

The canonical "designer marks spawn nodes, Director picks among them" pattern — not the zone/table approach already in this file (TreasureSpawnGroup/ShelfableSpawner), but a **per-point weighted selection** with occupancy and anti-clustering baked in:

```csharp
// Designer-placed component. The Director queries all of these each spawn tick.
public sealed class LootSpawnPoint : Component
{
    [Property] public float SpawnWeight { get; set; } = 1f;
    [Property] public GameObject CustomPrefab { get; set; }    // null = use Director default
    [Property] public string AchievementId { get; set; }

    // Returns false if something is already sitting here (physics overlap check).
    public bool CheckOccupancy()
        => Scene.Trace.Sphere( 24f, WorldPosition, WorldPosition )
               .WithoutTags( "loot_spawn" )
               .Run().Hit;
}

// Director.Spawning: weighted pick with anti-repeat queue + distance bias
// (near-milestone: prefer points FAR from all players)
Queue<LootSpawnPoint> _recentSpawns = new();   // last 2 positions excluded

LootSpawnPoint PickSpawnPoint( bool distanceBias )
{
    var candidates = Scene.GetAllComponents<LootSpawnPoint>()
        .Where( p => !_recentSpawns.Contains(p) && !p.CheckOccupancy() )
        .ToList();
    if ( candidates.Count == 0 ) return null;

    if ( distanceBias )
    {
        // score = SpawnWeight × min-distance-to-any-player (normalised)
        float maxDist = candidates.Max( p => MinPlayerDist(p) );
        candidates = candidates.OrderByDescending(
            p => p.SpawnWeight * (MinPlayerDist(p) / MathX.Max(maxDist, 1f)) ).ToList();
        var pick = candidates[0];
        TrackRecent( pick );
        return pick;
    }
    // plain weighted random
    float total = candidates.Sum( p => p.SpawnWeight );
    float roll  = Game.Random.Float( 0f, total );
    float acc   = 0f;
    foreach ( var p in candidates ) { acc += p.SpawnWeight; if ( acc >= roll ) { TrackRecent(p); return p; } }
    return candidates[^1];
}

void TrackRecent( LootSpawnPoint p )
{
    _recentSpawns.Enqueue( p );
    if ( _recentSpawns.Count > 2 ) _recentSpawns.Dequeue();
}
```

**Anti-patterns caught in the source:**
- Forgetting `CheckOccupancy()` → clues/items stack on the same point and vanish under each other. Fix: always guard before placing.
- Skipping the anti-repeat queue → the same corner gets hit repeatedly, breaking the "spreads across the map" feel. The queue size (2) is a single constant — tune it.

### Per-map metadata as a GameResource (despawn.murder)

`Systems/MapVote/MapResource.cs` — a `.mapvote` `GameResource` that carries not just scene metadata but **per-map spawn-tuning knobs** consumed by the Director at runtime:

```csharp
[GameResource( "Map Vote Resource", "mapvote", "Despawn map config", Icon = "map" )]
public sealed class MapResource : GameResource
{
    [Property] public SceneFile SceneFile    { get; set; }
    [Property] public Texture   Image        { get; set; }
    [Property] public float     VoiceRange   { get; set; } = 600f;

    // Director reads these to lerp spawn multiplier by lobby size
    [Property] public float ClueSpawnMultiplier    { get; set; } = 1f;
    [Property] public float ClueSpawnMultiplierMax { get; set; } = 1.5f;

    // Match a running scene back to its resource for runtime lookup
    public static MapResource GetCurrent( Scene scene )
        => ResourceLibrary.GetAll<MapResource>()
               .FirstOrDefault( r => r.SceneFile?.ResourcePath == scene.Source );
}
```

Steal this pattern whenever a game ships multiple maps with different difficulty/pacing: one `.mapvote` (or `.mapconfig`) file per map, `GetCurrent(Scene)` resolves at round start, the Director pulls multipliers from it. Zero per-map branching in code.

### Auto-classifying map size from spawn-point geometry (despawn.murder)

`RoundDirector.MapAnalysis.cs` — runs once at scene load, requires no hand-tuning:

```csharp
// Bucket: Small / Medium / Large / VeryLarge
// Formula: 0.7 * avgPairDist + 0.3 * boundingBoxDiag, then / sqrt(spawnCount)
var points = Scene.GetAllComponents<LootSpawnPoint>().ToList();
float avgDist  = AveragePairDistance( points );          // O(n²), fine for ≤100 points
float bbox     = BoundingBoxDiagonal( points );
float score    = (0.7f * avgDist + 0.3f * bbox) / MathX.Sqrt( MathX.Max(points.Count,1) );
MapSize = score switch { < 300 => Size.Small, < 600 => Size.Medium, < 900 => Size.Large, _ => Size.VeryLarge };
```

Use this to auto-set base spawn intervals, clue count targets, or round duration without exposing another knob to level designers. The 70/30 blend dampens outliers from single far-flung points that would inflate the bbox alone.

### Per-map C# loader components as "map plugins" (despawn.murder)

`Code/Maps/{Clue,Fate,Fracture,Plaza,Legacy}/*MapLoader.cs` — each map ships a bespoke `*MapLoader : Component` alongside a generic `MapLoader.cs` base. The generic loader handles common setup (spawn points, game-mode hooks); each map's plugin overrides or adds set-pieces. Plaza has a full scripted tanker-explosion cutscene: `CutsceneDirector`, `TankerExplosion`, `CarTrack*`, `FireZone` — all in the map's folder, never touching shared code.

Pattern: author a `MapLoader` base Component with virtual `OnMapLoaded()` / `OnRoundStart()` hooks, drop a concrete subclass into each map's prefab. Adding a new map = add a folder + one subclass. Shared gameplay code never changes. Maps become self-contained "plugins." The bridge's `create_gameobject` / `add_component_with_properties` can scaffold the stub.

### Atmospheric level dressing: 3-state flicker light (mishmaps.backrooms)

`Code/LightFlicker.cs` (`NeonFlickerLight`) — purely cosmetic, runs on every client (no `[Sync]`), zero curve/anim track, organic non-periodic behavior from a 3-state machine:

```csharp
public sealed class NeonFlickerLight : Component
{
    enum State { Off, On, Burst }
    [Property] public PointLight Light      { get; set; }
    [Property] public float OffMin          { get; set; } = 0.1f;
    [Property] public float OffMax          { get; set; } = 0.5f;
    [Property] public float BurstMin        { get; set; } = 0.05f;
    [Property] public float BurstMax        { get; set; } = 0.15f;

    State _state; RealTimeSince _timer; float _next; int _burstTarget, _burstCount;

    protected override void OnStart() => SwitchState( State.On );

    protected override void OnUpdate()
    {
        if ( _timer < _next ) return;
        if ( _state == State.Burst )
        {
            Light.Enabled = !Light.Enabled;
            if ( ++_burstCount >= _burstTarget ) SwitchState( RandomState() );
            else { _timer = 0; _next = Game.Random.Float( BurstMin, BurstMax ); }
        }
        else SwitchState( RandomState() );
    }

    void SwitchState( State s )
    {
        _state = s; _timer = 0; _burstCount = 0;
        _burstTarget = Game.Random.Int( 2, 6 );
        Light.Enabled = s != State.Off;
        _next = s == State.Off  ? Game.Random.Float( OffMin, OffMax )
              : s == State.On   ? Game.Random.Float( 1f, 5f )
                                : Game.Random.Float( BurstMin, BurstMax );
    }
    static State RandomState() => (State)Game.Random.Int( 0, 2 );
}
```

Drop this on any `PointLight` in a horror map. Extend to a `Style` enum (FluorescentDying / Sparking / StormStrobe) by varying the parameter ranges. The same skeleton drives any "intermittent ambient effect" — blinking signs, sparking consoles, pulsing growl sounds.

**Note:** the backrooms source export contains only this component — the actual maze geometry, collision, and player code were not included in the open-source package (see `sbox-lessons/mining-v2/games/mishmaps.backrooms.md` for scope warning).

### Document/object inspection interaction (dimmies.terryspapers)

`Code/ViewDocument.cs` — a pick-up-and-examine interaction that fits any "examine an item" mechanic (evidence, readable notes, loot appraisal):

```csharp
// Objects start hidden (z = -5). A trigger or NPC call sets Held = true.
// OnFixedUpdate lerps to camera-relative position for a "held up to face" feel.
protected override void OnFixedUpdate()
{
    if ( !Held ) { WorldPosition = Vector3.Lerp( WorldPosition, OriginalPos, 0.1f ); return; }
    var cam   = Scene.Camera;
    var target = cam.WorldPosition + cam.WorldRotation.Forward * HoldDistance;
    WorldPosition = Vector3.Lerp( WorldPosition, target, 0.1f );
    WorldRotation = Rotation.Slerp( WorldRotation, cam.WorldRotation * TargetRotOffset, 0.1f );
}
// Click while held → return to OriginalPos/Rot (saved on OnStart)
```

`TargetRotOffset` is a per-object `[Property]` (e.g. rotate ID cards face-up, tilt fingerprint tablets). Level design takeaway: **hide props below the desk (z < 0) as the "not visible" state** instead of toggling `Enabled` — smooth lerp-in on pickup, lerp-out on drop.

---

**Read these games for the patterns above:**
- `sbox-lessons/mining-v2/games/despawn.murder.md` — weighted spawn nodes, map-size auto-classification, per-map GameResource knobs, C# map-loader plugins, AI Director pacing
- `sbox-lessons/mining-v2/games/mishmaps.backrooms.md` — NeonFlickerLight (3-state machine atmosphere)
- `sbox-lessons/mining-v2/games/dimmies.terryspapers.md` — ViewDocument lerp-to-camera inspection, TCS scene-settle gate
