import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Publishing tools: get_project_config, set_project_config, validate_project,
 * set_project_thumbnail, get_package_details.
 *
 * Manages project configuration and publishing metadata.
 *
 * build_project / get_build_status / clean_build / export_project / prepare_publish
 * were removed in v1.3.0 — s&box does not expose a public API for these from
 * inside an addon, so the bridge never had handlers for them and the tools only
 * ever returned "Unknown command". See GitHub issue #3.
 */
export function registerPublishingTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── get_project_config ───────────────────────────────────────────
  server.tool(
    "get_project_config",
    "Read the full project configuration from the .sbproj file including title, description, version, type, package references, metadata, and raw JSON",
    {},
    async (params) => {
      const res = await bridge.send("get_project_config", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_project_config ───────────────────────────────────────────
  server.tool(
    "set_project_config",
    "Update project configuration fields for publishing: title, description, version, type, package ident, summary, visibility. Only provided fields are changed — edits string values in the .sbproj file in place. Returns { updated, path } (the .sbproj path); read the result back with get_project_config to confirm what actually changed.",
    {
      title: z.string().optional().describe("Project display title"),
      description: z
        .string()
        .optional()
        .describe("Project description for publishing"),
      version: z
        .string()
        .optional()
        .describe("Version string (e.g. '1.0.0', '2.1.3')"),
      type: z
        .string()
        .optional()
        .describe(
          "Project type: 'game', 'addon', 'library', or 'template'"
        ),
      packageIdent: z
        .string()
        .optional()
        .describe("Package identifier (e.g. 'myorg.mygame')"),
      summary: z
        .string()
        .optional()
        .describe("Short summary for asset.party listing"),
      isPublic: z
        .boolean()
        .optional()
        .describe("Whether the project is publicly visible"),
    },
    async (params) => {
      const res = await bridge.send("set_project_config", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── validate_project ─────────────────────────────────────────────
  server.tool(
    "validate_project",
    "Validate that the project is ready for publishing. Runs four checks: .sbproj exists, at least one scene, project Ident set, project Title set. Returns { valid, issueCount, issues, checks } — issues are human-readable problems and each checks entry has { check, pass, detail }; fix metadata gaps with set_project_config (it does NOT check compile errors — use get_compile_errors for that).",
    {},
    async (params) => {
      const res = await bridge.send("validate_project", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // build_project / get_build_status / clean_build / export_project removed
  // in v1.3.0 — no addon handler exists. See GitHub issue #3.

  // ── set_project_thumbnail ────────────────────────────────────────
  server.tool(
    "set_project_thumbnail",
    "Set or update the project thumbnail image (thumb.png) used for publishing. Provide either a source path or base64 image data",
    {
      sourcePath: z
        .string()
        .optional()
        .describe(
          "Relative path to an image file within the project to use as thumbnail"
        ),
      base64: z
        .string()
        .optional()
        .describe("Base64-encoded image data to write as thumbnail"),
      format: z
        .enum(["png", "jpg"])
        .optional()
        .describe("Image format when using base64 mode. Defaults to 'png'"),
    },
    async (params) => {
      const res = await bridge.send("set_project_thumbnail", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── get_package_details ──────────────────────────────────────────
  server.tool(
    "get_package_details",
    "Fetch package information from the s&box package backend (Package.FetchAsync) by ident. Returns { fullIdent, title, summary, description, org } — no download/rating/dependency data is included. Use it to confirm a package exists and what it is before install_asset.",
    {
      ident: z
        .string()
        .describe(
          "Package identifier (e.g. 'facepunch.flatgrass', 'myorg.mygame')"
        ),
    },
    async (params) => {
      const res = await bridge.send("get_package_details", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // prepare_publish removed in v1.3.0 — no addon handler. Equivalent intent is
  // covered by validate_project (which IS implemented). GitHub issue #3.
}
