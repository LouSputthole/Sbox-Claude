import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Economy & Save family — six Tier-2 scaffolds (Track E):
 *
 *   - create_currency_account    audited host-authoritative ledger: [Sync(FromHost)]
 *                                balance + Deposit/Withdraw/TryTransfer + a fixed-size
 *                                transaction ring buffer with GetRecentTransactions()
 *   - create_idle_economy        geometric bulk-buy purchasing (BaseCost * Growth^Owned,
 *                                closed-form Buy 1 / N / Max) + income auto-wired to a
 *                                sibling wallet via TypeLibrary reflection
 *   - create_signed_save         tamper-evident save: FNV-1a signature over payload+salt,
 *                                verify-on-load, Sanitize() clamp hook, forced reset on
 *                                mismatch, versioned
 *   - create_meta_progression    between-runs roguelite meta: persistent meta-currency +
 *                                unlock flags, BankRun(int) run-end seam, OnUnlocked event
 *   - add_steam_stat_currency    currency persisted over Sandbox.Services.Stats
 *                                (Steam-cloud, per account, per package ident)
 *   - create_loot_table_resource GameResource-based loot table assets ([AssetType],
 *                                .loot files, nested tables, depth cap) + a resolver component
 *
 * All generate clean, self-contained sealed game code (.cs) into the project; file/scene
 * mutating, refused during play mode by the bridge dispatch. Host-authoritative state uses
 * [Sync(SyncFlags.FromHost)] + IsProxy guards throughout.
 */
export function registerEconomySaveTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_currency_account ───────────────────────────────────────
  server.tool(
    "create_currency_account",
    "Generate a host-authoritative currency ACCOUNT component (sealed) — the audited sibling of create_economy_wallet (wallet = simple money, account = money + a ledger). Balance is [Sync(SyncFlags.FromHost)] so clients can't author their own money; host-guarded Deposit(amount, reason), Withdraw(amount, reason) -> bool, and TryTransfer(otherAccount, amount, reason) -> bool each record a Transaction { Time (Time.Now), signed Amount, Reason, BalanceAfter } into a fixed-size ring buffer (historySize, default 32; oldest entries overwritten SILENTLY). GetRecentTransactions(max) returns them NEWEST FIRST — the ledger is HOST-SIDE ONLY and does not replicate (Balance does); proxies get an empty list. Bind the instance OnBalanceChanged(long) for HUD labels. Single-player safe. Returns { created, path, className, startingBalance, historySize, placedOn, note, nextSteps }. Next: trigger_hotload, then attach via targetId re-run or add_component_to_new_object. Refuses if the file already exists; refused during play mode. Use create_economy_wallet when you don't need the audit trail; pair with create_idle_economy (it auto-wires this account's Money/TrySpend).",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'CurrencyAccount'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      startingBalance: z
        .number()
        .int()
        .optional()
        .describe("Balance the account opens with (host seeds it in OnStart). Defaults to 0"),
      historySize: z
        .number()
        .int()
        .optional()
        .describe("Transaction ring-buffer capacity (clamped 1..4096); fixed once the first transaction is recorded, oldest overwritten silently after that. Defaults to 32"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a per-player/bank GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_currency_account", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_idle_economy ───────────────────────────────────────────
  server.tool(
    "create_idle_economy",
    "Generate a geometric idle-economy component (sealed): generators on the classic BaseCost * Growth^Owned cost curve with Buy 1 / Buy N / Buy Max — CostOf(index, count), MaxAffordable(index), TryBuy(index, count) and BuyMax(index) all use the CLOSED-FORM geometric series (cost = c0*(g^n-1)/(g-1), buyMax = floor(log_g(funds*(g-1)/c0+1))) — no per-copy loops, Buy 1000 is the same math as Buy 1. Wallet wiring is TypeLibrary reflection with NO compile-time wallet dependency (the shipped create_idle_income pattern): each income tick invokes AddMoney(long|int) on the first sibling component that has one, purchases invoke TrySpend(long|int), Buy Max reads the sibling's Money (or Balance) property — works out of the box next to create_economy_wallet or create_currency_account; with NO wallet sibling, purchases are refused with a Log.Warning (never silent) while TotalEarned still accumulates. Host-authoritative: mutations IsProxy-guarded; owned counts are HOST-SIDE state (not replicated); TotalEarned is [Sync(FromHost)]. Static events OnPurchased(index, count, cost) and OnIncomeTick(amount, total). BuyMax steps down once past a whole-currency rounding edge rather than failing. Returns { created, path, className, generators, tickSeconds, placedOn, note, nextSteps }. Next: trigger_hotload, place it NEXT TO a wallet on the same GameObject, tune the parallel GeneratorNames/BaseCosts/Growths/IncomesPerSecond lists with set_property. Refused during play mode. Pair with create_offline_progress for away-time earnings; use create_idle_income for a bare income ticker with no purchasing.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'IdleEconomy'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      tickSeconds: z
        .number()
        .optional()
        .describe("Seconds between income grants (floored at 0.1). Defaults to 1"),
      generators: z
        .array(
          z.object({
            name: z.string().optional().describe("Generator display name"),
            baseCost: z.number().optional().describe("Cost of the first copy. Defaults to 15"),
            growth: z.number().optional().describe("Per-copy cost multiplier, min 1 (1.15 = classic curve). Defaults to 1.15"),
            incomePerSecond: z.number().optional().describe("Income each owned copy produces per second. Defaults to 0.5"),
          })
        )
        .optional()
        .describe("Baked-in generator defaults (inspector-tunable after generation). Omit for a starter trio: Cursor 15/1.15/0.5, Farm 200/1.15/4, Factory 3000/1.12/30"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of the GameObject to attach to — put it on the SAME GameObject as the wallet so the reflection wiring finds it (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_idle_economy", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_signed_save ────────────────────────────────────────────
  server.tool(
    "create_signed_save",
    "Generate a tamper-evident, versioned save-system component (sealed, owner-only). The SaveData payload POCO is serialized to JSON (Sandbox.Json), FNV-1a-64 hashed over payload + version + salt, and written as a signed envelope { Version, Payload, Signature } to FileSystem.Data. Load() re-verifies: a signature mismatch (hand-edited/corrupt file) triggers a FORCED RESET — the save file is DELETED, defaults are used, and the static OnTampered(reason) event fires (destructive and deliberate; tell the player). A version mismatch starts fresh without the tamper event (add migrations in Load). Loaded values pass a Sanitize() clamp hook so even a re-signed save can't smuggle absurd values. Dirty-flag autosave (autosaveSeconds, default 10; MarkDirty() to arm) + a final save in OnDestroy. HONEST LIMIT: the salt ships inside the game assembly, so this is tamper-EVIDENT (stops notepad edits), NOT cryptographically secure. If you omit salt, a unique random one is baked into the generated file — changing it later invalidates existing saves. Returns { created, path, className, fileName, version, autosaveSeconds, placedOn, note, nextSteps }. Next: trigger_hotload, attach, add your fields to SaveData + clamps to Sanitize(), bump version on shape changes. Refused during play mode. Use create_save_system for a plain unsigned save, create_save_slots for multi-slot UI flows, create_meta_progression for roguelite meta-state.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'SignedSave'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      fileName: z
        .string()
        .optional()
        .describe("FileSystem.Data path the signed envelope is written to. Defaults to 'save_signed.json'"),
      version: z
        .number()
        .int()
        .optional()
        .describe("Save-shape version baked into the file and the signature; mismatched files start fresh. Defaults to 1"),
      salt: z
        .string()
        .optional()
        .describe("Signing salt baked into the generated code. Omit to bake a unique random salt (recommended); changing it later invalidates existing saves"),
      autosaveSeconds: z
        .number()
        .optional()
        .describe("Dirty-flag autosave cadence in seconds; 0 disables the heartbeat (OnDestroy still saves). Defaults to 10"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a save-manager GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_signed_save", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_meta_progression ───────────────────────────────────────
  server.tool(
    "create_meta_progression",
    "Generate a between-runs roguelite meta-progression component (sealed, owner-only): persistent meta-currency + an unlock-flag dictionary saved to FileSystem.Data JSON (dirty-flag autosave + OnDestroy, the create_save_system shape). API: Grant(long), TrySpend(long) -> bool, Unlock(key) (idempotent — the static OnUnlocked(key) event fires only on the FIRST unlock, and unlocks write through to disk immediately), IsUnlocked(key) -> bool, and the run-end seam BankRun(int earned) which converts a finished run's earnings into meta-currency, bumps RunsBanked, and saves immediately — call it from your round machine's end-of-run transition (create_round_state_machine / create_round_phase_machine). Instance OnCurrencyChanged(long) drives meta-shop balance labels. Versioned payload: old-version files start fresh. IsProxy-guarded — in multiplayer each machine banks only its own local meta file (this is per-machine persistence, not a server economy). Returns { created, path, className, fileName, version, placedOn, note, nextSteps }. Next: trigger_hotload, attach to a persistent hub/menu-scene manager GameObject, gate content with IsUnlocked when building the player. Refused during play mode. Pair with create_currency_account (in-run money) and create_signed_save (if the meta file needs tamper evidence — this one is unsigned).",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'MetaProgression'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      fileName: z
        .string()
        .optional()
        .describe("FileSystem.Data path the meta state is written to. Defaults to 'meta.json'"),
      version: z
        .number()
        .int()
        .optional()
        .describe("Payload version; mismatched files start fresh. Defaults to 1"),
      autosaveSeconds: z
        .number()
        .optional()
        .describe("Dirty-flag autosave cadence in seconds; 0 disables the heartbeat (unlocks and BankRun still write through immediately). Defaults to 10"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a persistent manager GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_meta_progression", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── add_steam_stat_currency ───────────────────────────────────────
  server.tool(
    "add_steam_stat_currency",
    "Generate a currency component (sealed) persisted over Sandbox.Services.Stats — Steam-cloud persistence, per Steam account, per package ident, with NO local save file. The stat stores the ABSOLUTE balance: every Add(double)/TrySpend(double) pushes Stats.SetValue(statName, balance); Flush() (and OnDestroy) pushes the buffered writes. On start it reads the balance back asynchronously via Stats.GetLocalPlayerStats(ident) -> Refresh() -> Get(statName).Value and fires the static OnBalanceLoaded(double); wait for IsLoaded before showing the balance. CLOUD SEMANTICS (surprising): stat writes are buffered/rate-limited by the backend and apply ONLY to the LOCAL Steam user — calling this for another player silently does nothing, so attach it to the LOCAL player's GameObject (IsProxy guards keep remote copies inert); read-back is eventually consistent and can lag minutes behind writes — the in-session Balance property is the runtime truth. Dev sessions without a real published package ident may read back nothing (balance starts 0 with a log line). packageIdent defaults to the running package (Game.Ident). Returns { created, path, className, statName, packageIdent, flushEveryChange, placedOn, note, nextSteps }. Next: trigger_hotload, attach to the local player, bind OnBalanceChanged for the HUD. Refused during play mode. Use create_economy_wallet/create_currency_account for in-run networked money, create_signed_save for offline local persistence; pair with create_leaderboard_panel (the same stat can back a leaderboard).",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated component. Defaults to 'SteamStatCurrency'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      statName: z
        .string()
        .optional()
        .describe("Sandbox.Services stat that stores the balance (the stat-name string is the contract between write and read-back). Defaults to 'currency'"),
      packageIdent: z
        .string()
        .optional()
        .describe("Package ident to read stats from. Omit/empty = the running package (Game.Ident)"),
      flushEveryChange: z
        .boolean()
        .optional()
        .describe("Call Stats.Flush() after every balance change instead of relying on the buffered flush + OnDestroy flush (the backend rate-limits flushes). Defaults to false"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of the LOCAL player's GameObject to attach to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("add_steam_stat_currency", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── create_loot_table_resource ────────────────────────────────────
  server.tool(
    "create_loot_table_resource",
    "Generate GameResource-based loot tables — the data-asset sibling of create_weighted_loot_table. One .cs file containing THREE types: an entry POCO { Name, Weight, optional NestedTable reference }, a [AssetType]-registered GameResource loot-table class (designers author '.loot' files in the editor asset browser — New > Loot Table — after the hotload; NOTE: [AssetType(Name=..., Extension=..., Category=...)] is used because GameResourceAttribute is [Obsolete] on this SDK), and a '<name>Resolver' Component that rolls an assigned table by cumulative weight. Nested tables: an entry with a NestedTable rolls INTO that table instead of dropping its Name, capped at maxDepth (default 4) with a self-reference guard so cycles terminate (at the cap the deepest entry's Name is returned). Resolver.Roll() returns the item name (null + warning when no Table is assigned or the table is empty; entries with weight <= 0 never win; all-zero weights fall back to the first entry) and fires the static OnLoot(GameObject, item) event; roll HOST-SIDE and replicate the result yourself. targetId attaches the RESOLVER (the resource is an asset type, not a component). SURPRISING: pick an extension that is NOT a suffix of a built-in one (e.g. avoid 'cfg') or ResourceLibrary picks up engine files as phantom instances. Returns { created, path, className, resolverClass, extension, maxDepth, placedOn, note, nextSteps }. Next: trigger_hotload -> author .loot assets in the editor -> assign the resolver's Table (set_property with the asset path). Refused during play mode. Use create_weighted_loot_table for a single inline component with no asset files; create_gacha_drop_table for pity + duplicate mechanics.",
    {
      name: z
        .string()
        .optional()
        .describe("Class name for the generated GameResource (the resolver becomes '<name>Resolver'). Defaults to 'LootTableResource'"),
      directory: z
        .string()
        .optional()
        .describe("Subdirectory for the generated .cs file. Defaults to 'Code'"),
      extension: z
        .string()
        .optional()
        .describe("Asset file extension (lowercase alphanumerics; avoid suffixes of built-in extensions like 'cfg'). Defaults to 'loot'"),
      title: z
        .string()
        .optional()
        .describe("Display name of the asset type in the editor's New-asset menu. Defaults to 'Loot Table'"),
      maxDepth: z
        .number()
        .int()
        .optional()
        .describe("Default nested-table resolve depth cap baked into the resolver (clamped 0..16; also a [Property]). Defaults to 4"),
      targetId: z
        .string()
        .optional()
        .describe("GUID of a GameObject to attach the RESOLVER component to (only attaches if the type is already loaded — hotload first)"),
    },
    async (params) => {
      const res = await bridge.send("create_loot_table_resource", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
