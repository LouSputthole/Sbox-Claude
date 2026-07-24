import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";
import { existsSync, readFileSync, statSync } from "fs";
import { join } from "path";
import { inlineCaptureReply } from "./camera.js";

/**
 * Diagnostic tools (Batch 24 — "let Claude see its own errors"): read s&box's
 * editor log so Claude can check compile errors, exceptions, and Log.Info
 * output directly — instead of flying blind or relying on the user to relay
 * them.
 *
 * Deliberately reads the log FILE on the Node side (not over the bridge IPC),
 * so it works even when the s&box editor has crashed and the bridge is down —
 * which is exactly when you need the log most.
 *
 * Log path resolution:
 *   1. SBOX_LOG_PATH env var (explicit override — use this on macOS/Linux or
 *      non-Steam installs).
 *   2. Windows Steam auto-detect: parse steamapps/libraryfolders.vdf for each
 *      library, look for steamapps/common/sbox/logs/sbox-dev.log, pick newest.
 */

function locateSboxLog(): { path: string | null; tried: string[] } {
  const tried: string[] = [];

  const env = process.env.SBOX_LOG_PATH;
  if (env) {
    tried.push(env);
    if (existsSync(env)) return { path: env, tried };
  }

  if (process.platform === "win32") {
    const steamRoots = [
      "C:\\Program Files (x86)\\Steam",
      "C:\\Program Files\\Steam",
    ];
    const libs: string[] = [];
    for (const steam of steamRoots) {
      const vdf = join(steam, "steamapps", "libraryfolders.vdf");
      if (existsSync(vdf)) {
        libs.push(steam);
        try {
          const txt = readFileSync(vdf, "utf-8");
          for (const m of txt.matchAll(/"path"\s+"([^"]+)"/g)) {
            libs.push(m[1].replace(/\\\\/g, "\\"));
          }
        } catch {
          /* ignore unreadable vdf */
        }
      }
    }
    const candidates: string[] = [];
    for (const lib of libs) {
      const p = join(lib, "steamapps", "common", "sbox", "logs", "sbox-dev.log");
      tried.push(p);
      if (existsSync(p)) candidates.push(p);
    }
    if (candidates.length > 0) {
      candidates.sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs);
      return { path: candidates[0], tried };
    }
  }

  return { path: null, tried };
}

function tailLines(text: string, n: number): string[] {
  const lines = text.split(/\r?\n/);
  return lines.slice(Math.max(0, lines.length - n));
}

// A 3D point accepted as EITHER an object {x,y,z} OR a comma string "x,y,z",
// passed through unchanged. The C# handler parses both forms (source of truth).
// See the cross-language vector/color contract.
const Vector3Schema = z
  .union([
    z.object({ x: z.number(), y: z.number(), z: z.number() }),
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
      }, 'Must contain exactly three finite comma-separated numbers, e.g. "0,0,200"')
      .describe('Exact comma string "x,y,z", e.g. "0,0,200"'),
  ])
  .describe('World point — object {x,y,z} OR comma string "x,y,z"');

const RotationSchema = z
  .object({
    pitch: z.number().finite(),
    yaw: z.number().finite(),
    roll: z.number().finite(),
  })
  .strict();
export function registerDiagnosticTools(
  server: McpServer,
  bridge: BridgeClient
): void {
  // ── read_log ───────────────────────────────────────────────────────
  server.tool(
    "read_log",
    "Read s&box's editor log (sbox-dev.log) so Claude can see compile errors, exceptions, and Log.Info output directly. Reads the log file (not via the bridge), so it works even when the editor has crashed. If auto-detection fails (non-Windows / non-Steam install), set the SBOX_LOG_PATH environment variable to the full log path.",
    {
      lines: z
        .number()
        .int()
        .optional()
        .describe("How many lines from the end to return (default 80, max 1000)"),
      filter: z
        .string()
        .optional()
        .describe("Only return lines containing this substring (case-insensitive)"),
    },
    async (params) => {
      const { path, tried } = locateSboxLog();
      if (!path) {
        return {
          content: [
            {
              type: "text",
              text:
                "Error: couldn't locate sbox-dev.log. Set the SBOX_LOG_PATH environment variable to its full path.\nTried:\n" +
                tried.join("\n"),
            },
          ],
        };
      }
      let n = params.lines ?? 80;
      if (n < 1) n = 1;
      if (n > 1000) n = 1000;
      let text: string;
      try {
        text = readFileSync(path, "utf-8");
      } catch (e) {
        return {
          content: [{ type: "text", text: `Error reading ${path}: ${(e as Error).message}` }],
        };
      }
      let out = tailLines(text, n);
      if (params.filter) {
        const f = params.filter.toLowerCase();
        out = out.filter((l) => l.toLowerCase().includes(f));
      }
      const header = `# ${path}\n# last ${n} lines${params.filter ? ` · filter "${params.filter}"` : ""}\n\n`;
      return { content: [{ type: "text", text: header + out.join("\n") }] };
    }
  );

  // ── get_compile_errors ─────────────────────────────────────────────
  server.tool(
    "get_compile_errors",
    "Scan the recent s&box log for compile errors and exceptions — the fast way for Claude to confirm whether its last script/addon edit actually compiled. Reads sbox-dev.log directly (works even if the editor is mid-crash). Filters out the noisy 'Broken Reference: package.local.* (the compiler failed)' cascade (which masks the real cause) and surfaces the underlying '[Generic] Error | ...CSxxxx... file:line' diagnostics. Returns the real error lines, or an all-clear.",
    {
      lines: z
        .number()
        .int()
        .optional()
        .describe("How many lines from the end to scan (default 400, max 4000)"),
    },
    async (params) => {
      const { path } = locateSboxLog();
      if (!path) {
        return {
          content: [
            {
              type: "text",
              text: "Error: couldn't locate sbox-dev.log. Set the SBOX_LOG_PATH environment variable.",
            },
          ],
        };
      }
      let n = params.lines ?? 400;
      if (n < 1) n = 1;
      if (n > 4000) n = 4000;
      let text: string;
      try {
        text = readFileSync(path, "utf-8");
      } catch (e) {
        return {
          content: [{ type: "text", text: `Error reading ${path}: ${(e as Error).message}` }],
        };
      }
      const recent = tailLines(text, n);

      // The cascade: when a project's code fails to compile, every dependent
      // package (including the bridge's own editor assembly) emits a
      //   "Broken Reference: package.local.<x> (the compiler failed)"
      // line. There can be dozens of these and they MASK the real diagnostic —
      // so we drop them and surface the actual CSxxxx / [Generic] Error lines.
      const cascadeRe = /Broken Reference:.*\(the compiler failed\)/i;

      // Real compile diagnostics. We accept:
      //  - any line carrying a C# error code (error CS#### or "CS#### | file:line")
      //  - the "[Generic] Error | ..." diagnostic lines s&box emits
      //  - genuine compile-failure / exception markers
      // Whitelist (always surface, even with NO file path), e.g. a bare
      // location like "- :352,1" that s&box prints for project-level errors.
      const realErrorRe =
        /(error CS\d+|\bCS\d{3,5}\b|\[Generic\]\s*Error|Compile of .* Failed|Couldn't add project|Unhandled [Ee]xception|^\s*at Sandbox\.|StackTrace)/;
      const noFileWhitelistRe = /^\s*-\s*:\d+,\d+/; // e.g. "- :352,1"

      const cascadeLines = recent.filter((l) => cascadeRe.test(l));
      const realHits = recent.filter(
        (l) => !cascadeRe.test(l) && (realErrorRe.test(l) || noFileWhitelistRe.test(l))
      );

      if (realHits.length === 0) {
        if (cascadeLines.length > 0) {
          // We saw the masking cascade but none of the underlying diagnostics
          // fell within the scanned window — point Claude at the fuller log.
          return {
            content: [
              {
                type: "text",
                text:
                  `Saw ${cascadeLines.length} "Broken Reference: package.local.* (the compiler failed)" cascade line(s), ` +
                  `but the real compile error isn't in the last ${n} lines (the cascade masks it). ` +
                  `The underlying CSxxxx / [Generic] Error line is likely just above — call read_log ` +
                  `with more lines (e.g. lines: 1000) to find it, or get_compile_errors with a larger 'lines'.\n(${path})`,
              },
            ],
          };
        }
        return {
          content: [
            {
              type: "text",
              text: `No compile errors or exceptions in the last ${n} log lines — looks clean.\n(${path})`,
            },
          ],
        };
      }

      const suffix =
        cascadeLines.length > 0
          ? `\n\n(Filtered out ${cascadeLines.length} "Broken Reference … (the compiler failed)" cascade line(s) that mask the real cause.)`
          : "";
      return {
        content: [
          {
            type: "text",
            text: `Found ${realHits.length} real error line(s) in the last ${n} log lines:\n\n${realHits.join("\n")}${suffix}`,
          },
        ],
      };
    }
  );

  // ── frame_camera ─────────────────────────────────────────────────── (bridge)
  server.tool(
    "frame_camera",
    "Aim the s&box EDITOR viewport camera at a GameObject (by id) or a world point (position + optional radius), then call take_screenshot to capture that view. This is how Claude points its own screenshots at what it's working on — frame a spawned object, then screenshot to verify it actually looks right.",
    {
      id: z.string().optional().describe("GUID of a GameObject to frame on"),
      position: Vector3Schema
        .optional()
        .describe('World point to frame on (use instead of id) — object {x,y,z} or comma string "x,y,z"'),
      radius: z
        .number()
        .optional()
        .describe("Frame radius around the position, in units (default 128)"),
    },
    async (params) => {
      const res = await bridge.send("frame_camera", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
  );

  // -- screenshot_from -------------------------------------------------- (bridge alias)
  server.tool(
    "screenshot_from",
    "Historical alias of capture_view. Frame a GameObject or use an explicit position with optional lookAt/rotation, then return a labeled inline PNG. Uses a temporary non-main camera, works in edit or play mode, and never moves the scene's real camera.",
    {
      id: z.string().optional().describe("GUID of a GameObject to frame"),
      position: Vector3Schema
        .optional()
        .describe('Camera world position (use instead of id) — object {x,y,z} or exact comma string "x,y,z"'),
      lookAt: Vector3Schema
        .optional()
        .describe('World point to look at (pair with position) — object {x,y,z} or exact comma string "x,y,z"'),
      rotation: RotationSchema
        .optional()
        .describe("Explicit finite camera rotation (pair with position; alternative to lookAt)"),
      width: z.number().int().min(16).max(3840).optional().describe("Screenshot width (default 1280)"),
      height: z.number().int().min(16).max(2160).optional().describe("Screenshot height (default 720)"),
    },
    async (params) =>
      inlineCaptureReply(await bridge.send("screenshot_from", params), false)
  );
  // ── console_run ──────────────────────────────────────────────────── (bridge)
  server.tool(
    "console_run",
    "Run an s&box console command / ConCmd via Sandbox.ConsoleSystem.Run — e.g. a cvar ('sv_cheats 1') or a registered command. Also the invocation primitive behind execute_csharp. Fire-and-forget: returns only { ran, command } and does NOT capture console output — follow with read_log to see what the command printed.",
    {
      command: z.string().describe("The console command line to run"),
    },
    async (params) => {
      const res = await bridge.send("console_run", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── execute_csharp ───────────────────────────────────────────────── (orchestrated)
  let execCounter = 0;
  server.tool(
    "execute_csharp",
    "EXPERIMENTAL. Compile + run a C# snippet inside the s&box EDITOR (which is unsandboxed): writes a temp [ConCmd] into the project's Editor/ folder, hotloads, runs it, reads the result/exception from the log, then deletes the temp file. With expression:true, returns the JSON value of a single expression; otherwise runs statements. CAVEATS: each call triggers a hotload (~2-8s) and recompiles the project's editor assembly; a snippet that fails to compile is reported + cleaned up, but briefly taints the editor assembly until cleanup.",
    {
      code: z
        .string()
        .describe("C# to run (editor-context, unsandboxed). With expression:true, a single expression; otherwise statements."),
      expression: z
        .boolean()
        .optional()
        .describe("Treat code as an expression and return its JSON value (default false)"),
      timeoutMs: z
        .number()
        .int()
        .optional()
        .describe("Max wait for compile + run, ms (default 20000)"),
    },
    async (params) => {
      const id = `${Date.now().toString(36)}${++execCounter}`;
      const cmd = `claude_exec_${id}`;
      const filePath = `Editor/__Exec_${id}.cs`;
      const marker = `[EXEC ${id}]`;
      // Bare `Log` (via `using Sandbox;`), NOT `Sandbox.Log` — the latter stopped
      // resolving in project editor assemblies on engine 26.07.08b and broke every call.
      const inner = params.expression
        ? `var __r = (${params.code});\n\t\t\tLog.Info( "${marker} RESULT=" + System.Text.Json.JsonSerializer.Serialize( __r ) );`
        : `${params.code}\n\t\t\tLog.Info( "${marker} DONE" );`;
      const cs =
        `using Editor;\nusing Sandbox;\nusing System;\n\n` +
        `public static class __Exec_${id}\n{\n` +
        `\t[ConCmd( "${cmd}" )]\n\tpublic static void Run()\n\t{\n` +
        `\t\ttry\n\t\t{\n\t\t\t${inner}\n\t\t}\n` +
        `\t\tcatch ( System.Exception __e ) { Log.Error( "${marker} ERROR=" + __e.Message ); }\n` +
        `\t}\n}\n`;
      const timeout = params.timeoutMs ?? 20000;

      const wr = await bridge.send("write_file", { path: filePath, content: cs });
      if (!wr.success) {
        return { content: [{ type: "text", text: `execute_csharp: failed to write temp file: ${wr.error}` }] };
      }
      await bridge.send("trigger_hotload", {});

      const { path: logPath } = locateSboxLog();
      const startedAt = Date.now();
      let found: string | null = null;
      let compileErr: string | null = null;
      while (Date.now() - startedAt < timeout) {
        await new Promise((r) => setTimeout(r, 1500));
        await bridge.send("console_run", { command: cmd });
        if (logPath) {
          try {
            const txt = readFileSync(logPath, "utf-8");
            const tail = txt.slice(Math.max(0, txt.length - 30000));
            const mi = tail.lastIndexOf(marker);
            if (mi >= 0) {
              found = tail.slice(mi).split(/\r?\n/)[0];
              break;
            }
            if (/__Exec_/.test(tail) && /(error CS\d+|Compile of .* Failed)/i.test(tail)) {
              const errs = tail.split(/\r?\n/).filter((l) => /error CS\d+/i.test(l)).slice(-6).join("\n");
              if (errs) {
                compileErr = errs;
                break;
              }
            }
          } catch {
            /* log not ready yet */
          }
        }
      }

      // cleanup: remove the temp file + hotload back to a clean assembly
      try {
        await bridge.send("delete_script", { path: filePath });
      } catch {
        /* best effort */
      }
      try {
        await bridge.send("trigger_hotload", {});
      } catch {
        /* best effort */
      }

      if (compileErr) {
        return { content: [{ type: "text", text: `execute_csharp: compile error —\n${compileErr}` }] };
      }
      if (found) {
        let out = found;
        if (found.includes("RESULT=")) out = "RESULT = " + found.split("RESULT=")[1].trim();
        else if (found.includes("ERROR=")) out = "Runtime exception: " + found.split("ERROR=")[1].trim();
        else if (found.includes("DONE")) out = "Executed (no return value).";
        return { content: [{ type: "text", text: `execute_csharp ${id}: ${out}` }] };
      }
      return {
        content: [
          {
            type: "text",
            text: `execute_csharp ${id}: no result captured within ${timeout}ms — the snippet may still be compiling, or the log marker wasn't found. Try read_log with filter "${marker}".`,
          },
        ],
      };
    }
  );

  // ── get_bounds ─────────────────────────────────────────────────────── (bridge, Batch 33)
  server.tool(
    "get_bounds",
    "Get provenance-rich world bounds for a GameObject. Preserves legacy top-level center/size/extents/mins/maxs/radius/position/empty for compatibility, and adds render plus independent physics and solidPhysics aggregates. Collider outputs include trigger policy, capped contributor GameObject IDs and component type names, unsupported counts, and the exact API source. Read-only and play-aware.",
    {
      id: z.string().describe("GUID of the GameObject to measure"),
    },
    async (params) => {
      const res = await bridge.send("get_bounds", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );

  // ── find_objects_near ───────────────────────────────────────────
  server.tool(
    "find_objects_near",
    "Find GameObjects within a world-space radius of exactly one explicit position or originId, sorted nearest first. Optional name/component/tag filters are applied before the capped result, and the Scene root is excluded. Returns pivot-distance results plus requestedRadius/radiusClamped and total/showing/truncated/scanned; it deliberately does not pretend render or collider overlap is pivot distance. Read-only and play-aware.",
    {
      position: Vector3Schema.optional().describe("World-space search center; use instead of originId"),
      originId: z.string().optional().describe("GameObject GUID whose world position is the center"),
      radius: z.number().positive().max(1_000_000).optional().describe("Search radius in world units (default 256, max 1,000,000)"),
      limit: z.number().int().min(1).max(500).optional().describe("Maximum results (default 50)"),
      name: z.string().optional().describe("Case-insensitive GameObject name substring"),
      component: z.string().optional().describe("Required component type name"),
      tag: z.string().optional().describe("Required GameObject tag"),
      includeOrigin: z.boolean().optional().describe("Include originId itself (default false)"),
    },
    async (params) => {
      const res = await bridge.send("find_objects_near", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
  // -- screenshot_orbit ------------------------------------------------ (orchestrated, Batch 33)
  server.tool(
    "screenshot_orbit",
    "Capture a GameObject from several angles in one call without moving the real camera. Uses get_bounds for framing and capture_view temporary cameras for each angle, returning a labeled manifest followed by ordered inline PNG image blocks.",
    {
      id: z.string().describe("GUID of the GameObject to orbit"),
      shots: z.number().int().min(2).max(8).optional().describe("Number of angles (default 4)"),
      elevation: z.number().finite().min(0).max(1).optional().describe("Camera height factor from 0 to 1 (default 0.4)"),
      distance: z.number().finite().positive().optional().describe("Positive camera distance in units (default: auto from bounds)"),
      width: z.number().int().min(16).max(3840).optional().describe("Screenshot width (default 1280)"),
      height: z.number().int().min(16).max(2160).optional().describe("Screenshot height (default 720)"),
    },
    async (params) => {
      const boundsResponse = await bridge.send("get_bounds", { id: params.id });
      if (!boundsResponse.success) {
        return { content: [{ type: "text" as const, text: `Error (get_bounds): ${boundsResponse.error}` }] };
      }
      const data = boundsResponse.data as any;
      const center = data.center as { x: number; y: number; z: number };
      const sizeLength = Math.hypot(data.size.x, data.size.y, data.size.z);
      const distance = params.distance ?? Math.max(sizeLength * 1.6, 150);
      const shots = params.shots ?? 4;
      const elevation = params.elevation ?? 0.4;
      const width = params.width ?? 1280;
      const height = params.height ?? 720;
      const results: Array<Record<string, unknown>> = [];
      const captures: Array<{ path: string }> = [];

      for (let i = 0; i < shots; i++) {
        const angle = (2 * Math.PI * i) / shots;
        const dx = Math.cos(angle);
        const dy = Math.sin(angle);
        const length = Math.hypot(dx, dy, elevation) || 1;
        const cameraPosition = {
          x: center.x + (dx / length) * distance,
          y: center.y + (dy / length) * distance,
          z: center.z + (elevation / length) * distance,
        };
        const degrees = Math.round((angle * 180) / Math.PI);
        const capture = await bridge.send("screenshot_from", {
          position: cameraPosition,
          lookAt: center,
          width,
          height,
        });
        if (!capture.success) {
          results.push({ angle: degrees, error: capture.error });
          continue;
        }
        const pngPath = (capture.data as any)?.path;
        if (typeof pngPath !== "string" || pngPath.length === 0) {
          results.push({ angle: degrees, position: cameraPosition, error: "capture_view returned no PNG path" });
          continue;
        }
        captures.push({ path: pngPath });
        results.push({ angle: degrees, position: cameraPosition, captured: true });
      }

      const manifest = {
        orbited: data.name ?? params.id,
        center,
        distance: Math.round(distance),
        shots: results,
        note: `Captured ${captures.length}/${shots} angle(s); successful shots follow as ordered inline PNG image blocks.`,
      };
      return inlineCaptureReply(
        { success: true, data: { manifest, captures } },
        true
      );
    }
  );
  // ── capture_view ──────────────────────────────────────────────────── (bridge, Batch 34)
  server.tool(
    "capture_view",
    "Capture an inline PNG in edit or play mode. With no pose it renders the live main camera; pass exactly one id or position (+ lookAt or rotation) to use a temporary disabled/non-main camera that is removed afterwards. Returns a labeled manifest followed by the image block.",
    {
      id: z.string().optional().describe("GUID of a GameObject to frame (uses a temp camera)"),
      position: Vector3Schema
        .optional()
        .describe('Camera world position (temp camera; use instead of id) — object {x,y,z} or comma string "x,y,z"'),
      lookAt: Vector3Schema
        .optional()
        .describe('World point to look at (pair with position) — object {x,y,z} or comma string "x,y,z"'),
      rotation: RotationSchema
        .optional()
        .describe("Explicit camera rotation (pair with position)"),
      fov: z.number().optional().describe("Field of view for the temp camera"),
      renderUI: z.boolean().optional().describe("Include UI/HUD (default true). Renders world + world-space UI but NOT fullscreen screen-space panels (lobby/title overlays) — so capture_view sees 'through' menus; use take_screenshot for screen-space UI."),
      width: z.number().int().optional().describe("Width (default 1280)"),
      height: z.number().int().optional().describe("Height (default 720)"),
    },
    async (params) => {
      const res = await bridge.send("capture_view", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return inlineCaptureReply(res, false);
    }
  );
}
