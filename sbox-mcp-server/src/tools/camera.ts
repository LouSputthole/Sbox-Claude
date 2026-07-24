import { existsSync, readFileSync, unlinkSync } from "node:fs";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

const FiniteNumber = z.number().finite();
const FiniteTripleStringSchema = (example: string) =>
  z
    .string()
    .refine((value) => {
      const number = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/;
      const parts = value.split(",").map((part) => part.trim());
      return (
        parts.length === 3 &&
        parts.every(
          (part) => number.test(part) && Number.isFinite(Number(part))
        )
      );
    }, `Must contain exactly three finite comma-separated numbers, e.g. "${example}"`);

const Vector3Schema = z.union([
  z.object({ x: FiniteNumber, y: FiniteNumber, z: FiniteNumber }).strict(),
  FiniteTripleStringSchema("0,0,200").describe('Exact comma string "x,y,z"'),
]);

const RotationSchema = z.union([
  z
    .object({
      pitch: FiniteNumber,
      yaw: FiniteNumber,
      roll: FiniteNumber,
    })
    .strict(),
  FiniteTripleStringSchema("0,90,0").describe(
    'Exact comma string "pitch,yaw,roll"'
  ),
]);
type Content =
  | { type: "text"; text: string }
  | { type: "image"; data: string; mimeType: string };

function textReply(res: any) {
  return res.success
    ? { content: [{ type: "text" as const, text: JSON.stringify(res.data, null, 2) }] }
    : { content: [{ type: "text" as const, text: `Error: ${res.error}` }] };
}

export function inlineCaptureReply(res: any, many: boolean) {
  if (!res.success) {
    return { content: [{ type: "text" as const, text: `Error: ${res.error}` }] };
  }

  const data = res.data as any;
  const paths: string[] = many
    ? (data?.captures ?? [])
        .map((capture: any) => capture.path)
        .filter((path: unknown): path is string => typeof path === "string" && path.length > 0)
    : [data?.path].filter(
        (path: unknown): path is string => typeof path === "string" && path.length > 0
      );
  const manifest = data?.manifest ?? data;
  const content: Content[] = [
    { type: "text", text: JSON.stringify(manifest, null, 2) },
  ];
  for (const path of paths) {
    try {
      if (!existsSync(path)) throw new Error("file does not exist");
      content.push({
        type: "image",
        data: readFileSync(path).toString("base64"),
        mimeType: "image/png",
      });
    } catch (error) {
      content.push({
        type: "text",
        text: `Inline PNG conversion failed for ${path}: ${
          error instanceof Error ? error.message : String(error)
        }`,
      });
    } finally {
      try {
        unlinkSync(path);
      } catch {
        // Best effort: the addon wrote this bridge-owned temp file.
      }
    }
  }
  return { content };
}

export function registerCameraTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  server.tool(
    "save_camera_bookmark",
    "Save or replace a named camera bookmark in editor-session memory. Pass targetId for a deterministic framed object pose, position plus lookAt/rotation for an explicit pose, or omit both to snapshot the main camera. Returns the resolved pose; bookmarks reset on hotload/restart. Follow with list_camera_bookmarks or capture_camera_set.",
    {
      name: z.string().min(1).max(64).describe("Case-insensitive bookmark name"),
      targetId: z.string().optional().describe("GameObject GUID to frame"),
      position: Vector3Schema.optional().describe("Explicit camera position"),
      lookAt: Vector3Schema.optional().describe("World point to aim at; pair with position"),
      rotation: RotationSchema.optional().describe("Explicit rotation; alternative to lookAt"),
      fov: FiniteNumber.min(1).max(179).optional().describe("Field of view in degrees (1-179)"),
    },
    async (params) => textReply(await bridge.send("save_camera_bookmark", params))
  );

  server.tool(
    "list_camera_bookmarks",
    "List all session-scoped camera bookmarks in stable name order. Returns resolved pose, optional target/lookAt, FOV, and saved time. Read-only; feed returned names into capture_camera_set. State resets on addon hotload or editor restart.",
    {},
    async (params) => textReply(await bridge.send("list_camera_bookmarks", params))
  );

  server.tool(
    "delete_camera_bookmark",
    "Delete one session-scoped camera bookmark and its comparison baselines. Returns deleted=false when already absent. Use list_camera_bookmarks to inspect remaining names.",
    {
      name: z.string().min(1).max(64).describe("Case-insensitive bookmark name"),
    },
    async (params) => textReply(await bridge.send("delete_camera_bookmark", params))
  );

  server.tool(
    "capture_camera_set",
    "Capture 1-8 saved camera bookmarks in exact order. Returns a labeled manifest followed by ordered inline PNGs. Every successful run transactionally becomes the previous session baseline within a 64-entry/64 MiB memory budget; comparePrevious reports SkiaSharp RGBA delta metrics. renderUI defaults false for stable comparisons. Physics-trace occlusion is diagnostic and never moves the camera.",
    {
      names: z.array(z.string()).min(1).max(8).describe("Ordered bookmark names; duplicates are rejected"),
      comparePrevious: z.boolean().optional().describe("Compare against the previous capture with identical name/settings"),
      diffThreshold: z.number().int().min(0).max(255).optional().describe("Changed-pixel RGBA threshold (default 8)"),
      width: z.number().int().min(16).max(1600).optional().describe("Image width (default 960)"),
      height: z.number().int().min(16).max(1200).optional().describe("Image height (default 540)"),
      renderUI: z.boolean().optional().describe("Include screen-space UI (default false)"),
      checkOcclusion: z.boolean().optional().describe("Report blocking physics geometry (default true)"),
    },
    async (params) =>
      inlineCaptureReply(await bridge.send("capture_camera_set", params), true)
  );

  server.tool(
    "capture_topdown",
    "Capture a deterministic orthographic top-down inline PNG over targetId or an explicit center. Returns camera pose, fixed screen axes, ground-plane bounds, and world-units-per-pixel for measurement/placement. renderUI defaults false; output is capped at 1600x1200.",
    {
      targetId: z.string().optional().describe("GameObject GUID whose bounds determine framing"),
      center: Vector3Schema.optional().describe("Explicit world center; use instead of targetId"),
      worldHeight: FiniteNumber.positive().max(100_000).optional().describe("Screen-vertical ground span in world units (max 100,000)"),
      cameraHeight: FiniteNumber.positive().max(1_000_000).optional().describe("Requested camera height above center (max 1,000,000); raised when needed to clear the target top"),
      width: z.number().int().min(16).max(1600).optional().describe("Image width (default 1024)"),
      height: z.number().int().min(16).max(1200).optional().describe("Image height (default 1024)"),
      renderUI: z.boolean().optional().describe("Include screen-space UI (default false)"),
    },
    async (params) =>
      inlineCaptureReply(await bridge.send("capture_topdown", params), false)
  );
}
