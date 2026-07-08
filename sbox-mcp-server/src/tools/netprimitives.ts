import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Networking primitives pack — four multiplayer scaffolds (v1.20.0, Track B):
 *
 *   - create_host_rpc_action       validated + rate-limited [Rpc.Host] action skeleton
 *   - add_targeted_rpc             Rpc.FilterInclude single-client (unicast) side-effect
 *   - create_local_player_resolver proxy-safe "who is MY player" resolver (online + offline)
 *   - add_host_migration_recovery  proxy→authority transition detector + OnBecameHost hook
 *
 * Each generates ONE self-contained sealed Component .cs into the project via
 * ScaffoldHelpers. These are the correctness primitives every networked s&box
 * game hand-rolls (and usually gets wrong): host-authoritative validated actions,
 * unicast RPCs, proxy-safe local-player resolution, and host-migration recovery.
 * All are file/scene-mutating and refused during play mode by the bridge dispatch.
 */
export function registerNetPrimitivesTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_host_rpc_action ────────────────────────────────────────
  server.tool(
    "create_host_rpc_action",
    "Generate a validated, rate-limited host-action component — the SAFE skeleton for 'a client asks the host to DO something' (buy, use, vote, interact, spend). The generated sealed Component exposes a client-callable Request() that forwards to an [Rpc.Host] SubmitRequest() which runs ON THE HOST: it re-resolves WHO called it via Rpc.Caller (never trusting client args for identity), enforces a per-SteamId cooldown backed by a Dictionary<ulong, TimeSince>, runs a clearly-marked TODO block for your authoritative action, and fires the static OnActionExecuted(Connection) event. This is the correct answer to the #1 multiplayer exploit class: [Rpc.Host] is callable by ANY client with forged args — NetFlags restrict who may INVOKE, which is not security — so identity + rate-limit + validation all live inside the host body. Single-player safe (no session → the RPC runs locally, caller falls back to Connection.Local). Attach it to the object that owns the action (a player, a station, or your game manager). Next steps: hotload, tag/attach, call Request() from input or a UI button, fill the TODO host block, and subscribe to OnActionExecuted. Covers the backlog's add_rate_limited_rpc. Optionally attached to an existing GameObject by GUID (after a hotload).",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'HostRpcAction'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      cooldownSeconds: z
        .number()
        .optional()
        .describe("Minimum seconds between accepted requests, per calling player (the per-SteamId rate limit). Defaults to 1"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_host_rpc_action", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_targeted_rpc ──────────────────────────────────────────────
  server.tool(
    "add_targeted_rpc",
    "Generate a component demonstrating the unicast (single-client) RPC pattern via Rpc.FilterInclude. A normal [Rpc.Broadcast] runs on EVERY machine; the generated component's host-side SendTo(Connection target, string message) wraps its [Rpc.Broadcast] call in `using ( Rpc.FilterInclude( target ) )` so ONLY the target connection executes the body, which raises the static OnReceived(string) event. This is the RIGHT way to send something to one player — a private prompt, a personal reward toast, a per-player cutscene cue — instead of broadcasting to everyone and filtering on the client (which leaks data and wastes bandwidth). Call SendTo on the host (guarded behind Networking.IsActive so Networking.IsHost can't throw with no session; runs locally in solo). Attach to a networked manager object that is NetworkSpawn'd. Next steps: hotload, attach, call SendTo(player.Network.Owner, \"...\") from the host, and subscribe to OnReceived on the target client. Optionally attached to an existing GameObject by GUID (after a hotload).",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'TargetedRpc'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("add_targeted_rpc", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_local_player_resolver ──────────────────────────────────
  server.tool(
    "create_local_player_resolver",
    "Generate a proxy-safe 'who is MY player?' resolver — the corpus footgun-killer that stops you running local-player logic against a proxy of someone else's player. The generated sealed Component exposes a static Local property that lazily finds the player GameObject belonging to THIS machine: online it's the tagged object whose Network.Owner is the local connection (Network.Owner == Connection.Local, or Network.IsOwner); offline/solo (no session) it's the first/only tagged player. The result is cached and revalidated with IsValid() so a destroyed or respawned player is re-resolved automatically. Also exposes a static IsLocal(GameObject) helper for filtering events (e.g. only open a station overlay for your own player). Works identically online and offline — no `if (Network.IsOwner)` guard that silently disables everything in a solo playtest. Attach ONE to a persistent object (your game manager) so PlayerTag is configurable in the inspector; the resolver is static and callable from anywhere. Next steps: hotload, attach, tag each player GameObject with the PlayerTag, then read <Name>.Local / filter with <Name>.IsLocal(go). Optionally attached to an existing GameObject by GUID (after a hotload).",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'LocalPlayerResolver'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      playerTag: z
        .string()
        .optional()
        .describe("Tag that marks a player GameObject (players must carry this tag). Baked as the default PlayerTag, editable per-instance in the inspector. Defaults to 'player'"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_local_player_resolver", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_host_migration_recovery ───────────────────────────────────
  server.tool(
    "add_host_migration_recovery",
    "Generate a host-migration recovery component — it detects when THIS machine takes authority over its GameObject (the proxy→authority transition that promotes a client to host when the old host leaves) and gives you a clean hook to rebuild host-only state. It tracks IsProxy each frame; when it flips from true (someone else was the authority) to false (now it's us), it fires the static OnBecameHost(GameObject) event and runs a virtual-style TODO rebuild region, then — after a tunable SettleSeconds delay so in-flight packets can land — runs a deferred validation region. s&box has a known bug where networked objects are often destroyed during host migration and the new host inherits stale transient state, so the recommended shape is: detect becoming host, aggressively rebuild (re-arm host-only loops/TimeUntil timers against your clock, TakeOwnership of orphans, rebuild handle maps by world position, reconcile the [Sync] registry against the real scene), and defer the sanity check ~1s. Inert offline (IsProxy is always false with no session). Attach to your host-authoritative manager object (NetworkSpawn'd). Next steps: hotload, attach, subscribe to OnBecameHost, and fill the two TODO regions. Requires a real host migration (a second client that becomes host) to fire — it can't be exercised in a solo playtest. Optionally attached to an existing GameObject by GUID (after a hotload).",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'HostMigrationRecovery'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("add_host_migration_recovery", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
