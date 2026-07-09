import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Networking tools: add_network_helper, configure_network, get_network_status,
 * network_spawn, set_ownership, add_sync_property, add_rpc_method,
 * create_networked_player, create_lobby_manager, create_network_events.
 *
 * Manages s&box multiplayer: lobby creation, networked objects, RPCs, sync properties.
 */
export function registerNetworkingTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── add_network_helper ────────────────────────────────────────────
  server.tool(
    "add_network_helper",
    "Add a NetworkHelper component (with StartServer=true) to an existing GameObject for quick multiplayer setup — at runtime it creates the lobby and spawns the player prefab per connection. Returns { added, id, component:'NetworkHelper' }. NOTE: the current handler requires id (create a holder with create_gameobject first) and does not apply maxPlayers/playerPrefab — wire PlayerPrefab afterward with set_prefab_ref/set_property",
    {
      id: z
        .string()
        .optional()
        .describe("GUID of the GameObject to attach to. Required in practice — the current handler errors when omitted (create a holder with create_gameobject first)"),
      name: z
        .string()
        .optional()
        .describe("Rename the target GameObject to this. Omit to keep its current name"),
      maxPlayers: z
        .number()
        .optional()
        .describe("Maximum number of players in the lobby (currently not applied by the handler)"),
      playerPrefab: z
        .string()
        .optional()
        .describe("Path to the player prefab to spawn for each connection (currently not applied by the handler — set the NetworkHelper's PlayerPrefab afterward with set_prefab_ref)"),
    },
    async (params) => {
      const res = await bridge.send("add_network_helper", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── configure_network ─────────────────────────────────────────────
  server.tool(
    "configure_network",
    "Configure lobby settings on Sandbox.Networking. Currently only lobbyName is applied (sets Networking.ServerName) — Networking.MaxPlayers is read-only on this SDK, and playerPrefab/startServer are not applied by the handler (use add_network_helper + set_prefab_ref for those). Returns { configured, maxPlayers, serverName } reflecting the live Networking values",
    {
      maxPlayers: z
        .number()
        .optional()
        .describe("Maximum number of players (currently not applied — Networking.MaxPlayers is read-only on this SDK; the live value is echoed in the response)"),
      lobbyName: z
        .string()
        .optional()
        .describe("Display name for the lobby (sets Networking.ServerName — the only setting this handler applies)"),
      playerPrefab: z
        .string()
        .optional()
        .describe("Path to the player prefab (currently not applied by the handler — set the NetworkHelper's PlayerPrefab via set_prefab_ref instead)"),
      startServer: z
        .boolean()
        .optional()
        .describe("Start the server/lobby immediately (currently not applied by the handler — add_network_helper sets StartServer=true on the NetworkHelper)"),
    },
    async (params) => {
      const res = await bridge.send("configure_network", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── get_network_status ────────────────────────────────────────────
  server.tool(
    "get_network_status",
    "Check the current multiplayer status. Returns { isActive, isHost, isClient, isConnecting, maxPlayers, serverName } read from Sandbox.Networking — meaningful mostly in play mode with networking active. It does NOT return a player list or networked-object dump; use inspect_networked_object for a specific object's Network/[Sync] state",
    {},
    async (params) => {
      const res = await bridge.send("get_network_status", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── network_spawn ─────────────────────────────────────────────────
  server.tool(
    "network_spawn",
    "Network-enable a GameObject so it is synchronized across all connected clients. Calls NetworkSpawn(). Returns { spawned, id } on success — follow with inspect_networked_object to confirm the Network state, or set_ownership to hand it to a connection",
    {
      id: z.string().describe("GUID of the GameObject to network"),
    },
    async (params) => {
      const res = await bridge.send("network_spawn", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_ownership ─────────────────────────────────────────────────
  server.tool(
    "set_ownership",
    "Assign network ownership of a GameObject to a connection, or drop ownership. Omitting connectionId (or passing an empty string) calls Network.DropOwnership(); passing a connection Id or SteamId calls Network.AssignOwnership() on the matching live Connection. Returns { ownershipAssigned, id, connectionId } or { ownershipDropped, id }; errors if no connection matches (connections only exist while networking is active)",
    {
      id: z.string().describe("GUID of the networked GameObject"),
      connectionId: z
        .string()
        .optional()
        .describe(
          "Connection Id GUID or SteamId of the target connection. Omit OR pass an empty string to DROP ownership (there is no take-ownership mode in the current handler)"
        ),
    },
    async (params) => {
      const res = await bridge.send("set_ownership", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── add_sync_property ─────────────────────────────────────────────
  server.tool(
    "add_sync_property",
    "Annotate an EXISTING public property in a C# script with the [Sync] attribute so s&box replicates it across the network. This does NOT create a new property — the property named by `propertyName` must already be declared in the file; the tool only inserts the [Sync] attribute above it. Returns { added, path, property, attribute } — attribute echoes the exact [Sync...] emitted; errors if the property already has [Sync] or isn't found. Follow with trigger_hotload, then get_compile_errors",
    {
      path: z
        .string()
        .describe("Relative path to the script file (e.g. 'code/Player.cs')"),
      propertyName: z
        .string()
        .describe("Name of the existing public property to annotate with [Sync]"),
      propertyType: z
        .string()
        .optional()
        .describe(
          "Currently ignored — not yet implemented. The addon only annotates an existing property; it does not declare a new one, so the type comes from the existing declaration"
        ),
      syncFlags: z
        .string()
        .optional()
        .describe(
          "Optional SyncFlags to emit as [Sync( SyncFlags.X )] — e.g. 'Interpolate' (smooth interpolation), 'Query', 'FromHost'. Omit for a plain [Sync]"
        ),
      defaultValue: z
        .string()
        .optional()
        .describe(
          "Currently ignored — not yet implemented. The addon does not create or initialize a property, so no default is written"
        ),
    },
    async (params) => {
      const res = await bridge.send("add_sync_property", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── add_rpc_method ────────────────────────────────────────────────
  server.tool(
    "add_rpc_method",
    "Generate an RPC method stub in a C# script. Inserts the chosen RPC attribute ([Rpc.Broadcast] all clients, [Rpc.Host] host only, [Rpc.Owner] owner only) above a method with an empty body. Pass methodParams to give it a parameter list (e.g. 'Vector3 pos, int damage'); the body is left as a TODO for you to fill in",
    {
      path: z
        .string()
        .describe("Relative path to the script file"),
      methodName: z.string().describe("Name for the RPC method"),
      rpcType: z
        .enum(["Broadcast", "Host", "Owner"])
        .optional()
        .describe("RPC type: Broadcast (all), Host (server), Owner (owning client). Defaults to Broadcast"),
      methodParams: z
        .string()
        .optional()
        .describe(
          "Optional parameter list for the RPC method signature, e.g. 'Vector3 pos, int damage'. Omit for a parameterless method"
        ),
      body: z
        .string()
        .optional()
        .describe(
          "Currently ignored — not yet implemented. The addon emits an empty method body; fill it in yourself afterward"
        ),
    },
    async (params) => {
      const res = await bridge.send("add_rpc_method", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_networked_player ───────────────────────────────────────
  server.tool(
    "create_networked_player",
    "Generate a network-aware player controller with [Sync] properties, owner-only input, and [Rpc.Broadcast] actions. Writes <name>.cs and returns { created, path, className }. The generated Component syncs PlayerName/Health, moves a CharacterController from Input.AnalogMove behind an IsProxy guard, and exposes a [Property] MoveSpeed plus an [Rpc.Broadcast] TakeDamage(int). Follow with trigger_hotload, then get_compile_errors, then attach + network_spawn",
    {
      name: z
        .string()
        .optional()
        .describe("Class name. Defaults to 'NetworkedPlayer'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under code/"),
      moveSpeed: z.number().optional().describe("Movement speed (generated MoveSpeed [Property]). Defaults to 200"),
      includeHealth: z
        .boolean()
        .optional()
        .describe("Currently not applied by the handler — the [Sync] Health and [Rpc.Broadcast] TakeDamage are always generated"),
    },
    async (params) => {
      const res = await bridge.send("create_networked_player", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_lobby_manager ──────────────────────────────────────────
  server.tool(
    "create_lobby_manager",
    "Generate a lobby manager Component implementing Component.INetworkListener: a static Instance singleton, a [Sync] PlayerCount maintained in OnActive/OnDisconnected, and a LobbyState [Property] that flips to 'playing' when the lobby fills. Writes <name>.cs and returns { created, path, className }. Follow with trigger_hotload, then get_compile_errors, then place via add_component_to_new_object",
    {
      name: z
        .string()
        .optional()
        .describe("Class name. Defaults to 'LobbyManager'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under code/"),
      maxPlayers: z
        .number()
        .optional()
        .describe("Currently not applied by the handler — the generated MaxPlayers [Property] defaults to 16; tune it per-instance with set_property"),
    },
    async (params) => {
      const res = await bridge.send("create_lobby_manager", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_network_events ─────────────────────────────────────────
  server.tool(
    "create_network_events",
    "Generate a network event relay Component: [Rpc.Broadcast] SendEvent(eventName, payload) to all clients and [Rpc.Host] SendEventToHost(...), both dispatching into a local OnNetworkEvent switch you extend. It does NOT implement INetworkListener — use create_lobby_manager for connect/disconnect hooks. Writes <name>.cs and returns { created, path, className }. Follow with trigger_hotload, then get_compile_errors",
    {
      name: z
        .string()
        .optional()
        .describe("Class name. Defaults to 'NetworkEvents'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory under code/"),
      includeChat: z
        .boolean()
        .optional()
        .describe("Currently not applied by the handler — no chat system is generated"),
    },
    async (params) => {
      const res = await bridge.send("create_network_events", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
