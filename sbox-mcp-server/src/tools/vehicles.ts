import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";

/**
 * Vehicle tools (Batch 54): create_vehicle_controller, create_seat_system,
 * tune_vehicle, create_physics_grab_tool — the corpus vehicles theme.
 */
export function registerVehicleTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── create_vehicle_controller ────────────────────────────────────
  server.tool(
    "create_vehicle_controller",
    "Generate a drivable raycast-car component: 4-corner spring/damper suspension, mass-scaled engine force, speed-scaled yaw steering, lateral grip (lower = drift), and a BUILT-IN driver seat (press E to enter — the host assigns the driver network ownership for smooth driving — E to exit, controller input auto-disabled/restored). Attach it plus a Rigidbody and collider to any prop and it drives. Returns { created, path, className, nextSteps } — follow with trigger_hotload + compile_status, then batch_add_component the parts. Apply handling presets with tune_vehicle. HONEST LIMIT: compiles + runs is verified; driving FEEL needs a human playtest — tune from the inspector while playing",
    {
      name: z
        .string()
        .optional()
        .describe("Class/file name (default 'VehicleController' -> Code/VehicleController.cs). Errors if the file exists"),
      directory: z
        .string()
        .optional()
        .describe("Directory for the .cs file. Default 'Code'"),
      engineForce: z
        .number()
        .optional()
        .describe("Engine force (mass-scaled). Default 900"),
      steerStrength: z
        .number()
        .optional()
        .describe("Yaw steering torque (mass-scaled). Default 4000"),
      grip: z
        .number()
        .optional()
        .describe("Lateral grip 0-1 — fraction of sideways velocity killed per tick; lower = drift. Default 0.85"),
    },
    async (params) => {
      const res = await bridge.send("create_vehicle_controller", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_seat_system ───────────────────────────────────────────
  server.tool(
    "create_seat_system",
    "Generate a standalone networked one-occupant seat component: press E (use) to sit — claims route through the host so seats can't be shared — E to stand; the occupant parents to the seat with their controller input disabled and restored on exit, which tries each ExitOffsets entry and takes the first spot with clearance. Static OnOccupantChanged event for camera/UI. For chairs, benches, turret mounts; vehicles get a seat built into create_vehicle_controller. Returns { created, path, className, nextSteps } — trigger_hotload + compile_status, then attach to any prop",
    {
      name: z
        .string()
        .optional()
        .describe("Class/file name (default 'Seat' -> Code/Seat.cs). Errors if the file exists"),
      directory: z
        .string()
        .optional()
        .describe("Directory for the .cs file. Default 'Code'"),
    },
    async (params) => {
      const res = await bridge.send("create_seat_system", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── tune_vehicle ─────────────────────────────────────────────────
  server.tool(
    "tune_vehicle",
    "Apply a handling preset to a vehicle component on a GameObject: arcade (forgiving), drift (low grip, sharp steering), offroad (soft long-travel suspension), or race (fast, planted). Sets EngineForce/MaxSpeed/SteerStrength/GripFactor/SuspensionStrength/SuspensionDamping by name via reflection — targets create_vehicle_controller scaffolds out of the box, partially tunes anything exposing those property names. Returns { tuned, preset, component, applied, missing } — missing lists properties the component lacks. Auto-finds the first component with 'Vehicle' in its type name; pass component to target explicitly. Scene-mutating: refused during play mode (use set_property on runtime objects while playing)",
    {
      id: z
        .string()
        .describe("GUID of the GameObject carrying the vehicle component (from find_objects)"),
      preset: z
        .enum(["arcade", "drift", "offroad", "race"])
        .describe("Handling preset to apply"),
      component: z
        .string()
        .optional()
        .describe("Component type name to tune. Default: first component with 'Vehicle' in its name"),
    },
    async (params) => {
      const res = await bridge.send("tune_vehicle", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // ── create_physics_grab_tool ─────────────────────────────────────
  server.tool(
    "create_physics_grab_tool",
    "Generate a physgun-lite player component: hold attack2 while looking at a Rigidbody prop within Range to grab it — it spring-follows a point in front of your view with physics LIVE (collides and swings, unlike the parented create_carry_system) — attack1 throws it, release lets go. Grab requests route through the host, which assigns the grabber network ownership. Returns { created, path, className, nextSteps } — trigger_hotload + compile_status, attach to the player object; ensure_input_action if attack1/attack2 aren't bound",
    {
      name: z
        .string()
        .optional()
        .describe("Class/file name (default 'PhysicsGrabTool' -> Code/PhysicsGrabTool.cs). Errors if the file exists"),
      directory: z
        .string()
        .optional()
        .describe("Directory for the .cs file. Default 'Code'"),
    },
    async (params) => {
      const res = await bridge.send("create_physics_grab_tool", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );
}
