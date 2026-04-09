import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Project and file management tools: get_project_info, list_project_files,
 * read_file, write_file.
 *
 * This is the simplest tool file and serves as a good template for contributors.
 * Every tool follows the same pattern: define with server.tool(), send the
 * command to the Bridge, and return the result (or error) as text content.
 */
export function registerProjectTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── get_project_info ─────────────────────────────────────────────
  server.tool(
    "get_project_info",
    "Get information about the current s&box project — path, name, game type, dependencies, and configuration",
    {},
    async () => {
      const res = await bridge.send("get_project_info");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [
          { type: "text", text: JSON.stringify(res.data, null, 2) },
        ],
      };
    }
  );

  // ── list_project_files ───────────────────────────────────────────
  server.tool(
    "list_project_files",
    "Browse the project file tree. Optionally filter by directory path and/or file extension (e.g. '.cs', '.scene')",
    {
      path: z
        .string()
        .optional()
        .describe(
          "Relative directory path to list (e.g. 'code/Components'). Defaults to project root"
        ),
      extension: z
        .string()
        .optional()
        .describe(
          "Filter by file extension, including the dot (e.g. '.cs', '.scene')"
        ),
      recursive: z
        .boolean()
        .optional()
        .describe("Whether to list files recursively. Defaults to true"),
    },
    async (params) => {
      const res = await bridge.send("list_project_files", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [
          { type: "text", text: JSON.stringify(res.data, null, 2) },
        ],
      };
    }
  );

  // ── read_file ────────────────────────────────────────────────────
  server.tool(
    "read_file",
    "Read the contents of a file in the s&box project (scripts, scenes, configs, etc.)",
    {
      path: z
        .string()
        .describe(
          "Relative path to the file within the project (e.g. 'code/PlayerController.cs')"
        ),
    },
    async (params) => {
      const res = await bridge.send("read_file", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [
          {
            type: "text",
            text:
              typeof res.data === "string"
                ? res.data
                : JSON.stringify(res.data, null, 2),
          },
        ],
      };
    }
  );

  // ── write_file ───────────────────────────────────────────────────
  server.tool(
    "write_file",
    "Write or overwrite a file in the s&box project. Creates parent directories as needed",
    {
      path: z
        .string()
        .describe(
          "Relative path for the file (e.g. 'code/Components/Health.cs')"
        ),
      content: z.string().describe("The full file content to write"),
    },
    async (params) => {
      const res = await bridge.send("write_file", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [
          {
            type: "text",
            text: `File written successfully: ${params.path}`,
          },
        ],
      };
    }
  );
}
