import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Type discovery and code-search tools.
 *
 * These help Claude reference real s&box APIs instead of guessing. Use
 * describe_type before writing code that touches an unfamiliar type — the
 * Game.TypeLibrary reflection returns properties, methods, events, and
 * attributes for any loaded type. Use search_types to find types matching
 * a name pattern. find_in_project greps the user's project for usage examples.
 */
export function registerDiscoveryTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── describe_type ────────────────────────────────────────────────
  server.tool(
    "describe_type",
    "Inspect a type's full surface — properties, methods, events, attributes — via reflection on Game.TypeLibrary and loaded assemblies. Use this before writing code touching an unfamiliar component or s&box API. Examples: 'MeshComponent', 'PlayerController', 'NetworkHelper', 'Vector3'.",
    {
      name: z.string().describe("Type name (short or fully-qualified)"),
    },
    async (params) => {
      const res = await bridge.send("describe_type", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── list_libraries ───────────────────────────────────────────────
  server.tool(
    "list_libraries",
    "List the s&box libraries/addons installed in this project (reads Libraries/ + each .sbproj). Discovers what's available to build ON — e.g. character controllers (fish.scc = Shrimple Character Controller, facepunch.playercontroller), world/spline/road tools — so you can leverage an installed library (add its components via add_component_with_properties, or generate code against its API) instead of writing from scratch. Returns `count` and `libraries` [{folder, ident, org, title, type, enabled}] — ALL libraries, no limit or pagination; `enabled` is false when the library's .sbproj has been disabled (renamed .sbproj.disabled). Read-only.",
    {},
    async () => {
      const res = await bridge.send("list_libraries", {});
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── search_types ─────────────────────────────────────────────────
  server.tool(
    "search_types",
    "Find loaded types matching a name pattern. Useful for discovering 'is there a built-in X for this?'. Returns `count` and `matches` with each type's name, fullName, isComponent, and isAbstract — results are silently truncated at `limit` (default 50), so narrow the pattern if you hit the cap. Pass a match's name to describe_type for its full member surface.",
    {
      pattern: z.string().describe("Substring to match against type name (case-insensitive)"),
      namespace: z
        .string()
        .optional()
        .describe("Optional namespace filter (case-insensitive substring)"),
      components_only: z.boolean().default(false).describe("Only return Component subclasses (default false)"),
      limit: z.number().int().default(50).describe("Maximum matches to return (default 50); the search stops silently at this cap"),
    },
    async (params) => {
      const res = await bridge.send("search_types", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── get_method_signature ─────────────────────────────────────────
  server.tool(
    "get_method_signature",
    "Get the formal signature(s) of a method on a type — parameter names, types, defaults, return type, all overloads. Use before invoking an API you're unsure of.",
    {
      type: z.string().describe("Type name (e.g. 'Scene', 'GameObject')"),
      method: z.string().describe("Method name (case-sensitive)"),
    },
    async (params) => {
      const res = await bridge.send("get_method_signature", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── find_in_project ──────────────────────────────────────────────
  server.tool(
    "find_in_project",
    "Grep the user's s&box project for a symbol (case-sensitive substring; skips .git/bin/obj). Useful for finding usage examples of an API or seeing how the project already does something. Returns `symbol`, `count`, and `results` [{file, line, text}], capped at `max_results` (default 25) — raise it if you may be missing hits. Follow up with read_file on a result's file path.",
    {
      symbol: z.string().describe("Substring or symbol to search for"),
      extension: z.string().default(".cs").describe("File extension filter"),
      max_results: z.number().int().default(25).describe("Maximum hits to return (default 25); the search stops once reached"),
    },
    async (params) => {
      const res = await bridge.send("find_in_project", params);
      if (!res.success) return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
