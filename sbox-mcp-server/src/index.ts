#!/usr/bin/env node

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

const BRIDGE_URL = process.env.SBOX_BRIDGE_URL || "http://localhost:29015";
const BRIDGE_TIMEOUT = 5000;

interface BridgeResponse {
  success: boolean;
  data?: unknown;
  error?: string;
}

async function bridgeRequest(
  endpoint: string,
  method: string = "GET",
  body?: unknown
): Promise<BridgeResponse> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), BRIDGE_TIMEOUT);

  try {
    const options: RequestInit = {
      method,
      signal: controller.signal,
      headers: { "Content-Type": "application/json" },
    };
    if (body) {
      options.body = JSON.stringify(body);
    }

    const response = await fetch(`${BRIDGE_URL}${endpoint}`, options);
    const data = await response.json();
    return { success: true, data };
  } catch (error) {
    const message =
      error instanceof Error ? error.message : "Unknown error connecting to bridge";
    if (message.includes("abort")) {
      return { success: false, error: "Bridge connection timed out. Is s&box running with the Bridge Addon loaded?" };
    }
    return { success: false, error: `Bridge connection failed: ${message}` };
  } finally {
    clearTimeout(timeout);
  }
}

const server = new McpServer({
  name: "sbox",
  version: "1.0.0",
});

// --- Tools ---

server.tool(
  "get_bridge_status",
  "Check if the s&box bridge is connected and get status info",
  {},
  async () => {
    const result = await bridgeRequest("/status");
    if (!result.success) {
      return {
        content: [
          {
            type: "text" as const,
            text: `Bridge is not connected.\n${result.error}`,
          },
        ],
      };
    }
    return {
      content: [
        {
          type: "text" as const,
          text: `Bridge is connected!\n${JSON.stringify(result.data, null, 2)}`,
        },
      ],
    };
  }
);

server.tool(
  "get_game_objects",
  "List all game objects in the current s&box scene",
  {},
  async () => {
    const result = await bridgeRequest("/scene/objects");
    if (!result.success) {
      return {
        content: [{ type: "text" as const, text: result.error || "Failed to get game objects" }],
      };
    }
    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify(result.data, null, 2),
        },
      ],
    };
  }
);

server.tool(
  "get_scene_info",
  "Get information about the current s&box scene",
  {},
  async () => {
    const result = await bridgeRequest("/scene/info");
    if (!result.success) {
      return {
        content: [{ type: "text" as const, text: result.error || "Failed to get scene info" }],
      };
    }
    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify(result.data, null, 2),
        },
      ],
    };
  }
);

server.tool(
  "execute_command",
  "Execute a command in s&box",
  {
    command: z.string().describe("The command to execute in s&box"),
  },
  async ({ command }) => {
    const result = await bridgeRequest("/command", "POST", { command });
    if (!result.success) {
      return {
        content: [{ type: "text" as const, text: result.error || "Failed to execute command" }],
      };
    }
    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify(result.data, null, 2),
        },
      ],
    };
  }
);

server.tool(
  "get_console_output",
  "Get recent console output from s&box",
  {
    count: z
      .number()
      .optional()
      .describe("Number of recent log entries to retrieve (default: 50)"),
  },
  async ({ count }) => {
    const n = count || 50;
    const result = await bridgeRequest(`/console?count=${n}`);
    if (!result.success) {
      return {
        content: [{ type: "text" as const, text: result.error || "Failed to get console output" }],
      };
    }
    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify(result.data, null, 2),
        },
      ],
    };
  }
);

server.tool(
  "create_game_object",
  "Create a new game object in the s&box scene",
  {
    name: z.string().describe("Name for the new game object"),
    position: z
      .object({
        x: z.number(),
        y: z.number(),
        z: z.number(),
      })
      .optional()
      .describe("Position in world space (default: origin)"),
    components: z
      .array(z.string())
      .optional()
      .describe("Component type names to attach"),
  },
  async ({ name, position, components }) => {
    const result = await bridgeRequest("/scene/objects", "POST", {
      name,
      position: position || { x: 0, y: 0, z: 0 },
      components: components || [],
    });
    if (!result.success) {
      return {
        content: [{ type: "text" as const, text: result.error || "Failed to create game object" }],
      };
    }
    return {
      content: [
        {
          type: "text" as const,
          text: `Created game object "${name}"\n${JSON.stringify(result.data, null, 2)}`,
        },
      ],
    };
  }
);

server.tool(
  "get_project_files",
  "List project source files in the current s&box project",
  {
    path: z
      .string()
      .optional()
      .describe("Subdirectory path to list (default: project root)"),
    pattern: z
      .string()
      .optional()
      .describe('File pattern filter (e.g. "*.cs")'),
  },
  async ({ path, pattern }) => {
    const params = new URLSearchParams();
    if (path) params.set("path", path);
    if (pattern) params.set("pattern", pattern);
    const query = params.toString() ? `?${params.toString()}` : "";
    const result = await bridgeRequest(`/project/files${query}`);
    if (!result.success) {
      return {
        content: [{ type: "text" as const, text: result.error || "Failed to list project files" }],
      };
    }
    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify(result.data, null, 2),
        },
      ],
    };
  }
);

server.tool(
  "read_project_file",
  "Read the contents of a source file from the s&box project",
  {
    path: z.string().describe("Relative path to the file within the project"),
  },
  async ({ path }) => {
    const result = await bridgeRequest(
      `/project/file?path=${encodeURIComponent(path)}`
    );
    if (!result.success) {
      return {
        content: [{ type: "text" as const, text: result.error || "Failed to read file" }],
      };
    }
    return {
      content: [
        {
          type: "text" as const,
          text: typeof result.data === "string" ? result.data : JSON.stringify(result.data, null, 2),
        },
      ],
    };
  }
);

server.tool(
  "write_project_file",
  "Write or update a source file in the s&box project",
  {
    path: z.string().describe("Relative path to the file within the project"),
    content: z.string().describe("The file content to write"),
  },
  async ({ path, content }) => {
    const result = await bridgeRequest("/project/file", "POST", {
      path,
      content,
    });
    if (!result.success) {
      return {
        content: [{ type: "text" as const, text: result.error || "Failed to write file" }],
      };
    }
    return {
      content: [
        {
          type: "text" as const,
          text: `File written: ${path}`,
        },
      ],
    };
  }
);

server.tool(
  "compile_project",
  "Trigger a compilation of the s&box project and return results",
  {},
  async () => {
    const result = await bridgeRequest("/project/compile", "POST");
    if (!result.success) {
      return {
        content: [{ type: "text" as const, text: result.error || "Failed to compile project" }],
      };
    }
    return {
      content: [
        {
          type: "text" as const,
          text: JSON.stringify(result.data, null, 2),
        },
      ],
    };
  }
);

// --- Start server ---

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((error) => {
  console.error("Fatal error:", error);
  process.exit(1);
});
