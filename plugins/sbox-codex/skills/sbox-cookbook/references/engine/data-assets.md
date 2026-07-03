# Data Assets & Content Pipelines

Purpose: data-drive game content in modern s&box — `GameResource` catalogs, prefab registries, runtime file discovery, render-to-texture surfaces, and shipping editor authoring tools — without hand-writing C# per variant.

## Mental model

"Assets" in s&box spans four things, each with its own idiom:

1. **Data assets** — a `GameResource` subclass with `[AssetType]` gives every typed variant (a car, a weapon, an enemy) its own editor file, asset picker, and inspector. Designers add content; you write zero C#.
2. **Prefab registries** — designer-referenced `PrefabScene`/`GameObject` prefabs pre-cloned at awake, broken from prefab, indexed by a stable id, and handed out via `Clone`.
3. **Runtime files** — `FileSystem.Mounted` (read-only shipped) vs `FileSystem.Data` (user-writable) for mods, saves, and screenshots.
4. **Custom render objects** — raw `SceneLight`/`SceneCustomObject`/render targets you own and must clean up manually.

Cross-cutting rule: **reflection is the source of truth**. Editor-namespace and render-object APIs drift between SDK builds — confirm with `describe_type`/`search_types` before writing, never from training data.

## Pattern: data-drive content with `GameResource` + `[AssetType]`

Make every catalog of typed variants a `GameResource`. Each variant becomes a `.vcfg` file the designer edits in the inspector; reference other assets by typed path (`[ResourceType("vmdl")]`, `[ResourceType("sound")]`); add free-form `Tags` for gamemode filtering. Adding a new car = a new `.vcfg` + model, no code (sbox-vehicle-kit: VehicleConfig.cs:11-92).

```csharp
[AssetType( Name = "Vehicle Config", Extension = "vcfg", Category = "Vehicles" )]
[Icon( "directions_car" )]
public sealed class VehicleConfig : GameResource
{
    [Property] public string DisplayName { get; set; } = "Sedan";
    [Property, ResourceType( "vmdl" )]   public string ModelPath  { get; set; }
    [Property, ResourceType( "prefab" )] public string PrefabPath { get; set; }

    [Group( "Performance" ), Property, Range( 20, 400 )]
    public float MaxSpeedKmh { get; set; } = 140;

    [Group( "Cosmetics" ), Property]
    public List<string> Tags { get; set; } = new();   // "police", "starter", ...
}
```

`[Group(...)]`, `[Range(...)]`, `[Icon(...)]`, and `Curve` properties turn the inspector into the authoring tool (sbox-vehicle-kit: VehicleConfig.cs:21-42). For pure serializable data with no behavior, a thin `IAsset` interface (`Path => ResourcePath`) over a `GameResource` base is enough (SBox-Visual-Novel-Base: Asset.cs:21-25).

## Pattern: discover assets at runtime — and FILTER the suffix collision

Expose static accessors over `ResourceLibrary.GetAll<T>()`. **Critical gotcha:** the resource system suffix-matches extensions, so a `.vcfg` type silently picks up engine `core/cfg/*.cfg` files (e.g. `configschema.cfg`) as all-default phantom instances. Always reject engine-dir prefixes (sbox-vehicle-kit: VehicleConfig.cs:102-119).

```csharp
public static IEnumerable<VehicleConfig> All =>
    ResourceLibrary.GetAll<VehicleConfig>()
        .Where( v => v is not null
            && !string.IsNullOrEmpty( v.ResourcePath )
            && !v.ResourcePath.StartsWith( "cfg/", StringComparison.OrdinalIgnoreCase ) );

public static VehicleConfig Find( string ident ) => All.FirstOrDefault( v => v.ResourceName == ident );
public static IEnumerable<VehicleConfig> WithTag( string tag ) => All.Where( v => v.Tags.Contains( tag ) );
```

Any custom extension that is a *suffix* of a built-in one is vulnerable — bake this filter into every GameResource lookup.

## Pattern: prefab registry — pre-clone, BreakFromPrefab, index by id

For inspector-referenced prefabs, build a registry `Component`: in `OnAwake` clone each prefab disabled, parent it, **call `BreakFromPrefab()`**, and index by a stable string id. "Giving" an item is then a lookup + clone (simple-weapon-base: WeaponRegistry.cs:25-55).

```csharp
[Property] public List<PrefabScene> WeaponPrefabs { get; set; } = new();
public Dictionary<string, Weapon> Weapons { get; } = new();

protected override void OnAwake()
{
    Instance = this;
    foreach ( var prefab in WeaponPrefabs )
    {
        var go = prefab.Clone( new CloneConfig { StartEnabled = false } );
        go.SetParent( GameObject );
        go.BreakFromPrefab();                       // REQUIRED if mutated at runtime
        var weapon = go.Components.Get<Weapon>( true );
        Weapons.TryAdd( weapon.ClassName, weapon );
        go.Name = weapon.ClassName;
    }
}
```

`BreakFromPrefab()` is mandatory on any prefab you mutate at runtime (e.g. attaching weapon attachments) — skip it and runtime-added components are silently lost on the next clone (simple-weapon-base: WeaponRegistry.cs:40).

## Pattern: service-locator singleton for registries/settings

The standard idiom for a globally-reachable content service: a static `Instance` set in `OnAwake`, **nulled in `OnDestroy`** so a stale handle doesn't survive a scene reload/hotload (simple-weapon-base: WeaponRegistry.cs:14-23).

```csharp
public static WeaponRegistry Instance { get; private set; }
protected override void OnAwake()   => Instance = this;
protected override void OnDestroy() { if ( Instance == this ) Instance = null; }
```

Null-guard every read — the component may not have awoken yet. (For state that must survive hotload, prefer `GameObjectSystem<T>` — see the networking reference.)

## Pattern: runtime file discovery & persistence

Treat the two filesystems distinctly: `FileSystem.Mounted` is read-only packaged content (listed in `.sbproj` `Resources`); `FileSystem.Data` is the user-writable sandbox. **Discover = enumerate both and merge; write = `Data` only** (sgba: GameEntry.cs:13-40).

```csharp
public static List<GameEntry> Discover()
{
    List<GameEntry> entries = [];
    CollectFrom( FileSystem.Mounted, entries );     // shipped roms/levels
    CollectFrom( FileSystem.Data,    entries );     // user-imported
    return entries;
}

static void CollectFrom( BaseFileSystem fs, List<GameEntry> entries )
{
    IEnumerable<string> found;
    try { found = fs.FindFile( "roms", "*.gba" ) ?? []; } catch { return; }   // FindFile can throw
    foreach ( var name in found ) entries.Add( BuildEntry( fs, $"roms/{name}", name ) );
}
```

For reads, probe whichever store reports `FileExists`. Write all saves/states/screenshots under `FileSystem.Data` with stable derived paths.

## Pattern: render-to-texture surface (CCTV / scope / minimap / mirror)

In `OnEnabled` create a dynamic render target; in a pre-render hook call `Graphics.RenderToTexture` **throttled by a `TimeSince` gate**; bind the texture to the model's `screen` attribute or a material override; `Dispose()` in `OnDestroy` (wirebox: WireCameraScreenComponent.cs:30-118 — API surface only; that file uses legacy attributes).

```csharp
Texture _texture;
TimeSince _sinceRender = 0;
int _fps = 20;

protected override void OnEnabled()
{
    _texture?.Dispose();
    _texture = Texture.CreateRenderTarget()
        .WithSize( 500, 300 ).WithScreenFormat().WithDynamicUsage().Create();

    var so = Components.Get<ModelRenderer>().SceneObject;
    if ( so.Attributes.GetTexture( "screen" ) != null )   // model has a screen slot
        so.Attributes.Set( "screen", _texture );
    else                                                  // override a material index
    {
        var mat = Material.Create( "cam_screen", "simple" );
        mat.Set( "Color", _texture );
        Components.Get<ModelRenderer>().SetMaterialOverride( mat, "materialIndex0" );
    }
}

protected override void OnPreRender()
{
    if ( !_camera.IsValid() || _sinceRender < 1.0f / _fps ) return;   // THROTTLE
    _sinceRender = 0;
    Graphics.RenderToTexture( _camera.GetSceneCamera(), _texture );
}

protected override void OnDestroy() => _texture?.Dispose();
```

Rendering every frame, with N screens, tanks framerate — the `TimeSince` gate is not optional. The `WithDynamicUsage` render target is **not** GC-managed; `Dispose()` it.

## Pattern: editor-live custom visuals — `[ExecuteInEditor]` + raw scene objects

To drive engine render objects directly (live-preview tools, procedural lights), implement `Component.ExecuteInEditor` so lifecycle runs in the editor. You **own** the handle: delete it in `OnDisabled` and before recreating in `OnEnabled`, and guard every access with `IsValid()` (sbox-scenestaging: LineRendererLight.cs:4-70).

```csharp
public class LineRendererLight : Component, Component.ExecuteInEditor
{
    SceneLight _light;

    protected override void OnEnabled()
    {
        if ( _light.IsValid() ) _light.Delete();                 // before recreate
        _light = new SceneLight( Scene.SceneWorld, Vector3.Zero, 100, Color.Red );
    }

    protected override void OnDisabled() { if ( _light.IsValid() ) _light.Delete(); }

    protected override void OnUpdate()
    {
        if ( !_light.IsValid() ) return;
        _light.Shape = SceneLight.LightShape.Capsule;
        _light.Position = WorldPosition;
    }
}
```

Because `[ExecuteInEditor]` runs during normal editing, leaks (no `Delete()` on disable/recreate) accumulate orphaned `SceneLight`/`SceneObject` handles on every hotload.

## Pattern: ship editor tooling inside a library

Put editor-only code under an `editor/` folder and exclude it from the runtime build via `IgnoreFolders` (SBox-Visual-Novel-Base: vnbase_library.sbproj:25-28). It still compiles into the editor assembly (can reference `Editor`, `EditorUtility`, `AssetList`) but is stripped from the shipped runtime build.

```jsonc
// .sbproj
"Metadata": { "Compiler": { "IgnoreFolders": [ "editor", "unittest" ] } }
```

Add a **"New > YourType"** file creator to the asset-browser folder menu via a `static [Event]` handler. Defer to `AboutToShow` and **self-unsubscribe** — the event fires repeatedly, so an inline build appends your option N times (SBox-Visual-Novel-Base: Events.cs:13-39).

```csharp
[Event( "folder.contextmenu" )]
private static void FolderContextMenu( FolderContextMenu obj )
{
    if ( obj.Context is not AssetList assetList ) return;     // narrow first

    Action? handler = null;
    handler = () =>
    {
        obj.Menu.AboutToShow -= handler;                     // self-unsubscribe
        var menu = obj.Menu.FindOrCreateMenu( "New" ).FindOrCreateMenu( "VNBase" );
        menu.AddOption( new Option( "VNScript", "desc", () => CreateNewFile( assetList.Browser ) ) );
    };
    obj.Menu.AboutToShow += handler;
}
```

Write the file with `SaveFileDialog` + an inline raw-string template, wrapped in try/catch — editor commands swallow exceptions silently (SBox-Visual-Novel-Base: Events.cs:41-69).

```csharp
var chosen = EditorUtility.SaveFileDialog( "Save vnscript...", ".vnscript", defaultPath );
if ( string.IsNullOrWhiteSpace( chosen ) ) return;            // user cancelled = no-op
try { File.WriteAllText( chosen, Template ); Log.Info( $"Created {chosen}" ); }
catch ( Exception ex ) { Log.Error( $"Failed: {ex}" ); }
```

For **batch operations on existing assets**, hook `[Event("asset.contextmenu", Priority = 60)]`, gate on the selection, then add an option that loops over `e.SelectedList` (sbox-grubs: AddAutoLOD.cs:10-21).

```csharp
[Event( "asset.contextmenu", Priority = 60 )]
public static void OnAssetContext( AssetContextMenu e )
{
    if ( !e.SelectedList.All( x => x.AbsolutePath.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ) )
        return;
    e.Menu.AddOption( "Add Automatic LODs", "layers",
        action: () => { foreach ( var a in e.SelectedList ) AddLODsToVmdl( a.AbsolutePath ); } );
}
```

When a tool must hand-edit text-based asset source (`.vmdl`/`.vmat` is KeyValues), do it defensively: normalize newlines (`.Replace("\r\n","\n")`), bail if the block already exists (idempotency), anchor on a stable structural marker and error if it's missing, and format numbers with `CultureInfo.InvariantCulture` (sbox-grubs: AddAutoLOD.cs:96-144). Prefer a real asset API; reserve text surgery for fields no API exposes.

## Gotcha table

| Gotcha | Fix |
| --- | --- |
| `GetAll<T>()` returns engine `cfg/*.cfg` as phantom `.vcfg` instances (suffix match) | Reject `ResourcePath.StartsWith("cfg/", OrdinalIgnoreCase)` in your `All` accessor (sbox-vehicle-kit: VehicleConfig.cs:109-113) |
| Runtime-added components lost when a prefab is re-cloned | Call `BreakFromPrefab()` on any prefab you mutate at runtime (simple-weapon-base: WeaponRegistry.cs:40) |
| Stale static `Instance` survives scene reload/hotload; reads before awake NRE | Null it in `OnDestroy`, null-guard reads (simple-weapon-base: WeaponRegistry.cs:19-23) |
| `RenderToTexture` every frame × N screens tanks FPS | Throttle with a `TimeSince < 1/fps` gate (wirebox: WireCameraScreenComponent.cs:38-42) |
| `WithDynamicUsage` render target leaks (not GC-managed) | `Dispose()` in `OnDestroy` (wirebox: WireCameraScreenComponent.cs:102-108) |
| Raw `SceneLight`/`SceneObject` orphaned on hotload | `Delete()` in `OnDisabled` and before recreate; guard with `IsValid()` (sbox-scenestaging: LineRendererLight.cs:26-35) |
| Reading one filesystem misses shipped OR user content; writes outside `Data` don't persist | Discover = merge `Mounted` + `Data`; write = `Data` only (sgba: GameEntry.cs:13-20) |
| `[ExecuteInEditor]` runs in the editor, so render-object leaks bite during normal editing | Same `IsValid`/`Delete` lifecycle discipline applies at edit time, not just play |
| `editor/` code leaks into the runtime build (whitelist failure) | List `editor` in `.sbproj` `Compiler.IgnoreFolders` (VNBase: vnbase_library.sbproj:25-28) |
| `folder.contextmenu` fires repeatedly → option appended N times | Defer to `AboutToShow` and self-unsubscribe (`-= handler`) (VNBase: Events.cs:21-38) |
| Editor command lambdas swallow exceptions silently | Wrap dialog + write in try/catch + `Log.Error`/`Log.Info` (VNBase: Events.cs:53-68) |
| `SaveFileDialog` returns null/empty on cancel | Treat as no-op, not an error (VNBase: Events.cs:57-60) |
| Hand-editing `.vmdl`/`.vmat` text corrupts on CRLF/culture/marker drift | Normalize newlines, idempotency-guard, anchor on a stable marker, `InvariantCulture` (sbox-grubs: AddAutoLOD.cs:96-144) |

Verify live: editor-namespace types (`AssetList`, `FolderContextMenu`, `AssetContextMenu`, `EditorUtility`, `Option`) and render-object APIs (`SceneLight`, `Texture.CreateRenderTarget`, `Graphics.RenderToTexture`) shift between SDK builds — confirm signatures with `describe_type`/`search_types`/`get_method_signature` against the installed SDK; reflection is authoritative, not training data.

See also: **sbox-api** (resolve exact types/signatures before writing) and **sbox-build-feature** (screenshot-driven iteration to verify render targets and inspector authoring visually).

## Corpus refresh (2026): more reference implementations

### Pattern: GameResource carries both a data sheet AND a behavior prefab (despawn.murder)

`SubRoleResource` (`.subrole`, `Systems/SubRoles/SubRoleResource.cs`) stores display data *and* an optional `GameObject BehaviorPrefab`. The round-state reads the resource generically: `Give(resource.Equipment)` then `GameObject.Clone(NetworkSpawn=true)` for `BehaviorPrefab`. Zero per-role branching in the spawn path — adding a role = author a `.subrole` + a behavior prefab, no C# (despawn.murder: SubRoleResource.cs, InProgressRoundState.Roles.cs).

Also demonstrates `PostReload()` cache invalidation: a `static Dictionary<string,SubRoleResource> _cache` is populated on first access and cleared in an override of `PostReload()` so the editor's asset-hot-reload flushes stale entries without a restart.

```csharp
[AssetType( Name = "SubRole", Extension = "subrole", Category = "Despawn" )]
public sealed class SubRoleResource : GameResource
{
    [Property] public string Key       { get; set; }   // "clairvoyant"
    [Property] public bool   Enabled   { get; set; } = true;
    [Property] public bool   AlwaysAssigned { get; set; }  // one per murderer
    [Property] public EquipmentResource Equipment { get; set; }
    [Property] public GameObject        BehaviorPrefab { get; set; }

    static Dictionary<string, SubRoleResource> _cache;
    public static SubRoleResource Find( string key ) =>
        ( _cache ??= ResourceLibrary.GetAll<SubRoleResource>()
            .Where( r => r.Enabled )
            .ToDictionary( r => r.Key ) )
        .GetValueOrDefault( key );

    protected override void PostReload() { _cache = null; }  // flush on hot-reload
}
```

Anti-pattern: building a cache in a static property without a `PostReload()` flush. In the editor, designers hot-reload assets constantly; a stale `_cache` means code keeps reading the old values until the game is restarted.

### Pattern: per-asset metadata resource matched at runtime by scene identity (despawn.murder)

`MapResource` (`.mapvote`) stores per-map metadata including Director knobs (`ClueSpawnMultiplier`, `ClueSpawnMultiplierMax`). `GetCurrent(scene)` matches by `scene.Source` — the loaded scene file path — so map-specific tuning never requires a switch statement in game code (despawn.murder: MapResource.cs).

```csharp
public static MapResource GetCurrent( Scene scene ) =>
    ResourceLibrary.GetAll<MapResource>()
        .FirstOrDefault( m => m.SceneFile?.ResourcePath == scene.Source );
```

### Pattern: GameResource as a pure data sheet for a modifier bus (thefancylads.farm_land)

`Buff` (`.buff`) is a `GameResource` whose only payload is `Dictionary<string, BuffEffectData>`, where each entry is `{ float Value; BuffOperationType Multiply | Add | Set }`. The dotted key namespace (`farming.yield.vegetable`, `economy.market.{itemId}.sellprice`) is documented in the source. Gameplay code never names a buff; it calls `BuffManager.Instance.GetModifier("farming.mutation.chance")` and folds the stack. Designers author buffs as assets; code reads by key. The `[Icon("eco")]` decorator on `CropResource` and `[Icon("backpack")]` on `ItemResource` add recognizable icons to the asset browser (thefancylads.farm_land: Buff.cs, BuffManager.cs, CropResource.cs).

```csharp
[AssetType( Name = "Buff", Extension = "buff", Icon = "auto_awesome" )]
public sealed class Buff : GameResource
{
    [Property] public string DisplayName { get; set; }
    [Property] public Dictionary<string, BuffEffectData> Effects { get; set; } = new();
}

// runtime read — no enum, no switch, no per-buff code
float yield = baseYield * BuffManager.Instance.GetModifier( "farming.yield.vegetable" );
```

### Pattern: `[Flags]` enum category mask on GameResource items (thefancylads.farm_land)

`ItemCategory` is a `[Flags]` enum. `ItemResource` carries `ItemCategory Category`. A composite value `Crop = Vegetable | Salad | Grain | Berry | Special` lets a single filter match any crop subtype without enumerating each one. Extension helpers `IsIncludedInAny(mask)` / `Includes(flag)` keep call sites readable (thefancylads.farm_land: ItemResource.cs, ItemCategory.cs).

```csharp
[Flags] public enum ItemCategory { None=0, Vegetable=1, Salad=2, Grain=4, Berry=8, Special=16,
    Crop = Vegetable | Salad | Grain | Berry | Special }

// filter a ResourceLibrary lookup to only crop items:
var crops = ResourceLibrary.GetAll<ItemResource>().Where( i => i.Category.IsIncludedInAny( ItemCategory.Crop ) );
```

### Counter-pattern: when NOT to use GameResource (facepunch.ss2)

`facepunch.ss2` intentionally uses **zero `GameResource` types** for meta-progression data. All shop items, gem definitions, quest defs, and achievement defs live as `static` C# structs/POCOs (`ShopItemDef`, `QuestDef`) assembled in partial-class builders and serialized to `progress.json` via `FileSystem.Data.ReadJsonSafe<T>` / `WriteJson`. The reason: the catalog is code-bound (upgrade behavior = overriding a base class method), not designer-editable, so a `.vcfg` file per perk would add zero value and hundreds of files. **Rule of thumb: prefer GameResource when content is designer-editable and behavior-free; prefer code+JSON when content and behavior are inseparable** (facepunch.ss2: ProgressManager.cs, ShopItemDef.cs).

### Counter-pattern: reflection-driven catalog as an alternative to ResourceLibrary (facepunch.ss2)

When all variants of a type are defined in code (not files), use `TypeLibrary.GetTypes<T>()` + an attribute to build the pool rather than `ResourceLibrary.GetAll<T>()`. `[Perk(Rarity, curse, locked, ...)]` decorates each of 300+ perk classes; `PerkManager` builds the weighted pool at startup via `TypeLibrary.GetTypes<Perk>()`. Adding a perk = adding a file; no registry, no hand-list. The `[AssetType]`+`ResourceLibrary` path is for file assets; the `[Attribute]`+`TypeLibrary` path is for code-only catalogs (facepunch.ss2: PerkManager.cs, Perk.cs).

```csharp
// code-only catalog — no .vcfg files, no ResourceLibrary
var pool = TypeLibrary.GetTypes<Perk>()
    .Where( t => !t.IsAbstract )
    .Select( t => t.GetAttribute<PerkAttribute>() )
    .Where( a => a is not null && !a.Locked );
```

### Updated gotcha table additions

| Gotcha | Fix |
| --- | --- |
| `GameResource` cache not flushed on hot-reload; designers see stale values in-editor | Override `PostReload()` and null/clear the cache (despawn.murder: SubRoleResource.cs) |
| Per-role/per-type branching in spawn/assignment code explodes as content grows | Store `BehaviorPrefab` on the resource; clone + `NetworkSpawn` generically; zero branching (despawn.murder: SubRoleResource.cs) |
| Adding 300 perk files as `.vcfg` assets for code-bound upgrades is busywork | Use `TypeLibrary.GetTypes<Perk>()` + `[PerkAttribute]` for code-only catalogs (facepunch.ss2: PerkManager.cs) |
| Per-map tuning requires a switch in game code | Author a `MapResource` per map; resolve at runtime with `GetCurrent(scene)` by `scene.Source` (despawn.murder: MapResource.cs) |

**Read these games** for data-asset patterns: `despawn.murder` (multi-type GameResource DSL, behavior-prefab dispatch, `PostReload` cache, map metadata), `thefancylads.farm_land` (GameResource as modifier-bus data sheet, `[Flags]` category mask, `[Icon]` decorator), `facepunch.ss2` (code-only catalog via `TypeLibrary`, explicit no-GameResource meta-progression). Prior: `sbox-vehicle-kit` (suffix-filter gotcha), `simple-weapon-base` (prefab registry + `BreakFromPrefab`), `SBox-Visual-Novel-Base` (editor tooling).
