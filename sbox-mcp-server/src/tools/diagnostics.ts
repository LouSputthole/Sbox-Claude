import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { BridgeClient } from "../transport/bridge-client.js";
import { existsSync, readFileSync, statSync, readdirSync } from "fs";
import { join, dirname } from "path";

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

/**
 * Derive the editor's screenshots folder from the located log path
 * (<sbox>/logs/sbox-dev.log → <sbox>/screenshots). SBOX_SCREENSHOTS_DIR overrides.
 */
function locateScreenshotsDir(): string | null {
  if (process.env.SBOX_SCREENSHOTS_DIR) return process.env.SBOX_SCREENSHOTS_DIR;
  const { path } = locateSboxLog();
  if (!path) return null;
  return join(dirname(dirname(path)), "screenshots");
}

/** Newest .png in a dir with mtime strictly greater than afterMs, or null. */
function newestPng(
  dir: string,
  afterMs: number
): { path: string; mtimeMs: number } | null {
  try {
    let best: { path: string; mtimeMs: number } | null = null;
    for (const f of readdirSync(dir)) {
      if (!f.toLowerCase().endsWith(".png")) continue;
      const fp = join(dir, f);
      const m = statSync(fp).mtimeMs;
      if (m > afterMs && (!best || m > best.mtimeMs)) best = { path: fp, mtimeMs: m };
    }
    return best;
  } catch {
    return null;
  }
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
    z.string().describe('Comma string "x,y,z", e.g. "0,0,200"'),
  ])
  .describe('World point — object {x,y,z} OR comma string "x,y,z"');

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

  // ── screenshot_from ──────────────────────────────────────────────── (bridge)
  server.tool(
    "screenshot_from",
    "Take a screenshot from a chosen angle. take_screenshot is locked to the scene's main camera; this temporarily moves that camera to frame your target, captures, then restores it — so Claude can finally AIM its own screenshots. Pass id (frame an object) OR position {x,y,z} with optional lookAt {x,y,z} or rotation {pitch,yaw,roll}. After it returns, read the newest PNG in the editor's screenshots folder.",
    {
      id: z.string().optional().describe("GUID of a GameObject to frame"),
      position: Vector3Schema
        .optional()
        .describe('Camera world position (use instead of id) — object {x,y,z} or comma string "x,y,z"'),
      lookAt: Vector3Schema
        .optional()
        .describe('World point to look at (pair with position) — object {x,y,z} or comma string "x,y,z"'),
      rotation: z
        .object({ pitch: z.number(), yaw: z.number(), roll: z.number() })
        .optional()
        .describe("Explicit camera rotation (pair with position)"),
      width: z.number().int().optional().describe("Screenshot width (default 1920)"),
      height: z.number().int().optional().describe("Screenshot height (default 1080)"),
    },
    async (params) => {
      const res = await bridge.send("screenshot_from", params);
      if (!res.success) {
        return { content: [{ type: "text", text: `Error: ${res.error}` }] };
      }
      return {
        content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }],
      };
    }
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
    "Get a GameObject's world-space bounding box. Returns { id, name, center, size, extents, mins, maxs, radius, position, empty } — objects with no renderer report empty:true (bounds collapse to the world position) plus an explanatory note. Feed center/radius into screenshot_from or frame_camera to frame the object, or use mins/maxs for placement math (screenshot_orbit calls this internally).",
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

  // ── screenshot_orbit ──────────────────────────────────────────────── (orchestrated, Batch 33)
  server.tool(
    "screenshot_orbit",
    "Capture a GameObject from several angles in ONE call — orbits the scene's main camera around the object and screenshots each angle, so Claude can verify 3D work from multiple sides instead of guessing from one. Drives get_bounds (framing) + screenshot_from per angle (each its own frame, the reliable capture path). Returns the saved PNG paths in order — READ them to inspect.",
    {
      id: z.string().describe("GUID of the GameObject to orbit"),
      shots: z
        .number()
        .int()
        .optional()
        .describe("Number of angles around the object (default 4, clamped 2-8)"),
      elevation: z
        .number()
        .optional()
        .describe("Camera height factor: 0 = level, 1 = high (default 0.4)"),
      distance: z
        .number()
        .optional()
        .describe("Camera distance in units (default: auto from bounds)"),
      width: z.number().int().optional().describe("Screenshot width (default 1280)"),
      height: z.number().int().optional().describe("Screenshot height (default 720)"),
    },
    async (params) => {
      const b = await bridge.send("get_bounds", { id: params.id });
      if (!b.success) {
        return { content: [{ type: "text", text: `Error (get_bounds): ${b.error}` }] };
      }
      const data = b.data as any;
      const c = data.center as { x: number; y: number; z: number };
      const sizeLen = Math.hypot(data.size.x, data.size.y, data.size.z);
      const dist = params.distance ?? Math.max(sizeLen * 1.6, 150);
      let shots = params.shots ?? 4;
      shots = Math.max(2, Math.min(8, shots));
      const elev = params.elevation ?? 0.4;
      const w = params.width ?? 1280;
      const h = params.height ?? 720;

      const ssDir = locateScreenshotsDir();
      let lastMtime = 0;
      if (ssDir) {
        const n = newestPng(ssDir, 0);
        if (n) lastMtime = n.mtimeMs;
      }

      const results: Array<Record<string, unknown>> = [];
      for (let i = 0; i < shots; i++) {
        // s&box names screenshots at 1-second granularity, so two shots in the
        // same wall-clock second overwrite each other. Space them out.
        if (i > 0) await new Promise((res) => setTimeout(res, 1100));
        const ang = (2 * Math.PI * i) / shots;
        const dx = Math.cos(ang);
        const dy = Math.sin(ang);
        const len = Math.hypot(dx, dy, elev) || 1;
        const camPos = {
          x: c.x + (dx / len) * dist,
          y: c.y + (dy / len) * dist,
          z: c.z + (elev / len) * dist,
        };
        const deg = Math.round((ang * 180) / Math.PI);
        const r = await bridge.send("screenshot_from", {
          position: camPos,
          lookAt: c,
          width: w,
          height: h,
        });
        if (!r.success) {
          results.push({ angle: deg, error: r.error });
          continue;
        }
        let file: string | null = null;
        if (ssDir) {
          const started = Date.now();
          while (Date.now() - started < 4000) {
            const n = newestPng(ssDir, lastMtime);
            if (n) {
              file = n.path;
              lastMtime = n.mtimeMs;
              break;
            }
            await new Promise((res) => setTimeout(res, 200));
          }
        }
        results.push({ angle: deg, position: camPos, file });
      }

      const files = results
        .filter((s) => typeof s.file === "string")
        .map((s) => s.file as string);
      const summary = {
        orbited: data.name ?? params.id,
        center: c,
        distance: Math.round(dist),
        shots: results,
        note: ssDir
          ? `Captured ${files.length}/${shots} angle(s). READ these PNGs to inspect the object from each side:\n${files.join("\n")}`
          : `Captured ${shots} angle(s), but couldn't locate the screenshots folder (set SBOX_LOG_PATH or SBOX_SCREENSHOTS_DIR). Read the newest PNGs in <sbox>/screenshots/.`,
      };
      return { content: [{ type: "text", text: JSON.stringify(summary, null, 2) }] };
    }
  );

  // ── capture_view ──────────────────────────────────────────────────── (bridge, Batch 34)
  server.tool(
    "capture_view",
    "Capture a PNG of the scene from a camera — and crucially this WORKS IN PLAY MODE, capturing the RUNNING game (via CameraComponent.RenderToBitmap, unlike take_screenshot/screenshot_from which are edit-only). With no args it renders the live main camera = the player's POV (incl. HUD). Pass position {x,y,z} (+ lookAt or rotation) or id (a GameObject to frame) to capture from a temporary camera that never disturbs the game's own camera. Returns the saved PNG's absolute 'path' — READ it to see the result.",
    {
      id: z.string().optional().describe("GUID of a GameObject to frame (uses a temp camera)"),
      position: Vector3Schema
        .optional()
        .describe('Camera world position (temp camera; use instead of id) — object {x,y,z} or comma string "x,y,z"'),
      lookAt: Vector3Schema
        .optional()
        .describe('World point to look at (pair with position) — object {x,y,z} or comma string "x,y,z"'),
      rotation: z
        .object({ pitch: z.number(), yaw: z.number(), roll: z.number() })
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
      return { content: [{ type: "text", text: JSON.stringify(res.data, null, 2) }] };
    }
  );
}
