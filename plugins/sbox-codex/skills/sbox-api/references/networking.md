# Networking

RPCs, [Sync] properties, ownership, authority, host vs client, replication, lobbies, and dedicated servers.

---

## Overview

s&box networking is **intentionally simple** — owner-authoritative by default. The owner of a networked object controls its position, rotation, and [Sync] properties. If unowned, the host simulates it.

**Key concepts:**
- **Host** — the computer running the game (player in singleplayer, lobby host, or dedicated server)
- **Client** — a connected player who is not the host
- **Owner** — the connection that simulates a networked object
- **Proxy** — a networked object you don't own (`IsProxy == true`)
- **NetworkMode** — how a GameObject participates in networking

---

## Lobby & Connection

### Creating a Lobby

```csharp
Networking.CreateLobby( new LobbyConfig
{
    MaxPlayers = 8,
    Privacy = LobbyPrivacy.Public,
    Name = "My Game"
} );
```

`LobbyConfig` properties: `MaxPlayers`, `Privacy` (`Public`/`Private`/`FriendsOnly`), `Name`, `Hidden`, `DestroyWhenHostLeaves`, `AutoSwitchToBestHost`.

### Querying & Joining

```csharp
// List all lobbies for this game
var lobbies = await Networking.QueryLobbies();

// Join by lobby ID
Networking.Connect( lobby.LobbyId );

// Join best available lobby
await Networking.JoinBestLobby( gameIdent );

// Disconnect
Networking.Disconnect();
```

`LobbyInformation` has: `LobbyId`, `OwnerId`, `Members`, `MaxMembers`, `Name`, `Map`, `Game`, `IsFull`, `IsHidden`.

### Connection

```csharp
Connection.Local       // your own connection
Connection.All         // all connected players (IReadOnlyList<Connection>)
Connection.Host        // the host connection
Connection.Find( id )  // find by Guid
```

| Property | Type | Description |
|----------|------|-------------|
| `Id` | `Guid` | Unique identifier |
| `DisplayName` | `string` | Player display name |
| `SteamId` | `SteamId` | Steam ID |
| `IsHost` | `bool` | Is this the host |
| `IsActive` | `bool` | Fully connected |
| `IsConnecting` | `bool` | Still connecting |
| `Ping` | `float` | Latency in ms |
| `CanSpawnObjects` | `bool` | Permission to spawn networked objects (default: true) |
| `CanRefreshObjects` | `bool` | Permission to refresh owned objects |

Key methods:
- `Kick( string reason )` — host only
- `HasPermission( string permission )` → `bool`
- `Down/Pressed/Released( string action )` — query remote player's input (host only)

### Networking Static

| Property | Description |
|----------|-------------|
| `Networking.IsHost` | True if you're the host (or not connected) |
| `Networking.IsClient` | Connected and NOT host |
| `Networking.IsActive` | Currently connected |
| `Networking.IsConnecting` | Currently connecting |
| `Networking.ServerName` | Server name |
| `Networking.MaxPlayers` | Max players |

---

## Networked Objects

### NetworkMode

Set in inspector or code. Determines how a GameObject participates in networking.

| Mode | Behavior |
|------|----------|
| `NetworkMode.Never` | Never networked |
| `NetworkMode.Object` | Own networked object — has owner, [Sync] properties, RPCs |
| `NetworkMode.Snapshot` **(default)** | Sent as part of initial scene snapshot when client joins |

### Spawning on Network

```csharp
// Create and network-spawn a prefab
var go = PlayerPrefab.Clone( spawnPoint.WorldPosition );
go.NetworkSpawn();                    // caller becomes owner
go.NetworkSpawn( connection );        // specific owner
go.NetworkSpawn( new NetworkSpawnOptions
{
    Owner = connection,
    OrphanedMode = NetworkOrphaned.Host,
    OwnerTransfer = OwnerTransfer.Takeover,
    AlwaysTransmit = true
} );
```

After `NetworkSpawn()`, changes to components/hierarchy are **not** automatically networked. Call `Network.Refresh()` to push structural changes.

### Destroying

```csharp
go.Destroy();  // works for networked objects too
```

---

## [Sync] Properties

Automatically synchronize property values from owner to all clients. Only the owner can change synced properties (unless `SyncFlags.FromHost`).

```csharp
public sealed class PlayerStats : Component
{
    [Sync] public int Kills { get; set; }
    [Sync] public string PlayerName { get; set; }
    [Sync] public Vector3 AimDirection { get; set; }
    [Sync( SyncFlags.FromHost )] public int TeamId { get; set; }     // only host can change
    [Sync( SyncFlags.Interpolate )] public float Health { get; set; } // smoothly interpolated
}
```

### Supported Types

Unmanaged value types (`int`, `bool`, `float`, `Vector3`, `Rotation`, `Transform`, `Color`, `Angles`, etc.), `string`, `GameObject`, `Component`, `GameResource`, and structs of supported types.

### SyncFlags

| Flag | Effect |
|------|--------|
| `SyncFlags.FromHost` | Host controls value instead of object owner |
| `SyncFlags.Query` | Check for changes each tick (use when backing field modified outside setter) |
| `SyncFlags.Interpolate` | Smooth interpolation between ticks. Works with: `float`, `double`, `Angles`, `Rotation`, `Transform`, `Vector3` |

### Change Detection

```csharp
[Sync, Change( "OnHealthChanged" )]
public float Health { get; set; } = 100f;

void OnHealthChanged( float oldValue, float newValue )
{
    Log.Info( $"Health changed from {oldValue} to {newValue}" );
}
```

### Networked Collections

```csharp
[Sync] public NetList<int> Inventory { get; set; } = new();
[Sync] public NetDictionary<string, int> AmmoCount { get; set; } = new();
```

`NetList<T>` and `NetDictionary<K,V>` work like their standard counterparts (`Add`, `Remove`, indexers, etc.). Initialize with `new()` on the owner. They do NOT support `[Property]`.

---

## RPC Messages

Remote Procedure Calls — a method that when called, executes on remote machines too. RPCs can be on Components or static classes.

### [Rpc.Broadcast]

Called on ALL clients and host:

```csharp
[Rpc.Broadcast]
public void PlayHitEffect( Vector3 position, Vector3 normal )
{
    // Runs on everyone
    var effect = HitEffectPrefab.Clone( position );
    effect.WorldRotation = Rotation.LookAt( normal );
}
```

### [Rpc.Host]

Called on the **host only**:

```csharp
[Rpc.Host]
public void RequestSpawn()
{
    // Only runs on host — safe for authoritative logic
    var player = PlayerPrefab.Clone( GetSpawnPoint() );
    player.NetworkSpawn( Rpc.Caller );
}
```

### [Rpc.Owner]

Called on the **owner** of the object (or host if no owner):

```csharp
[Rpc.Owner]
public void NotifyHit( float damage )
{
    // Only runs on the object's owner
    Health -= damage;
}
```

### Static RPCs

```csharp
[Rpc.Broadcast]
public static void AnnounceMessage( string message )
{
    Log.Info( message );
}
```

### NetFlags

Pass flags to control delivery:

```csharp
[Rpc.Broadcast( NetFlags.Unreliable )]
public void UpdatePosition( Vector3 pos ) { }

[Rpc.Host( NetFlags.OwnerOnly )]  // only owner can call this
public void DealDamage( float amount ) { }
```

| Flag | Description |
|------|-------------|
| `NetFlags.Reliable` | **Default.** Guaranteed delivery. For important events. |
| `NetFlags.Unreliable` | May not arrive, may be out of order. Fast/cheap. For effects, position updates. |
| `NetFlags.SendImmediate` | Not batched — sent right away. For voice streaming. |
| `NetFlags.DiscardOnDelay` | Drop if can't send quickly. Unreliable only. |
| `NetFlags.HostOnly` | Only host may call this RPC. |
| `NetFlags.OwnerOnly` | Only owner of the object may call this RPC. |

### Filtering Recipients

Filter which connections receive a Broadcast RPC:

```csharp
// Exclude specific connections
using ( Rpc.FilterExclude( c => c.DisplayName == "Harry" ) )
{
    PlayEffect();
}

// Include only specific connections
using ( Rpc.FilterInclude( targetConnection ) )
{
    SendPrivateMessage( message );
}
```

### Caller Information

Inside an RPC, check who called it:

```csharp
[Rpc.Broadcast]
public void SendChatMessage( string message )
{
    if ( Rpc.Calling )  // true if called from remote connection
    {
        Log.Info( $"{Rpc.Caller.DisplayName}: {message}" );
    }
}
```

`Rpc.Caller` returns the calling `Connection`. `Rpc.CallerId` returns their `Guid`. `Rpc.Calling` is `true` when invoked remotely.

### Supported Argument Types

Same as [Sync] properties: unmanaged types, `string`, `GameObject`, `Component`, `GameResource`.

---

## Ownership

### Who Controls What

| Situation | Controller |
|-----------|-----------|
| Scene object (no explicit owner) | Host |
| `NetworkSpawn()` by a client | That client becomes owner |
| `NetworkSpawn( connection )` | Specified connection |
| Owner disconnects | Depends on `NetworkOrphaned` mode |

### The IsProxy Pattern

The most important ownership check — skip simulation for objects you don't own:

```csharp
protected override void OnUpdate()
{
    if ( IsProxy ) return;  // someone else controls this

    // Your movement/input code here
    HandleInput();
}
```

### Ownership Transfer

```csharp
go.Network.TakeOwnership();                // become the owner
go.Network.DropOwnership();                // release to host
go.Network.AssignOwnership( connection );  // give to specific client (host only by default)
```

### OwnerTransfer Mode

Controls who can change ownership:

```csharp
go.Network.SetOwnerTransfer( OwnerTransfer.Takeover );
```

| Mode | Who Can Transfer |
|------|------------------|
| `OwnerTransfer.Fixed` **(default)** | Host only |
| `OwnerTransfer.Takeover` | Anyone |
| `OwnerTransfer.Request` | Must request from host |

### Orphaned Mode (Disconnect Handling)

What happens when the owner disconnects:

```csharp
go.Network.SetOrphanedMode( NetworkOrphaned.Host );
```

| Mode | Behavior |
|------|----------|
| `NetworkOrphaned.Destroy` **(default)** | Object destroyed |
| `NetworkOrphaned.Host` | Host becomes owner |
| `NetworkOrphaned.Random` | Random client becomes owner |
| `NetworkOrphaned.ClearOwner` | No owner, host simulates |

### Network Accessor (go.Network)

| Property | Type | Description |
|----------|------|-------------|
| `Active` | `bool` | Is this object networked |
| `IsOwner` | `bool` | Are we the owner |
| `Owner` | `Connection` | Owner connection (null if none) |
| `OwnerId` | `Guid` | Owner's connection ID |
| `IsProxy` | `bool` | Controlled by someone else |
| `IsCreator` | `bool` | Did we create this object |
| `OwnerTransfer` | `OwnerTransfer` | Who can transfer ownership |
| `NetworkOrphaned` | `NetworkOrphaned` | Disconnect behavior |
| `AlwaysTransmit` | `bool` | Always send updates (default: true) |
| `Interpolation` | `bool` | Smooth transform interpolation |
| `Flags` | `NetworkFlags` | Additional flags |

Key methods:
- `TakeOwnership()` → `bool`
- `AssignOwnership( Connection )` → `bool`
- `DropOwnership()` → `bool`
- `Refresh()` — push structural changes to clients
- `Refresh( Component )` — refresh specific component
- `ClearInterpolation()` — snap to position (teleport)
- `SetOwnerTransfer( OwnerTransfer )`
- `SetOrphanedMode( NetworkOrphaned )`

### NetworkFlags

| Flag | Effect |
|------|--------|
| `NetworkFlags.NoInterpolation` | Disable transform interpolation |
| `NetworkFlags.NoPositionSync` | Don't sync position |
| `NetworkFlags.NoRotationSync` | Don't sync rotation |
| `NetworkFlags.NoScaleSync` | Don't sync scale |
| `NetworkFlags.NoTransformSync` | Don't sync any transform |

---

## Transform Interpolation

Networked transforms are interpolated smoothly by default. To teleport:

```csharp
WorldPosition = newPosition;
Network.ClearInterpolation();  // snap immediately for all clients
```

To disable interpolation entirely:

```csharp
go.Network.Interpolation = false;
```

---

## Network Events

### INetworkListener (Host Only)

React to player connections/disconnections. Implement on a Component in the scene.

```csharp
public sealed class GameManager : Component, Component.INetworkListener
{
    [Property] public GameObject PlayerPrefab { get; set; }

    public void OnActive( Connection connection )
    {
        // Player fully loaded — spawn their character
        var player = PlayerPrefab.Clone( GetSpawnPoint() );
        player.NetworkSpawn( connection );
    }

    public void OnDisconnected( Connection connection )
    {
        Log.Info( $"{connection.DisplayName} left" );
    }

    public bool AcceptConnection( Connection connection, ref string reason )
    {
        if ( IsBanned( connection ) )
        {
            reason = "You are banned";
            return false;
        }
        return true;
    }
}
```

| Method | When Called |
|--------|-----------|
| `AcceptConnection( Connection, ref string reason )` | On host, to accept/deny. Return false to reject. |
| `OnConnected( Connection )` | Client connected (still loading) |
| `OnActive( Connection )` | Client fully loaded, entering game |
| `OnDisconnected( Connection )` | Client left |
| `OnBecameHost( Connection previousHost )` | You are now the host (previous host left) |

### INetworkSpawn

Called when an ancestor `GameObject` is network-spawned:

```csharp
public sealed class WeaponSetup : Component, Component.INetworkSpawn
{
    public void OnNetworkSpawn( Connection owner )
    {
        // Initialize weapon for the new owner
    }
}
```

### IGameObjectNetworkEvents

Ownership change events, targeted at the specific GameObject:

```csharp
public sealed class OwnerTracker : Component, IGameObjectNetworkEvents
{
    void IGameObjectNetworkEvents.NetworkOwnerChanged( Connection newOwner, Connection previousOwner ) { }
    void IGameObjectNetworkEvents.StartControl() { }   // we just became controller (no longer proxy)
    void IGameObjectNetworkEvents.StopControl() { }    // we just became proxy
}
```

### INetworkSnapshot (Custom Snapshot Data)

Send custom data (voxels, world state) to joining clients:

```csharp
public sealed class VoxelWorld : Component, Component.INetworkSnapshot
{
    byte[] VoxelData;

    void INetworkSnapshot.WriteSnapshot( ref ByteStream writer )
    {
        writer.Write( VoxelData.Length );
        writer.WriteArray( VoxelData );
    }

    void INetworkSnapshot.ReadSnapshot( ref ByteStream reader )
    {
        var length = reader.Read<int>();
        VoxelData = reader.ReadArray<byte>( length ).ToArray();
    }
}
```

---

## Network Visibility

By default, all networked objects transmit to all connections. For larger games, disable `AlwaysTransmit` and implement `INetworkVisible`:

```csharp
public sealed class DistanceCulling : Component, Component.INetworkVisible
{
    public bool IsVisibleToConnection( Connection connection, in BBox worldBounds )
    {
        return connection.DistanceSquared( WorldPosition ) < 5000f * 5000f;
    }
}
```

When culled: sync vars and transforms stop updating, but the object still exists on the client (disabled). RPCs still deliver.

Hammer maps with VIS compiled use PVS automatically as fallback.

---

## NetworkHelper Component

A ready-made component for simple multiplayer setup:

```csharp
// Add to a GameObject in your scene
// Set PlayerPrefab in inspector
// Set StartServer = true
// Optionally add SpawnPoint components to the scene
```

| Property | Description |
|----------|-------------|
| `StartServer` | Auto-create lobby on scene load |
| `PlayerPrefab` | Prefab spawned for each player |
| `SpawnPoints` | List of spawn locations (random selection) |

Uses `INetworkListener.OnActive` internally to spawn and assign player prefabs.

---

## Scene Startup (ISceneStartup)

For game initialization on host. Best implemented on a `GameObjectSystem`:

```csharp
public sealed class GameManager : GameObjectSystem<GameManager>, ISceneStartup
{
    public GameManager( Scene scene ) : base( scene ) { }

    void ISceneStartup.OnHostPreInitialize( SceneFile scene )
    {
        // Before scene loads (host only) — scene is empty
    }

    void ISceneStartup.OnHostInitialize()
    {
        // After scene loads (host only) — spawn cameras, start lobby
        Networking.CreateLobby();
    }

    void ISceneStartup.OnClientInitialize()
    {
        // After scene loads (host + client, NOT dedicated server)
        // Spawn client-side only objects (mark as not networked!)
    }
}
```

---

## Dedicated Servers

### Server-Side Code

Use `#if SERVER` blocks or `.Server.cs` file naming for host-only code (stripped from published client builds):

```csharp
#if SERVER
public void AdminCommand()
{
    // This code only exists on the server
}
#endif
```

### User Permissions

Configure via `users/config.json`:

```json
{
    "users": {
        "76561198000000000": {
            "permissions": ["admin", "moderator"],
            "claims": { "role": "admin" }
        }
    }
}
```

Check with `connection.HasPermission( "admin" )`.

---

## Common Patterns

### Player Controller with Networking

```csharp
public sealed class MyPlayer : Component, Component.INetworkSpawn
{
    [Sync] public string DisplayName { get; set; }
    [Sync] public int Score { get; set; }
    [Property] public float Speed { get; set; } = 200f;

    public void OnNetworkSpawn( Connection owner )
    {
        DisplayName = owner.DisplayName;
    }

    protected override void OnFixedUpdate()
    {
        if ( IsProxy ) return;  // don't control other players

        var wishDir = Input.AnalogMove.Normal;
        WorldPosition += wishDir * Speed * Time.Delta;
    }

    [Rpc.Broadcast]
    public void PlayJumpEffect()
    {
        // Everyone sees the effect
        Sound.Play( "player.jump", WorldPosition );
    }

    [Rpc.Host]
    public void RequestDamage( GameObject target, float amount )
    {
        // Only host processes damage — authoritative
        var health = target.GetComponent<HealthComponent>();
        health?.TakeDamage( amount );
    }
}
```

### Quick Reference

| Task | Code |
|------|------|
| Check if host | `Networking.IsHost` |
| Check if proxy | `IsProxy` (on Component/GameObject) |
| Get all connections | `Connection.All` |
| Get local connection | `Connection.Local` |
| Get host connection | `Connection.Host` |
| Spawn networked object | `go.NetworkSpawn()` |
| Spawn for specific player | `go.NetworkSpawn( connection )` |
| Take ownership | `go.Network.TakeOwnership()` |
| Drop ownership | `go.Network.DropOwnership()` |
| Check if networked | `go.Network.Active` |
| Teleport (no lerp) | `WorldPos = x; Network.ClearInterpolation()` |
| Push structural changes | `go.Network.Refresh()` |
| Create lobby | `Networking.CreateLobby( config )` |
| Disconnect | `Networking.Disconnect()` |
