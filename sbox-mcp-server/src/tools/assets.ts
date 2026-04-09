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
    "Browse the s&box community asset library. Search for packages by name, description, or type to find models, maps, and tools to install",
    {
      query: z
        .string()
        .optional()
        .describe("Search term for packages"),
      type: z
        .string()
        .optional()
        .describe("Package type filter (e.g. 'model', 'map', 'library')"),
      maxResults: z
        .number()
        .optional()
        .describe("Maximum results. Defaults to 25"),
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
    "Install a community asset package into the project by its ident (e.g. 'facepunch.flatgrass'). Adds it as a project dependency",
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
}
