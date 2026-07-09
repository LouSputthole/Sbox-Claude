import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Asset browser tools: search_assets, list_asset_library, install_asset, get_asset_info.
 * Provides access to both project-local assets and the s&box community asset library.
 */
export function registerAssetTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── search_assets ────────────────────────────────────────────────
  server.tool(
    "search_assets",
    "Search for assets in the project by name, type, or keyword. Returns models, materials, sounds, textures, prefabs, etc.",
    {
      query: z
        .string()
        .optional()
        .describe("Search term to match against asset name or path"),
      type: z
        .string()
        .optional()
        .describe(
          "Asset type filter (e.g. 'model', 'material', 'sound', 'texture', 'prefab')"
        ),
      maxResults: z
        .number()
        .optional()
        .describe("Maximum results to return. Defaults to 50"),
    },
    async (params) => {
      const res = await bridge.send("search_assets", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── list_asset_library ───────────────────────────────────────────
  server.tool(
    "list_asset_library",
    "List assets visible to the editor's AssetSystem — project assets plus mounted engine/installed-package content (it does NOT query the remote sbox.game library; use install_asset to pull in new packages). Returns { count, assets:[{ name, path, relativePath, assetType }] } — pass a returned path to spawn_model, assign_model, or get_asset_info",
    {
      query: z
        .string()
        .optional()
        .describe("Search term, case-insensitive substring match against asset NAMES"),
      type: z
        .string()
        .optional()
        .describe("Asset type filter, substring-matched against the asset's type (e.g. 'model', 'material', 'sound')"),
      maxResults: z
        .number()
        .optional()
        .describe("Maximum results. Defaults to 200"),
    },
    async (params) => {
      const res = await bridge.send("list_asset_library", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── install_asset ────────────────────────────────────────────────
  server.tool(
    "install_asset",
    "Install a community asset package into the project by its ident (e.g. 'facepunch.flatgrass'). Adds it as a project dependency. Returns { installed, ident, name, path, relativePath, restartRecommended, note } — pass the returned path to spawn_model / assign_model. CAUTION: if the package is a code LIBRARY (adds a PackageReference), trigger_hotload will NOT surface its types — call restart_editor so the new package compiles into the project",
    {
      ident: z
        .string()
        .describe(
          "Package identifier (e.g. 'facepunch.flatgrass', 'author.package_name')"
        ),
    },
    async (params) => {
      const res = await bridge.send("install_asset", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── get_asset_info ───────────────────────────────────────────────
  server.tool(
    "get_asset_info",
    "Get detailed metadata about a specific asset — type, path, tags, package source",
    {
      path: z.string().describe("Asset path (e.g. 'models/citizen/citizen.vmdl')"),
    },
    async (params) => {
      const res = await bridge.send("get_asset_info", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── copy_asset_with_dependencies ─────────────────────────────────
  server.tool(
    "copy_asset_with_dependencies",
    "Copy a project asset and its full dependency closure (via Asset.GetReferences(deep:true)) into a target directory, preserving relative path structure so material references to textures keep resolving. SHADOW GUARD: refuses to write under core engine trees (models/citizen, models/dev, materials/dev, materials/default) -- copying there triggers an infinite asset-recompile loop (BRIDGE_GOTCHAS #5). Cloud/procedural/transient assets are skipped with a reason. Returns { copied:[{from,to}], skipped:[{path,reason}], count, note }.",
    {
      sourcePath: z
        .string()
        .describe("Absolute path OR project-relative path of the source asset (e.g. 'models/props/crate.vmdl'). Use search_assets to find the exact path."),
      targetDir: z
        .string()
        .optional()
        .describe("Project-relative destination directory (e.g. 'Assets/library'). Defaults to 'Assets/library'"),
      overwrite: z
        .boolean()
        .optional()
        .describe("Overwrite existing files at the destination. Defaults to false"),
    },
    async (params) => {
      const res = await bridge.send("copy_asset_with_dependencies", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
