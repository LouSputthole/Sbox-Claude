import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Script management tools: create_script, edit_script, delete_script, trigger_hotload.
 * create_script generates boilerplate from parameters or writes raw C# content.
 * edit_script supports find/replace, line insert, append, and line deletion operations.
 */
export function registerScriptTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_script ────────────────────────────────────────────────
  server.tool(
    "create_script",
    "Create a new C# component script in the project — a minimal s&box Component class (name is sanitized to a valid identifier), or your exact code when content is provided. Errors if the file already exists. Returns { path, created, className } — the new type is NOT live until a recompile, so call trigger_hotload, then attach it with add_component_with_properties (component=className).",
    {
      name: z
        .string()
        .describe(
          "Class name for the component (e.g. 'PlayerController'). Will also be the filename"
        ),
      directory: z
        .string()
        .optional()
        .describe(
          "Subdirectory under code/ to place the script (e.g. 'Components'). Defaults to 'code/'"
        ),
      description: z
        .string()
        .optional()
        .describe(
          "Description of what this component does — used to generate appropriate code"
        ),
      properties: z
        .array(
          z.object({
            name: z.string().describe("Property name"),
            type: z.string().describe("C# type (e.g. 'float', 'Vector3', 'GameObject')"),
            default: z
              .string()
              .optional()
              .describe("Default value as a string"),
            description: z
              .string()
              .optional()
              .describe("Property description for the editor tooltip"),
          })
        )
        .optional()
        .describe("List of [Property] fields to include in the component"),
      content: z
        .string()
        .optional()
        .describe(
          "Full C# file content. If provided, ignores name/properties and writes this directly"
        ),
    },
    async (params) => {
      const res = await bridge.send("create_script", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(res.data, null, 2),
          },
        ],
      };
    }
  );

  // ── edit_script ──────────────────────────────────────────────────
  server.tool(
    "edit_script",
    "Edit an existing C# script in place via exact-text find/replace or a full-content overwrite. Errors if the file or the find text isn't found (find/replace replaces ALL occurrences). Returns { path, edited, operation } where operation is 'find_replace' or 'overwrite' — follow with trigger_hotload so the change compiles, then get_compile_errors if in doubt.",
    {
      path: z
        .string()
        .describe("Relative path to the script file (e.g. 'code/PlayerController.cs')"),
      operations: z
        .array(
          z.object({
            type: z
              .enum(["replace", "insert", "append", "delete_lines"])
              .describe("Type of edit operation"),
            find: z
              .string()
              .optional()
              .describe("Text to find (for 'replace' operation)"),
            replacement: z
              .string()
              .optional()
              .describe("Replacement text (for 'replace' and 'insert' operations)"),
            line: z
              .number()
              .optional()
              .describe("Line number (for 'insert' and 'delete_lines' operations)"),
            endLine: z
              .number()
              .optional()
              .describe("End line number (for 'delete_lines' operation)"),
            content: z
              .string()
              .optional()
              .describe("Content to append (for 'append' operation)"),
          })
        )
        .describe("List of edit operations to apply in order"),
    },
    async (params) => {
      const res = await bridge.send("edit_script", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(res.data, null, 2),
          },
        ],
      };
    }
  );

  // ── delete_script ────────────────────────────────────────────────
  server.tool(
    "delete_script",
    "Permanently delete a file from the project by its project-relative path (built for C# scripts, but removes any file; no recycle bin, and editor undo cannot restore it). Errors if the file doesn't exist. Returns a confirmation with the path — follow with trigger_hotload so the removed class actually leaves the compiled assembly.",
    {
      path: z
        .string()
        .describe("Relative path to the script file to delete"),
    },
    async (params) => {
      const res = await bridge.send("delete_script", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [
          { type: "text", text: `Script deleted: ${params.path}` },
        ],
      };
    }
  );

  // ── trigger_hotload ──────────────────────────────────────────────
  server.tool(
    "trigger_hotload",
    "Force s&box to recompile and hotload all C# scripts immediately. Use after creating or editing scripts to see changes in real-time",
    {},
    async () => {
      const res = await bridge.send("trigger_hotload");
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [
          {
            type: "text",
            text: "Hotload triggered — s&box is recompiling scripts",
          },
        ],
      };
    }
  );
}
