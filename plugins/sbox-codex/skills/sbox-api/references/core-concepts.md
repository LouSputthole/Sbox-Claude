# Core Concepts

Scene system, GameObjects, Components, lifecycle, Properties, Prefabs, events, and systems.

## Architecture Overview

s&box uses a **Scene > GameObject > Component** hierarchy. A `Scene` contains `GameObjects`. Each `GameObject` has a transform and zero or more `Components` that provide behavior. All gameplay code lives in `Component` subclasses.

`Scene` itself extends `GameObject` — it IS the root GameObject.

---

## GameObjects

A `GameObject` is a container in the scene. It has a transform, tags, children, and components.

### Creating & Destroying

```csharp
// Create empty GameObject
var go = new GameObject();
go.Name = "MyObject";

// Create as child
var child = new GameObject( true, "Child" );
child.SetParent( go );

// Destroy
go.Destroy();

// Check if still valid (not destroyed)
if ( go.IsValid() ) { /* safe to use */ }
```

### Transform

Transform is relative to parent. World accessors compute the absolute transform.

```csharp
// World space
go.WorldPosition = new Vector3( 100, 0, 50 );
go.WorldRotation = Rotation.FromAxis( Vector3.Up, 90f );
go.WorldScale = Vector3.One * 2f;

// Local space (relative to parent)
go.LocalPosition = new Vector3( 10, 0, 0 );
go.LocalRotation = Rotation.Identity;
go.LocalScale = Vector3.One;

// Full transform struct
go.WorldTransform = new Transform( position, rotation, scale );
```

`GameTransform` wraps the transform with interpolation support:
- `GameObject.Transform.Position` / `.Rotation` / `.Scale` — world coordinates
- `GameObject.Transform.LocalPosition` / `.LocalRotation` / `.LocalScale` — local coordinates
- `Transform.LerpTo( Transform target, float frac )` — smooth interpolation
- `Transform.ClearInterpolation()` — snap to final position

### Tags

Tags are strings. They are **inherited** — children have all parent tags. Removing a tag from a child requires removing it from the parent.

```csharp
go.Tags.Add( "enemy" );
go.Tags.Remove( "enemy" );
go.Tags.Set( "enemy", isEnemy );  // conditional
bool has = go.Tags.Has( "enemy" );
```

### Children & Hierarchy

```csharp
go.Children                              // List<GameObject>
go.Parent                                // GameObject or null
go.SetParent( other, keepWorldPosition: true );
go.IsRoot                                // true if parented to scene
go.Root                                  // root ancestor
go.IsDescendant( other )                 // hierarchy check
go.IsAncestor( other )
```

### Key Properties

| Property | Type | Description |
|----------|------|-------------|
| `Scene` | `Scene` | The scene this object belongs to |
| `Enabled` | `bool` | Local enabled state |
| `Active` | `bool` | Enabled AND all ancestors enabled |
| `IsValid` | `bool` | Not destroyed |
| `IsProxy` | `bool` | Networked & owned by another client |
| `Id` | `Guid` | Unique identifier |
| `Flags` | `GameObjectFlags` | `Hidden`, `NotSaved`, `DontDestroyOnLoad`, etc. |
| `Network` | `NetworkAccessor` | Networking interface |

---

## Components

All gameplay code extends `Component`. A component is attached to exactly one `GameObject`.

### Writing a Custom Component

```csharp
public sealed class MyComponent : Component
{
    [Property] public float Speed { get; set; } = 200f;
    [Property] public GameObject Target { get; set; }

    protected override void OnUpdate()
    {
        if ( Target is null || !Target.IsValid() ) return;

        var direction = (Target.WorldPosition - WorldPosition).Normal;
        WorldPosition += direction * Speed * Time.Delta;
    }
}
```

Key patterns:
- `sealed class` — seal your components unless you need inheritance
- `[Property]` — exposes to editor inspector, serialized/saved
- Access `WorldPosition`, `WorldRotation`, etc. directly (shortcuts for `GameObject.WorldPosition`)
- Access `Scene`, `GameObject`, `Transform`, `Components`, `Tags` directly from any component

### Adding & Querying Components

```csharp
// Add (optional: startEnabled parameter, defaults true)
var renderer = go.AddComponent<ModelRenderer>();
var renderer = go.GetOrAddComponent<ModelRenderer>();  // idempotent

// Query single (optional: includeDisabled, defaults false)
var c = go.GetComponent<ModelRenderer>();
var c = go.GetComponentInChildren<ModelRenderer>();   // also: includeDisabled, includeSelf
var c = go.GetComponentInParent<ModelRenderer>();     // also: includeDisabled, includeSelf

// Query multiple (same optional params)
var all = go.GetComponents<ModelRenderer>();
var all = go.GetComponentsInChildren<ModelRenderer>();
var all = go.GetComponentsInParent<ModelRenderer>();

// Advanced: FindMode flags
var c = go.Components.Get<ModelRenderer>( FindMode.Disabled | FindMode.InAncestors );
var all = go.Components.GetAll<ModelRenderer>( FindMode.Enabled | FindMode.InSelf | FindMode.InChildren );
var everything = go.Components.GetAll();  // all components on this GameObject

// Scene-wide fast lookup
var game = Scene.Get<GameManager>();
foreach ( var model in Scene.GetAll<ModelRenderer>() ) { }
```

### Removing / Destroying

```csharp
component.Destroy();           // remove component from its GameObject
component.DestroyGameObject(); // destroy the entire GameObject
// also: GameObject.Destroy()
```

### Component References in Inspector

```csharp
// Drag-and-drop reference in editor
[Property] ModelRenderer BodyRenderer { get; set; }

// Auto-create if missing
[RequireComponent] ModelRenderer BodyRenderer { get; set; }
```

---

## Lifecycle Methods

Override these `protected virtual` methods on `Component`. All return `void` except `OnLoad` (async `Task`).

### Execution Order

```
Scene Load
  └─ OnLoad (async) — loading screen stays open until all complete
  └─ OnValidate — after deserialization / property changes
  └─ OnAwake — once, when created, if parent GameObject enabled

Per Frame (for enabled components):
  ┌─ OnStart — once, before first update
  ├─ OnFixedUpdate — every fixed timestep (use for physics/movement)
  ├─ OnUpdate — every frame
  └─ OnPreRender — every frame, after bone calculations (NOT on dedicated server)

State Changes:
  ├─ OnEnabled — when component becomes enabled
  ├─ OnDisabled — when component becomes disabled
  └─ OnDestroy — once, when destroyed
```

**A component is "enabled" only if its own `Enabled` is true AND its `GameObject` and ALL ancestor GameObjects are enabled.**

### Method Details

| Method | When Called | Notes |
|--------|-----------|-------|
| `OnLoad()` | After deserialization | `async Task`. Loading screen waits. Use for procedural generation. |
| `OnValidate()` | Property change / deserialization | Enforce property limits. Not a lifecycle hook. |
| `OnAwake()` | Once, after load, if parent enabled | Initialization. Called before OnStart. |
| `OnStart()` | Once, before first update | Called when enabled for first time. Always before first `OnFixedUpdate`. |
| `OnEnabled()` | Each time component becomes enabled | Setup subscriptions, start effects. |
| `OnUpdate()` | Every frame | General per-frame logic. |
| `OnFixedUpdate()` | Every fixed timestep | Physics, movement, traces. Preferred for `CharacterController` movement. |
| `OnPreRender()` | Every frame, after bone calc | Visual adjustments. **Not called on dedicated server.** |
| `OnDisabled()` | Each time component becomes disabled | Cleanup subscriptions. |
| `OnDestroy()` | Once, when destroyed | Final cleanup. |

### Additional Virtual Methods

| Method | Purpose |
|--------|---------|
| `OnParentChanged(GameObject old, GameObject new)` | Parent hierarchy changed |
| `OnTagsChanged()` | Tags modified |
| `OnRefresh()` | After network snapshot refresh |
| `DrawGizmos()` | Editor-only debug drawing |

### Execution Order Warning

**Do not rely on the order in which the same callback is invoked across different GameObjects.** The order is not predictable. If you need deterministic ordering, use a `GameObjectSystem` with explicit stage/order.

---

## Component Interfaces

Implement these as additional interfaces on your component.

### ExecuteInEditor

Runs `OnAwake`, `OnEnabled`, `OnDisabled`, `OnUpdate`, `OnFixedUpdate` in edit mode.

```csharp
public sealed class MyEditorTool : Component, Component.ExecuteInEditor
{
    protected override void OnUpdate()
    {
        if ( Game.IsEditor ) { /* editor-only logic */ }
    }
}
```

### ICollisionListener

React to physics collisions. Requires a collider on the same or child GameObject.

```csharp
public sealed class HitDetector : Component, Component.ICollisionListener
{
    public void OnCollisionStart( Collision collision ) { }   // first contact
    public void OnCollisionUpdate( Collision collision ) { }  // sustained contact (per physics step)
    public void OnCollisionStop( CollisionStop collision ) { } // separation
}
```

### ITriggerListener

React to trigger volume overlaps.

```csharp
public sealed class TriggerZone : Component, Component.ITriggerListener
{
    public void OnTriggerEnter( Collider other ) { }
    public void OnTriggerExit( Collider other ) { }
}
```

### IDamageable

Standard damage interface. Query with `Components.Get<IDamageable>()`.

```csharp
public sealed class Health : Component, Component.IDamageable
{
    [Property] public float HP { get; set; } = 100f;

    public void OnDamage( in DamageInfo damage )
    {
        HP -= damage.Damage;
        if ( HP <= 0 ) GameObject.Destroy();
    }
}
```

`DamageInfo` has: `float Damage`, `GameObject Attacker`, `Vector3 Position`, and more.

---

## Properties (Editor Attributes)

`[Property]` exposes a field/property to the editor inspector and serializes it. Combine with attributes to control appearance.

### Common Attributes

| Attribute | Effect |
|-----------|--------|
| `[Property]` | Expose to inspector, serialize |
| `[Hide]` | Serialize but hide from inspector |
| `[RequireComponent]` | Auto-create component if missing |
| `[Group( "Name" )]` | Visual grouping in inspector |
| `[ToggleGroup( "BoolPropName" )]` | Group with enable/disable checkbox |
| `[Title( "Display Name" )]` | Override display name |
| `[Range( min, max )]` | Numeric slider with limits (clamped by default) |
| `[Step( n )]` | Numeric increment step |
| `[ReadOnly]` | Display but disallow editing |
| `[ShowIf( "Prop", value )]` | Conditional visibility |
| `[HideIf( "Prop", value )]` | Conditional hiding |
| `[Feature( "Tab" )]` | Separate tab in inspector |
| `[FeatureEnabled( "Tab" )]` | Bool to toggle feature tab |
| `[Order( n )]` | Control property ordering |
| `[Header( "text" )]` | Section header above property |
| `[Space]` | Visual spacer |
| `[InlineEditor]` | Expand struct/class inline |
| `[Advanced]` | Hidden unless user requests |
| `[Flags]` | Multi-select enum |

### String-Specific

| Attribute | Effect |
|-----------|--------|
| `[TextArea]` | Multi-line text input |
| `[Placeholder( "hint" )]` | Placeholder text |
| `[InputAction]` | Dropdown of configured input actions |
| `[ImageAssetPath]` | Image file picker |
| `[MapAssetPath]` | Map file picker |
| `[FontName]` | Font dropdown |
| `[FilePath]` | General file picker |

### Validation

```csharp
[Property, Validate( nameof(IsSpeedValid), "Speed must be positive", LogLevel.Warn )]
public float Speed { get; set; } = 100f;

bool IsSpeedValid() => Speed > 0;
```

---

## Prefabs

A Prefab is a reusable `GameObject` template saved to disk as a `.prefab` file. When the prefab asset updates, all scene instances update too.

### Spawning in Code

```csharp
public sealed class Spawner : Component
{
    [Property] public GameObject Prefab { get; set; }  // drag PrefabFile here

    protected override void OnUpdate()
    {
        if ( Input.Pressed( "attack1" ) )
        {
            // Clone at position
            GameObject instance = Prefab.Clone( WorldPosition );

            // Clone with position + rotation
            GameObject instance2 = Prefab.Clone( WorldPosition, WorldRotation );

            // Break link to prefab source (becomes regular GameObjects)
            instance.BreakFromPrefab();
        }
    }
}
```

`GameObject.Clone()` has 11 overloads — the most common take `Vector3 position` and optionally `Rotation rotation`.

### Instance Overrides

Prefab instances in scenes can override individual properties, add components, or add child GameObjects without affecting the source prefab. Overrides are stored per-instance and preserved when the source prefab updates.

### Static Prefab Loading

```csharp
// Load a prefab by file path
var prefab = GameObject.GetPrefab( "prefabs/bullet.prefab" );
var instance = prefab.Clone( WorldPosition );
```

---

## Scene Events

Broadcast and listen to custom events across all active Components and GameObjectSystems in a scene. Events are **local only** — not sent over the network.

### Defining an Event

```csharp
public interface IPlayerEvent : ISceneEvent<IPlayerEvent>
{
    void OnSpawned( Player player ) { }
    void OnDied( Player player ) { }
}
```

Deriving from `ISceneEvent<T>` adds static `Post()` and `PostToGameObject()` helpers. Default method implementations let listeners opt into only the events they care about.

### Broadcasting

```csharp
// To all listeners in scene
IPlayerEvent.Post( x => x.OnSpawned( player ) );

// To a specific GameObject only
IPlayerEvent.PostToGameObject( target.GameObject, x => x.OnDied( player ) );

// Raw Scene.RunEvent also works on any type
Scene.RunEvent<SkinnedModelRenderer>( x => x.Tint = Color.Red );
```

### Listening

Implement the interface on a Component or GameObjectSystem:

```csharp
public sealed class ScoreTracker : Component, IPlayerEvent
{
    void IPlayerEvent.OnDied( Player player )
    {
        Log.Info( $"{player.Name} died" );
    }
}
```

### Built-in Event Interfaces

| Interface | Events | Use For |
|-----------|--------|---------|
| `ISceneStartup` | `OnHostPreInitialize`, `OnHostInitialize`, `OnClientInitialize` | Scene/game initialization |
| `ISceneLoadingEvents` | `AfterLoad` | Post-scene-load setup |
| `IScenePhysicsEvents` | Physics callbacks | Physics event handling |
| `IGameObjectNetworkEvents` | Network lifecycle | Network state changes |

`ISceneStartup` is critical for game initialization — `OnHostInitialize` fires after the scene loads on host (spawn cameras, start lobbies), `OnClientInitialize` fires on both host and client (spawn client-side objects).

---

## GameObjectSystem

A system that exists once per scene, hooks into specific frame stages, and processes components in bulk. Automatically instantiated for every scene.

### Creating a System

```csharp
public class GravitySystem : GameObjectSystem<GravitySystem>
{
    public GravitySystem( Scene scene ) : base( scene )
    {
        // Listen to a specific stage with explicit order
        Listen( Stage.StartUpdate, 0, ApplyGravity, "ApplyGravity" );
    }

    void ApplyGravity()
    {
        foreach ( var body in Scene.GetAllComponents<GravityBody>() )
        {
            body.Velocity += Vector3.Down * 800f * Time.Delta;
        }
    }
}
```

### Stages

| Stage | When |
|-------|------|
| `StartUpdate` | Beginning of frame update |
| `UpdateBones` | After animations, before rendering |
| `PhysicsStep` | During `FixedUpdate` physics tick |
| `Interpolation` | Transform interpolation pass |
| `FinishUpdate` | End of frame update |
| `StartFixedUpdate` | Beginning of fixed update |
| `FinishFixedUpdate` | End of fixed update |
| `SceneLoaded` | After scene load completes |

### Access

```csharp
// Via generic static property (requires GameObjectSystem<T>)
GravitySystem.Current.DoSomething();

// Via scene lookup
var system = Scene.GetSystem<GravitySystem>();
```

Systems can implement `ISceneStartup` and other event interfaces, making them ideal for game managers.

### Configuration

Properties marked `[Property]` on a system are configurable in Project Settings > Systems and saved per-project.

---

## Async in Components

Async tasks in s&box run on the **main thread** (like coroutines). They are the standard pattern for delayed/sequential operations.

```csharp
protected override void OnStart()
{
    _ = SpawnWaves();  // fire-and-forget from sync code
}

async Task SpawnWaves()
{
    for ( int wave = 0; wave < 10; wave++ )
    {
        SpawnEnemies( wave );
        await Task.DelaySeconds( 30f );  // wait 30 seconds between waves
    }
}
```

Key APIs:
- `await Task.DelaySeconds( float )` — wait game-time seconds
- `await Task.DelayRealtimeSeconds( float )` — wait real-time seconds (available via `Component.Task`)
- `await Task.Frame()` — wait one frame
- `await Task.WhenAll( task1, task2 )` — parallel tasks

**Cancellation:** Components have a `Task` property with auto-cancellation when the GameObject becomes invalid. Always consider what happens if the object is destroyed mid-await.

---

## Unity Anti-Pattern Table

| Unity Pattern (WRONG in s&box) | s&box Pattern (CORRECT) |
|-------------------------------|------------------------|
| `class Foo : MonoBehaviour` | `class Foo : Component` |
| `void Start()` | `protected override void OnStart()` |
| `void Update()` | `protected override void OnUpdate()` |
| `void FixedUpdate()` | `protected override void OnFixedUpdate()` |
| `void OnEnable()` | `protected override void OnEnabled()` |
| `void OnDisable()` | `protected override void OnDisabled()` |
| `void OnDestroy()` | `protected override void OnDestroy()` |
| `void Awake()` | `protected override void OnAwake()` |
| `GetComponent<T>()` | `GetComponent<T>()` (same, but also `Components.Get<T>()` for FindMode) |
| `FindObjectOfType<T>()` | `Scene.Get<T>()` or `Scene.GetAll<T>()` |
| `Instantiate( prefab )` | `prefab.Clone( position )` |
| `transform.position` | `WorldPosition` or `GameObject.WorldPosition` |
| `transform.localPosition` | `LocalPosition` or `GameObject.LocalPosition` |
| `[SerializeField]` | `[Property]` |
| `[HideInInspector]` | `[Hide]` |
| `[Header("X")]` | `[Header("X")]` (same) |
| `[Range(0,1)]` | `[Range(0,1)]` (same) |
| `StartCoroutine()` | `_ = MyAsyncMethod()` (native async/await) |
| `yield return new WaitForSeconds(n)` | `await Task.DelaySeconds(n)` |
| `Debug.Log()` | `Log.Info()` |
| `Destroy( gameObject )` | `GameObject.Destroy()` or `DestroyGameObject()` |
| `gameObject.SetActive( false )` | `GameObject.Enabled = false` |
| `Application.isPlaying` | `Game.IsEditor` (inverted sense) |
| `SceneManager.LoadScene()` | `Scene.Load()` or `Scene.LoadFromFile()` |
| `DontDestroyOnLoad( go )` | `go.Flags = GameObjectFlags.DontDestroyOnLoad` |
| `Physics.Raycast()` | `Scene.Trace.Ray()` (see input-and-physics.md) |
| `OnCollisionEnter()` | Implement `Component.ICollisionListener` interface |
| `OnTriggerEnter()` | Implement `Component.ITriggerListener` interface |

---

## .NET Restrictions

s&box whitelists allowed .NET APIs for security. Key restrictions:

| Blocked | Alternative |
|---------|-------------|
| `System.IO.*` (file I/O) | Use s&box Filesystem API |
| `Console.Log` | `Log.Info()` |
| Raw sockets, HTTP clients | Use s&box `Http` class |

Editor-only code and libraries are exempt. Standalone games can opt out but cannot publish to the platform with the whitelist disabled.
