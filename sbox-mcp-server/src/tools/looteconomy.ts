import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Loot / Economy variants pack — three Tier-2 scaffolds (v1.20.0, Track D):
 *
 *   - create_gacha_drop_table  per-rarity roll + pity counter + duplicate detection,
 *                              host-authoritative ([Rpc.Host] roll → [Rpc.Broadcast])
 *   - create_currency_pickup   networked coin: optional magnet + host-validated grant
 *                              into create_economy_wallet's AddMoney + replicated despawn
 *   - create_offline_progress  DateTime delta on enable + clamp + deterministic tick replay
 *
 * All generate a clean, self-contained sealed Component .cs; file/scene-mutating, refused
 * during play mode by the bridge dispatch. create_currency_pickup + create_offline_progress
 * emit host/owner-authoritative code ([Sync]-free state stays host-side); create_gacha_drop_table
 * routes the roll through [Rpc.Host] and announces via [Rpc.Broadcast].
 */
export function registerLootEconomyTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_gacha_drop_table ───────────────────────────────────────
  server.tool(
    "create_gacha_drop_table",
    "Generate a host-authoritative gacha / loot-box roller component. Two-level pick: parallel [Property] lists RarityNames + RarityWeights select a RARITY by cumulative weight (the create_weighted_loot_table shape), then a flat 'Rarity:Item' [Property] list (e.g. 'Legendary:Dragon Fang') picks an ITEM uniformly within that rarity — simple and inspector-editable. A pity counter (PityAfter, default 50) guarantees the rarest tier (the LAST entry in RarityNames) after N rolls without it and resets on a hit. Duplicate detection against an owned-items set fires a host-side OnDuplicate hook (marked TODO: convert dupes to shards/currency). Roll() routes to the host via an [Rpc.Host] RequestRoll (Rpc.Caller re-validated — NetFlags is not security) and the result fans out via [Rpc.Broadcast] so every machine fires the static OnRolled(rarity, item, isDuplicate) event; single-player safe (RPCs run locally). Use create_weighted_loot_table instead for a simpler single-tier weighted pick with no pity/dupe/networking. Pairs with create_economy_wallet (spend currency to roll) and create_inventory (store the pulls).",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'GachaDropTable'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      pityAfter: z
        .number()
        .int()
        .optional()
        .describe("Rolls without a rarest-tier hit before the next roll is guaranteed rarest. 0 disables pity. Defaults to 50"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a per-player/manager GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_gacha_drop_table", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_currency_pickup ────────────────────────────────────────
  server.tool(
    "create_currency_pickup",
    "Generate a networked coin / currency pickup component (sealed, Component.ITriggerListener). Host-spawned; when a GameObject carrying PlayerTag ('player') enters its trigger the HOST validates and grants Value (default 1) into a wallet on the player, then destroys the pickup network-wide (the host Destroy() replicates — there is no NetworkDestroy on this SDK). Optional magnet: while MagnetRadius (default 0 = off) is > 0 the coin accelerates toward the nearest player each FixedUpdate (host-side, capped by MaxMagnetSpeed). IsProxy guards keep the grant + despawn host-only in multiplayer (NetworkSpawn the coin on the host); single-player works with no networking. The deposit is reflection-free and dependency-free: a static Grant seam is wired ONCE to the direct typed call — player.Components.Get<EconomyWallet>()?.AddMoney(amount) — so the component compiles with NO hard reference to a specific wallet class (rename the wallet type if yours differs; mirrors create_pickup's self-contained convention). WalletComponentName (default 'EconomyWallet') is used to locate the wallet and name the fix if Grant is left unwired (never silent). Pairs with create_economy_wallet (AddMoney/TrySpend/CanAfford) and create_floating_combat_text (spawn a '+N' popup from OnCollected).",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'CurrencyPickup'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      value: z
        .number()
        .int()
        .optional()
        .describe("How much currency the pickup grants into the wallet. Defaults to 1"),
      magnetRadius: z
        .number()
        .optional()
        .describe("Magnet range in world units — within it the coin flies to the nearest player each FixedUpdate. 0 = magnet off. Defaults to 0"),
      walletComponentName: z
        .string()
        .optional()
        .describe("Type name of the wallet component to deposit into (used to locate it and to name the fix if the Grant seam is left unwired). Defaults to 'EconomyWallet'"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a coin GameObject to attach to — give it a trigger Collider (SphereCollider, IsTrigger=true). Only attaches if the type is already loaded — hotload first"),
    },
    async (params) => {
      const res = await bridge.send("create_currency_pickup", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_offline_progress ───────────────────────────────────────
  server.tool(
    "create_offline_progress",
    "Generate an offline / idle-progress component (sealed, owner/host-only) — the idle-game staple. Persists LastSeenUtc (DateTime) to FileSystem.Data JSON on a dirty-flag autosave heartbeat (AutosaveSeconds) and on OnDisabled, copying create_save_system's persistence patterns. On enable it computes elapsed = now − LastSeenUtc, guards a clock rollback (negative → 0), clamps to MaxOfflineHours (default 8), then replays that time through a SimulateOffline(double seconds) TODO hook in fixed TickSeconds chunks (default 1) so idle accumulation is deterministic (frame-rate independent), and fires the static OnOfflineProgressApplied(seconds) event (drive a 'welcome back, you earned X' screen). IsProxy-guarded so a client can't author their own offline earnings. Fill in the SimulateOffline hook with your idle math (e.g. wallet.AddMoney(rate*seconds)). Pairs with create_economy_wallet / create_save_system / create_stat_modifier_system.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'OfflineProgress'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      maxOfflineHours: z
        .number()
        .optional()
        .describe("Offline time is clamped to this many hours (stops a week-away paying out a week). Defaults to 8"),
      tickSeconds: z
        .number()
        .optional()
        .describe("SimulateOffline chunk size in seconds — smaller = finer-grained deterministic replay (floored at 0.1). Defaults to 1"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of an idle/save-manager GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_offline_progress", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
