import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Material and model tools: assign_model, create_material, assign_material, set_material_property.
 * Handles .vmat creation, ModelRenderer management, and runtime material property changes.
 */
export function registerMaterialTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── assign_model ─────────────────────────────────────────────────
  server.tool(
    "assign_model",
    "Set a 3D model on a GameObject's ModelRenderer. Creates the renderer component if it doesn't exist",
    {
      id: z.string().describe("GUID of the GameObject"),
      model: z
        .string()
        .describe(
          "Model path (e.g. 'models/citizen/citizen.vmdl', 'models/dev/box.vmdl')"
        ),
    },
    async (params) => {
      const res = await bridge.send("assign_model", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_material ──────────────────────────────────────────────
  server.tool(
    "create_material",
    "Create a new material file (.vmat) with a shader and properties like color, roughness, metallic, texture",
    {
      path: z
        .string()
        .describe(
          "Relative path for the material (e.g. 'materials/walls/brick.vmat')"
        ),
      shader: z
        .string()
        .optional()
        .describe(
          "Shader to use. Defaults to 'shaders/complex.shader' (PBR)"
        ),
      properties: z
        .record(z.unknown())
        .optional()
        .describe(
          "Material properties as key-value pairs (e.g. { \"Color\": \"#ff0000\", \"Roughness\": 0.8 })"
        ),
    },
    async (params) => {
      const res = await bridge.send("create_material", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── assign_material ──────────────────────────────────────────────
  server.tool(
    "assign_material",
    "Apply a material to a ModelRenderer on a GameObject. Optionally target a specific material slot",
    {
      id: z.string().describe("GUID of the GameObject"),
      material: z
        .string()
        .describe("Material path (e.g. 'materials/walls/brick.vmat')"),
      slot: z
        .number()
        .optional()
        .describe("Material slot index. Defaults to 0 (first slot)"),
    },
    async (params) => {
      const res = await bridge.send("assign_material", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── set_material_property ────────────────────────────────────────
  server.tool(
    "set_material_property",
    "Change a property on the material assigned to a GameObject — color, roughness, metallic, texture, etc.",
    {
      id: z.string().describe("GUID of the GameObject"),
      property: z
        .string()
        .describe(
          "Material property name (e.g. 'Color', 'Roughness', 'Metalness', 'Normal')"
        ),
      value: z
        .unknown()
        .describe(
          "Property value — number for floats, string for texture paths/colors, {r,g,b,a} for colors"
        ),
    },
    async (params) => {
      const res = await bridge.send("set_material_property", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
