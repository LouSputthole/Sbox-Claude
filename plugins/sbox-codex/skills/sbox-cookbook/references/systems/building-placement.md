# Building / Placement System

Purpose: let a player place objects (furniture, shelves, props, structures) into the world with a live ghost preview, validity feedback, optional grid-snapping, and host-authoritative commit — the core of shop/restaurant/sandbox builders.

## What it IS and when you need it

A placement system has three jobs:
1. **Preview** — show a non-networked "ghost" of the thing under the cursor, tinted green/red for valid/invalid.
2. **Validate** — decide whether the spot is legal (on the right surface, in bounds, not overlapping, affordable).
3. **Commit** — on click, the **host** clones the real prefab, marks the world as occupied, and `NetworkSpawn()`s it.

Reach for this whenever the player builds/decorates a space. Two flavours dominate the mined games: **free placement** (snap a single ghost to a traced surface — enifun.shop_manager, sandbox spawners) and **grid placement** (snap to integer cells, support multi-cell footprints + rotation — GASTROTOWN/restaurant_dev, emg shelves). Both share the same ghost→validate→host-commit spine.

## Canonical modern-s&box recipe

### 1. The ghost preview (client-local, NetworkMode.Never)

The ghost is a local clone with all logic/colliders stripped so it never blocks its own raycast or replicates. Tint it to show validity.

```csharp
// Clone the prefab as a ghost; strip everything but the renderer.
_ghost = resource.Prefab.Clone();
_ghost.NetworkMode = NetworkMode.Never;          // never replicates
foreach ( var c in _ghost.Components.GetAll<Collider>( FindMode.EverythingInSelfAndDescendants ) )
    c.Enabled = false;                            // ghost must not block its own trace
```

Tint by re-coloring every renderer when validity flips (enifun.shop_manager: `Code/Shop/ShopBuilder.cs:348`):

```csharp
var color = _canPlace ? GhostColorValid : GhostColorInvalid;   // green / red
foreach ( var r in _ghost.Components.GetAll<ModelRenderer>( FindMode.EverythingInSelfAndDescendants ) )
    r.Tint = color;
```

### 2. Cast the mouse ray and position the ghost

Top-down/RTS builders cast the camera's mouse ray; first-person builders trace from the eyes. **Always ignore the ghost's own hierarchy** or it traces itself (enifun.shop_manager: `Code/Shop/ShopBuilder.cs:294`):

```csharp
var ray   = camera.GetMouseRay();                 // or Camera.ScreenPixelToRay(Mouse.Position)
var trace = Scene.Trace.Ray( ray, 2000f )
    .IgnoreGameObjectHierarchy( _ghost )          // critical: skip the ghost itself
    .Run();
if ( !trace.Hit ) return;

_placePos = SnapToGrid( trace.HitPosition );      // free-placement: skip snap, use HitPosition
_ghost.WorldPosition = _placePos;
_ghost.WorldRotation = _placeRot;
```

Free-placement snap is just rounding to a unit grid (enifun.shop_manager: `Code/Shop/ShopBuilder.cs:705`):

```csharp
static Vector3 SnapToGrid( Vector3 p ) => new Vector3(
    MathF.Round( p.x / GridSize ) * GridSize,
    MathF.Round( p.y / GridSize ) * GridSize, p.z );   // GridSize = 8 here
```

### 3. Validate (client-side, for UI feedback only)

Walk the parent chain of the hit object for a required tag (you can't tag every child), then check overlap and any game rules (enifun.shop_manager: `Code/Shop/ShopBuilder.cs:318`, `:354`):

```csharp
static bool HasTagInParents( GameObject hit, string tag )
{
    for ( var cur = hit; cur != null; cur = cur.Parent )
        if ( cur.Tags.Has( tag ) ) return true;
    return false;
}

_canPlace = trace.GameObject != null
    && HasTagInParents( trace.GameObject, "shop_floor" )   // right surface
    && !IsOverlappingFurniture()                           // box-trace vs existing, WithoutTags the ghost
    && funds.CanAfford( item.Cost );                       // advisory affordability
```

### 4. Commit on the host (authoritative)

Client validity is advisory. The host re-checks and is the only place that clones the real object. Non-host clicks route through an `[Rpc.Host]`.

```csharp
void OnClickPlace()
{
    if ( !_canPlace ) return;
    if ( Networking.IsHost ) DoPlace( _placePos, _placeRot, SelectedIndex );
    else                     RequestPlaceItemOnHost( _placePos, _placeRot, SelectedIndex );
}

[Rpc.Host]
void RequestPlaceItemOnHost( Vector3 pos, Rotation rot, int index ) => DoPlace( pos, rot, index );

void DoPlace( Vector3 pos, Rotation rot, int index )
{
    if ( !Networking.IsHost ) return;                 // re-assert on host
    var real = Items[index].Prefab.Clone();
    real.WorldPosition = pos; real.WorldRotation = rot;
    real.GetComponent<Sellable>().Setup( Items[index] );
    real.NetworkSpawn();                              // now it replicates to everyone
    Scene.NavMesh.SetDirty();                         // pathing changed — rebake
}
```

### 5. Grid variant: cells, footprints, rotation

For grid builders the world is a `NetDictionary<Vector2Int, Cell>` keyed by cell coord, host-synced. World↔grid is round-to-cell (restaurant_dev: `Code/Common/Restaurants/RestaurantGrid.cs`). Multi-cell footprints rotate via a per-`Direction` offset matrix (restaurant_dev: `:238`):

```csharp
public static List<Vector2Int> GetOccupiedCells( Vector2Int anchor, Vector2Int size, Direction dir )
{
    var cells = new List<Vector2Int>();
    for ( int x = 0; x < size.x; x++ )
    for ( int y = 0; y < size.y; y++ )
    {
        var off = dir switch {              // 90° rotation per Direction
            Direction.North => new Vector2Int(  x,  y ),
            Direction.East  => new Vector2Int( -y,  x ),
            Direction.South => new Vector2Int( -x, -y ),
            Direction.West  => new Vector2Int(  y, -x ),
            _               => new Vector2Int(  x,  y ) };
        cells.Add( anchor + off );
    }
    return cells;
}
```

Placement validates **every** footprint cell, then clones + stamps cells + spawns (restaurant_dev: `:742`):

```csharp
public bool TryAddFurni( Vector2Int gridPos, FurniResource res, Direction dir, out GameObject furni )
{
    furni = null;
    if ( !Networking.IsHost ) return false;                  // host-authoritative
    var cells = GetOccupiedCells( gridPos, res.GridSize, dir );
    foreach ( var c in cells )                               // ALL cells must be free + in-bounds
    {
        if ( !IsValidGridPosition( c ) ) return false;
        if ( TryGetCell( c, out var ex ) && ex is { IsOccupied: true } ) return false;
    }
    furni = res.Prefab.Clone( new CloneConfig( new Transform(), Restaurant.GameObject ) );
    furni.WorldPosition = GridToWorldPosition( gridPos );
    furni.LocalRotation = dir.ToRotation();
    foreach ( var c in cells )                               // reassign whole struct (NetDictionary)
        _furniCells[c] = new() { Furni = res.Id, IsOccupied = true, Object = furni, IsAnchor = c == gridPos };
    furni.NetworkSpawn();
    return true;
}
```

## Variations seen across games

- **Free vs grid.** enifun.shop_manager & the ISpawner games place a single ghost on a traced surface; GASTROTOWN/restaurant_dev & emg use integer cell grids with multi-cell footprints + rotation.
- **Tool-strategy build mode.** GASTROTOWN's `BuildModeClient` holds a `Dictionary<BuildTool, IBuildTool>` of stateless tool strategies (Wall/Furni/Floor/Select/Destroy) sharing a `BuildContext { Grid, Server, PhantomManager }`; `SetActiveTool` calls `OnDeactivated/OnActivated`. Clean copyable pattern for multi-mode builders (`Code/Common/BuildMode/BuildModeClient.cs:31`, `Tools/FurniTool.cs:40`).
- **Bounds-derived grids.** emg.everything_must_go derives a stocking grid from the renderer's model `Bounds` (columns × rows) rather than a fixed world grid — items snap into the first free cell (`Code/Shelving/Shelf.cs:66`).
- **Ident-string spawn router.** Sandbox-style games (artisan.darkrpog, apl.sandboxwars) abstract placement behind `ISpawner` (`DrawPreview`, `Task<bool> Loading`, `Spawn(transform, player)`) and route an ident like `prop:path` through a switch, tracing from the eyes with surface-normal alignment (`Code/Spawner/ISpawner.cs:5`, `Code/GameLoop/GameManager.cs:1042`).
- **Front-clearance / spacing rules.** enifun adds a second "arrow" raycast so a shelf's customer-access front also lands on valid floor, plus min-distance-between-registers (`ShopBuilder.cs:320`).
- **Physgun grab/spin/snap (free-floating, not surface-stamped).** A Garry's-Mod-style hold-and-place where the carried object floats at a scroll-adjustable distance from the eyes, spins with the mouse (hold `use`), and snaps its rotation to 45°/15° increments (run/walk). The whole hold/release is host-authoritative: the client sends `Request*` RPCs, the host owns a synced `GrabState` struct (target + grab distance + rotation). Pull-from-distance grabs a far object toward you; scroll (faster while ducking) changes hold distance. Verified against klavs.basebuilder `Code/BaseBuilder/BaseBuilderPlacementTool.cs` (synced `GrabState`, mouse spin with `use`, snap-to-grid rotation, scroll grab-distance, host-auth hold/release via client `Request*` RPCs); same lineage in dexlab.sandbox-reforged. Choose this over surface-stamping when the player should freely position/rotate a physics prop in mid-air (sandbox builds, decorating) rather than snap it flat to a floor cell.
- **Support-based modular building (foundation → wall/floor/pillar, snap-to-existing).** Grid-free structure building where each piece type carries a `Size` + `GroundOnly` spec and snaps to a nearby existing piece per a `CanUseSupportType` table: foundations chain edge-to-edge on the ground, walls/floors/pillars require a valid support and snap to its edges/corners/top within a `VerticalSnapTolerance`. Validation gates on build distance, terrain flatness/steepness (multi-sample ground trace + normal-dot), **footprint overlap** (with a deliberate exception for perpendicular wall corner joints), and solid-obstacle traces; the single tinted ghost shows green-valid / blue-snapped / red-invalid plus a *reason* string. Place/delete are `[Rpc.Host]` request → authority validates → `[Rpc.Owner]` confirm-with-message. Use for bases/fences/cemetery walls where pieces must connect *logically* rather than fill a fixed grid (vault77.chop_the_forest `Building/ModularBuildingTool.cs` + `ModularBuildingPiece.cs`).

## Gotchas

- **Ghost must be `NetworkMode.Never` with colliders/logic disabled** or it replicates, blocks its own raycast, or acts as a real object (enifun: `ShopBuilder.cs`; GASTROTOWN phantom uses tag exclusions `'phantom'/'agent'/'grabbed'`).
- **Ignore the ghost hierarchy in the placement trace** (`IgnoreGameObjectHierarchy(_ghost)`) and exclude floor/wall/spawn tags in the overlap trace (`WithoutTags`) — self-collision is the #1 false "invalid".
- **Client validity is advisory; the host re-validates.** Every commit path starts with `if (!Networking.IsHost) return;`, and non-host clicks go through `[Rpc.Host]` (all grid mutation in restaurant_dev is `if(!Networking.IsHost) return false;`).
- **`NetDictionary` cells hold structs — mutate by reassigning the whole struct**, not editing in place (restaurant_dev `_furniCells[c] = new(){...}`). NetList/NetDictionary only replicate from host.
- **Tag checks must walk parents** (`HasTagInParents`) because the hit collider is usually a deep child.
- **Rebake pathing after placing/removing** anything that affects walkable space: `Scene.NavMesh.SetDirty()` (enifun).
- **Multiple cell resolutions = off-by-N bugs.** GASTROTOWN keeps fine furni cells + 4× coarser wall/floor cells — mixing them silently misplaces things.
- **Never send `GameResource` refs over RPCs** — send a string `Id` and reconstruct via `ResourceLibrary.GetAll<T>().FirstOrDefault(r => r.Id == id)` on the host; refs don't round-trip on the wire (GASTROTOWN economy).
- **Pickup/move = hide original, re-run placement, re-commit** (enifun `_isPickupMode`); don't forget to destroy the ghost (`DestroyGhost`) or it leaks.

## Seen in

- **enifun.shop_manager** — `Code/Shop/ShopBuilder.cs` (free-placement ghost, grid-snap, front clearance, host RPC) — the cleanest single-file reference.
- **thefancylads.restaurant_dev** (GASTROTOWN) — `Code/Common/Restaurants/RestaurantGrid.cs` (multi-resolution cell grid, footprints, rotation), `Code/Common/BuildMode/BuildModeClient.cs` + `Tools/FurniTool.cs` (tool-strategy build mode + phantom).
- **emg.everything_must_go** — `Code/Shelving/Shelf.cs` (bounds-derived snap grid, host-authoritative `[Sync(FromHost)]` state).
- **artisan.darkrpog** — `Code/Spawner/ISpawner.cs`, `Code/GameLoop/GameManager.cs` (ISpawner + ident-string router, eye-trace placement).
- **apl.sandboxwars** — `sandbox/Code/Spawner/ISpawner.cs`, `GameManager.Spawn.cs` (ISpawner strategy + broadcast spawn dispatch).
- **klavs.basebuilder** / **dexlab.sandbox-reforged** — `Code/BaseBuilder/BaseBuilderPlacementTool.cs` (physgun grab/spin/45°-snap, scroll distance, host-auth hold/release).
- **vault77.chop_the_forest** — `Building/ModularBuildingTool.cs` + `ModularBuildingPiece.cs` (support-based foundation→wall/floor/pillar snap with reason-string ghost, host-auth place/delete).
- **stellawisps.lumberyard** — `Code/Tycoon/TycoonMain.cs` (claimable-plot tycoon: grid build + BBox overlap + JSON relative-transform save), `BuyTycoonButton.cs` (claim plot via `Network.TakeOwnership`).
- **meteorlab.garden** — `Code/Simple/BasePlacer.cs` (translucent ghost + OOBBox zone-containment + hold-to-place ActionRing). **thefancylads.farm_land** — `Code/Common/Building/BuildingPlacer.cs` (grid-snap host-auth with phantom valid/invalid tint + move/destroy + ownership toasts).

Verify live: the placement API surface changes between SDK builds — confirm signatures against the installed SDK with the bridge's `describe_type` / `search_types` reflection (e.g. `describe_type Scene.Trace`, `GameObject`, `CloneConfig`, `NetworkMode`, `NetDictionary`) before relying on a method shape. See also the **sbox-api** skill (authoritative type/method lookup) and **sbox-build-feature** skill (the screenshot-driven place-it-and-look iteration loop).

## Corpus refresh (2026): more reference implementations

Net-new variations from the latest mining pass. (The 4 newest games — facepunch.ss2, despawn.murder, barrelproto.ragroll plus facepunch.fair — were all checked; only **facepunch.fair** has building-placement material. The other gold here is from **dexlab.sandbox-reforged**, **apl.sandboxwars**, and **bublic.stone_by_stone**.)

### Shader-attribute ghost instead of tint-swap (facepunch.fair)

The cleanest tycoon in the corpus drives the ghost's validity color through a **single shader attribute** on the `SceneObject`, not by re-tinting every renderer. One channel value = valid/invalid/none; the material reads it. Also stops the preview batching so it renders distinctly (`Park/Buildings/BuildingPlacer.cs`, same idiom in `Rides/TrackRides/TrackBuilder.cs`):

```csharp
foreach ( var so in ghost.Components.GetAll<ModelRenderer>().Select( r => r.SceneObject ) )
{
    so.Attributes.Set( "Ghost", canPlace ? 1 : 2 );   // 0=off, 1=green/valid, 2=red/invalid
    so.Batchable = false;                              // preview shouldn't merge into the static batch
}
```

Cheaper than `r.Tint = color` across a deep hierarchy and the material author controls the exact look (fresnel, scanlines). Pair it with placement juice keyed off the surface: a `place_dustcloud` particle whose `ParticleRingEmitter.Radius` is set at runtime, and a sound switched by an `ObjectMaterial` enum (`place_object_wood_1`, …).

### Polymorphic commit + auto-rotate-to-neighbour (facepunch.fair)

`BuildingPlacer` (client) owns the ghost and runs `CanPlace()` locally; the host re-validates and spends money in `[Rpc.Host]` methods dispatched over `switch (ObjectToPlace)` — `Building | Animal | PathFurniture | Decoration` each route to `PlaceBuilding/PlaceDecoration/…`. The net-new placement nicety is **auto-rotate**: before committing, snap a shop's entrance toward the adjacent path so guests can actually reach it (`TryAutoRotate`). Refunds use `SnapToGrid(10)` rounding and `Math.Min(cost*3/4, cost)` — round the world but clamp the money. Same `if (Networking.IsHost)` re-check spine as the rest of this doc; the value is the *single ghost, many object kinds via one switch* shape.

### Track / spline builder: a "ghost" that is a live-remeshed mesh, not a clone (facepunch.fair)

For builders where the placed thing is procedural geometry (coaster track, fence run, pipe), the preview can't be a prefab clone. `Rides/TrackRides/TrackBuilder.cs` keeps a `NotNetworked | NotSaved` preview `TrackSection`+`TrackMesh` and **re-meshes it only when the inputs change** (`HashCode.Combine(elementCount, Money)`), tinted by the same `"Ghost"` attribute. Build cost = `length*BaseTrackCost + elevationTotal*ElevationCost` (sample the spline per tile); `CalculateBuildCost` returns `null` (un-buildable) if it would go underground, exceed `MaxElevation`, or collide. Two obstruction passes sample the spline every 16 units (`HasGridObstructions` vs grid, `HasTrackObstructions` vs other sections, ignoring shared end nodes). Build/Demolish are `[Rpc.Host]` taking a network-safe **`TrackElement.RpcSafe` struct** — the reusable pattern of an `RpcSafe` companion projection when the real type won't round-trip on the wire (the doc already warns "never send `GameResource` over RPC"; this is the structural answer for complex elements).

### Surface-aligned transform from the trace normal (apl.sandboxwars)

Free-placement on arbitrary surfaces (walls, ramps) needs the object oriented to the surface, not just dropped at the hit point. `GameManager.Spawn` derives a full basis from the trace normal with cross-products rather than only setting position (`Code/GameLoop/GameManager.Spawn.cs`):

```csharp
var up = trace.Normal;
var forward = Vector3.Cross( up, Vector3.Right ).Normal;     // any non-parallel ref axis
if ( forward.Length < 0.01f ) forward = Vector3.Cross( up, Vector3.Forward ).Normal;
var rot = Rotation.LookAt( forward, up );                    // object's +Z hugs the surface
var t = new Transform( trace.HitPosition, rot );
```

The same file also shows the **broadcast-spawn-with-caller-bail** shape: `[Rpc.Broadcast] Spawn(ident)` where the calling client plays local SFX + a `Sandbox.Services.Stats.Increment("spawn",…)` then `return`s, and only the host actually clones/`NetworkSpawn`s — one method, client juice + host authority.

### Blueprint placement: spawn a saved contraption, not one prefab (apl.sandboxwars / dexlab.sandbox-reforged)

The "dupe" path is placement of a *serialized subtree* under the ghost transform — useful for any "stamp a prefabbed group" feature. `DuplicatorSpawner.Spawn` deep-clones the blueprint JSON, **re-uniquifies the GUIDs**, and deserializes each object with a transform override inside a batch group, then stamps ownership + spawns (`Code/Spawner/DuplicatorSpawner.cs`):

```csharp
SceneUtility.MakeIdGuidsUnique( json );                       // critical: else the paste collides with the original
using ( Scene.BatchGroup() )
foreach ( var objNode in objects )
{
    var go = new GameObject();
    go.Deserialize( objNode, new GameObject.DeserializeOptions { TransformOverride = world } );
    go.GetOrAddComponent<Ownable>().Set( player );
    go.NetworkSpawn();
}
```

`MakeIdGuidsUnique` is the gotcha — paste without it and you get duplicate-GUID corruption. Cloud-referenced models are `await Package.MountAsync`'d before the deserialize so workshop props resolve on another machine.

### Toolgun-style mode framework for placement tools (dexlab.sandbox-reforged / apl.sandboxwars)

When a builder has *several* placement actions (place, weld, remove, rotate) rather than one, the GMod toolgun shape beats a giant `OnUpdate` if/else. `ToolMode : Component` is the base each tool subclasses; it registers **declarative actions** with lambda labels (so HUD hints reflect tool state), auto-dispatches them through cancellable scene events, and persists per-tool settings (`Code/Weapons/ToolGun/ToolMode.cs`, `ToolAction.cs`, `ToolMode.Cookies.cs`):

```csharp
RegisterAction( ToolInput.Primary, () => $"Place {ItemName}", DoPlace, InputMode.Pressed );
// DispatchActions(): on Input.Pressed/Down → fire IToolActionEvents.OnToolAction (cancellable
//   → governance/limits veto here), invoke callback, then OnPostToolAction(Track()'d new objects).
protected override void OnEnabled()  => LoadCookies();   // per-tool persisted settings (Network.IsOwner)
protected override void OnDisabled() => SaveCookies();
```

Sibling tools live as components on the gun, exactly one enabled, switched via `[Rpc.Host] SetToolMode(name)`. Two extra placement aids worth lifting: a **snap grid** (`ToolMode.SnapGrid.cs`) and, in apl.sandboxwars, a **snap-grid aim-lock** — hold `use` to lock the camera onto the nearest snap corner (`Rotation.LookAt(snapPos - eye)` fed back through `ref angles`), opt-out per tool via `ShouldDisplaySnapGrid`. Constraint tools there are a **two-stage state machine** (pick Point1 → Point2 → create) whose creation `[Rpc.Host(NetFlags.OwnerOnly)]` **re-runs validity on the host** before building — the constraint analogue of "client validity is advisory."

### "Placement" as pre-placed models toggled by purchase — no runtime spawn (bublic.stone_by_stone)

A single-player, save-friendly alternative to ghost→clone→spawn: author every building tier as pre-placed (disabled) GameObjects in the scene, and "place" by **enabling the right one** when the upgrade is bought. Each `UpdateItem` owns a `List<Models>` (GameObject refs); for single-tier categories only the highest purchased tier's models are on (`Code/Ui/Update.razor` `ApplyModelsFromPurchased`):

```csharp
var top = items.Where( i => i.IsBuy ).MaxBy( i => i.Level );        // highest purchased tier
foreach ( var it in items )
foreach ( var m in it.Models ) m.Enabled = ( it == top );          // multi-model garden keeps all on
```

No `Clone`, no `NetworkSpawn`, no occupancy map — and persistence is trivial (save the purchase flags, call `ApplyModelsFromPurchased()` on load). Reach for this when the build space is fixed and the player picks *tiers*, not *positions* (tycoon upgrade buildings, base-room fit-outs) — it sidesteps every networking/ghost gotcha in this doc at the cost of free positioning.

### Buy-land chunks gate the build zone (facepunch.fair)

Where the player must *own ground* before placing on it, `Park/BuildingZone.cs` models the buildable area as 32×32 owned chunks bought adjacently at distance-scaled cost (`GetChunkCost = 10000 * round(dist/zoneLen)`); `PathBuilder.PlacePath` checks `BuildingZone.Instance.IsOwned(pos)` **twice** (client + host) before laying. Net-new shipping detail: the owned-chunk set rides as a raw `ByteStream` via `Component.INetworkSnapshot` (not `[Sync]`), and the zone traces the **perimeter of owned tiles with a right-hand wall-follow** to place fence/pillar props + a `LineRenderer` boundary. A composable "validate placement against an owned-region mask" layer on top of the surface/overlap checks already in this doc.

### Updated "read these games" pointer

- **facepunch.fair** — `Park/Buildings/BuildingPlacer.cs` (shader-attribute ghost, `CanPlace()` + polymorphic `[Rpc.Host]` over `switch(ObjectToPlace)`, `TryAutoRotate`), `Rides/TrackRides/TrackBuilder.cs` (live-remeshed `NotNetworked` track ghost, elevation costing, `RpcSafe` struct), `Park/Paths/PathBuilder.cs` (drag-lay paths, host cursor-continuation RPC), `Park/BuildingZone.cs` (buy-land chunks + `INetworkSnapshot` + perimeter fence). The most architecturally mature builder in the corpus.
- **apl.sandboxwars** — `Code/GameLoop/GameManager.Spawn.cs` (surface-aligned transform from trace normal, broadcast-spawn-with-caller-bail), `Code/Spawner/DuplicatorSpawner.cs` (blueprint paste with `MakeIdGuidsUnique` + `Deserialize(TransformOverride)`), `Code/Weapons/ToolGun/Modes/BaseConstraintToolMode.cs` (two-stage host-revalidated constraint placement), `Code/Weapons/ToolGun/ToolMode.SnapGrid.cs` (snap-grid aim-lock).
- **dexlab.sandbox-reforged** — `Code/Weapons/ToolGun/ToolMode.cs` + `ToolAction.cs` + `ToolMode.Cookies.cs` + `ToolMode.SnapGrid.cs` (the toolgun mode framework: declarative cancellable actions, per-tool cookies, snap grid). Same physgun lineage already noted above.
- **bublic.stone_by_stone** — `Code/Ui/Update.razor` + `Code/Ui/House.razor` (`ApplyModelsFromPurchased`: pre-placed-models-toggled-by-purchase, the no-spawn save-friendly "placement" variant for single-player tier builders).
