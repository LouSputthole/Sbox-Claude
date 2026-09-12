import * as fs from "fs";
import * as path from "path";
import * as os from "os";

/**
 * File-based IPC transport for communicating with the s&box Bridge Addon.
 *
 * There is NO socket. Communication is entirely through a shared temp directory:
 * - MCP server writes request files (req_*.json) atomically — it writes
 *   req_*.json.tmp first, then renames it into place so the addon can never
 *   read a half-written request. The addon consumes only req_*.json and
 *   ignores *.tmp.
 * - s&box addon polls for them, RENAMES the request to req_*.json.processing
 *   when it picks it up, processes on the main editor thread, writes the
 *   response file (res_*.json, also temp+rename) and only THEN deletes the
 *   sentinel — so a timeout can tell "never picked up" from "still executing",
 *   and a crash mid-handler leaves the request on disk where the next editor
 *   session logs it instead of losing it silently.
 * - MCP server polls for response files
 *
 * The addon also maintains `status.json` as a HEARTBEAT. `heartbeat` is stamped
 * from the editor MAIN THREAD (frame loop) and `processHeartbeat` from the
 * addon's poll-timer thread, so a modal dialog (main thread blocked, process
 * alive) is distinguishable from a closed/crashed editor. "Connected" means
 * the main-thread heartbeat is recent — not merely that the file exists.
 */

/** Default IPC directory name under the system temp dir. */
const IPC_DIR_NAME = "sbox-bridge-ipc";

/** Suffix the addon appends to a request file it has picked up but not yet answered. */
export const PROCESSING_SUFFIX = ".processing";

/**
 * Wire-contract version stamped on every request (`protocolVersion`) and
 * published by the addon in status.json. Bump when the request/response
 * envelope changes shape; the addon refuses requests newer than it understands
 * with a readable error instead of misparsing them.
 */
export const IPC_PROTOCOL_VERSION = 1;

/** Strip a leading UTF-8 BOM (older addons may prepend one to IPC files). */
function stripBom(s: string): string {
  return s.charCodeAt(0) === 0xfeff ? s.slice(1) : s;
}

/**
 * Max age of the editor's status heartbeat before we consider the bridge dead.
 * The addon refreshes the heartbeat roughly once per second from its frame
 * loop, so this gives generous margin for GC pauses / frame hitches while still
 * catching a closed, crashed, or frame-stalled editor within a few seconds.
 */
export const STATUS_STALE_MS = 5000;

/**
 * How many consecutive polls a response file may fail to parse before the
 * request is failed with a diagnostic. The addon writes responses atomically
 * (temp + rename), so a persistent parse failure is a real error — not a
 * half-written file — and must surface instead of hiding behind the timeout.
 */
export const MAX_RESPONSE_PARSE_FAILURES = 5;

// ── diagnostics logging ────────────────────────────────────────────
//
// stdout is the MCP protocol channel, so transport diagnostics go to stderr.
// Every formerly-silent catch in this file now reports through here (GitHub
// issue #18) — behaviour is still non-throwing, but nothing is invisible.

export type IpcLogger = (message: string) => void;

let logger: IpcLogger = (m) => {
  try {
    process.stderr.write(`[sbox-mcp ipc] ${m}\n`);
  } catch {
    /* stderr closed — nothing sensible left to do */
  }
};

const seenWarnings = new Set<string>();

/** Replace the diagnostics sink (tests; embedding hosts). */
export function setIpcLogger(fn: IpcLogger | null): void {
  logger = fn ?? (() => {});
  seenWarnings.clear();
}

/** Log a diagnostic. `once` collapses repeats of the same message. */
export function ipcWarn(message: string, once = false): void {
  if (once) {
    if (seenWarnings.has(message)) return;
    seenWarnings.add(message);
  }
  logger(message);
}

function errText(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

/** Resolve the IPC directory, honoring an explicit override. */
export function resolveIpcDir(): string {
  const override = process.env.SBOX_BRIDGE_IPC_DIR;
  if (override && override.trim().length > 0) return override;
  return path.join(os.tmpdir(), IPC_DIR_NAME);
}

/** Result of inspecting the editor's status.json heartbeat. */
export interface StatusClassification {
  /** The editor reported `running: true`. */
  running: boolean;
  /** The main-thread heartbeat is recent enough to trust (or the addon predates heartbeats). */
  fresh: boolean;
  /** Age of the main-thread heartbeat in ms, or null if the addon doesn't emit one. */
  heartbeatMs: number | null;
  /**
   * Age of the addon's poll-thread heartbeat in ms, or null on addons that
   * don't emit one. Fresh while `fresh` is false means the editor PROCESS is
   * alive but its main thread is blocked (modal dialog / long operation).
   */
  processHeartbeatMs: number | null;
  /** The addon's own diagnosis of why it is unresponsive (e.g. "main thread stalled"), or null. */
  blockedBy: string | null;
  /** Wire-contract version the addon speaks, or null on addons that predate it. */
  protocolVersion: number | null;
}

function ageOf(value: unknown, nowMs: number): number | null {
  if (typeof value !== "string") return null;
  const t = Date.parse(value);
  return Number.isNaN(t) ? null : nowMs - t;
}

/**
 * Decide whether a parsed status.json means the bridge is live.
 *
 * A recent heartbeat → fresh. A stale heartbeat → not fresh (editor closed,
 * crashed, or frame loop stalled). No heartbeat field at all → treated as fresh
 * for backward compatibility with addons built before v1.3.2 (so upgrading the
 * MCP server alone never regresses a working setup to "disconnected").
 */
export function classifyStatus(
  status: unknown,
  nowMs: number,
  staleMs: number
): StatusClassification {
  const none: StatusClassification = {
    running: false,
    fresh: false,
    heartbeatMs: null,
    processHeartbeatMs: null,
    blockedBy: null,
    protocolVersion: null,
  };
  if (!status || typeof status !== "object") return none;

  const s = status as Record<string, unknown>;
  const running = s.running === true;
  const processHeartbeatMs = ageOf(s.processHeartbeat, nowMs);
  const blockedBy = typeof s.blockedBy === "string" && s.blockedBy.length > 0 ? s.blockedBy : null;
  const protocolVersion = typeof s.protocolVersion === "number" ? s.protocolVersion : null;
  const heartbeatMs = ageOf(s.heartbeat, nowMs);

  if (heartbeatMs !== null) {
    return {
      running,
      fresh: heartbeatMs <= staleMs,
      heartbeatMs,
      processHeartbeatMs,
      blockedBy,
      protocolVersion,
    };
  }
  // Old addon (no parseable heartbeat) — don't regress working setups.
  return { running, fresh: true, heartbeatMs: null, processHeartbeatMs, blockedBy, protocolVersion };
}

/**
 * Build a timeout error that names WHICH side of the IPC broke, so a 30s hang
 * is actionable instead of opaque.
 */
export function describeTimeout(opts: {
  reqConsumed: boolean;
  ipcDir: string;
  timeoutMs: number;
  command: string;
  status?: StatusClassification;
  /** The addon's req_*.json.processing sentinel exists: picked up, handler still running. */
  stillProcessing?: boolean;
}): string {
  const { reqConsumed, ipcDir, timeoutMs, command, status, stillProcessing } = opts;
  if (stillProcessing) {
    return (
      `Request '${command}' timed out after ${timeoutMs}ms. The editor picked it up and is STILL inside the ` +
      `handler (its req_*.json.processing sentinel is present) — a slow or hung handler, or the editor died ` +
      `mid-call. Check the s&box console / lifeline read_log for [SboxBridge] lines; if the editor is gone, ` +
      `the next session will log this request as discarded.`
    );
  }
  if (!reqConsumed) {
    // The addon now leaves the request file in place until it has WRITTEN the
    // response, so "not consumed" means either nobody is polling or the main
    // thread never got to it. The status heartbeats tell those apart.
    if (status && status.processHeartbeatMs !== null && status.processHeartbeatMs <= STATUS_STALE_MS && !status.fresh) {
      return (
        `Request '${command}' timed out after ${timeoutMs}ms. The s&box editor PROCESS is alive (addon poll thread ` +
        `heartbeat ${status.processHeartbeatMs}ms ago) but its MAIN THREAD has not ticked for ${status.heartbeatMs}ms` +
        `${status.blockedBy ? ` — the addon reports: ${status.blockedBy}` : ""}. This is almost always a modal ` +
        `dialog in the editor (e.g. "External Changes Detected" after a scene file changed on disk) or a very long ` +
        `operation. Bring the editor to the foreground and dismiss the dialog; if none is visible, use the lifeline ` +
        `read_log to see what it is doing, or restart the editor.`
      );
    }
    return (
      `Request '${command}' timed out after ${timeoutMs}ms. The s&box editor never picked up the request ` +
      `(its req_*.json file was not consumed). Likely causes: s&box isn't running, the Claude Bridge addon ` +
      `failed to load, or the editor and MCP server resolved different IPC directories (server is using: ` +
      `${ipcDir}). Open the s&box editor console and check for [SboxBridge] lines — it logs the directory it ` +
      `is watching; set SBOX_BRIDGE_IPC_DIR on both sides if they disagree.`
    );
  }
  return (
    `Request '${command}' timed out after ${timeoutMs}ms. The editor consumed the request but never wrote a ` +
    `response. Its frame loop may be stalled (e.g. the s&box window is unfocused or minimized) or the handler ` +
    `errored. Check the s&box editor console for [SboxBridge] errors.`
  );
}

/** A single command request sent to the s&box Bridge. */
export interface BridgeRequest {
  id: string;
  command: string;
  params: Record<string, unknown>;
  /** Wire-contract version (IPC_PROTOCOL_VERSION). */
  protocolVersion: number;
}

/** Response from the s&box Bridge. Check `success` before reading `data`. */
export interface BridgeResponse {
  id: string;
  success: boolean;
  data?: unknown;
  error?: string;
}

/**
 * File-based IPC client that communicates with the s&box Bridge Addon.
 */
export class BridgeClient {
  private requestCounter = 0;
  private ipcDir: string;
  private connected = false;
  private lastPongTime = 0;

  static readonly POLL_INTERVAL_MS = 50; // 50ms polling for responses
  static readonly STATUS_CHECK_INTERVAL_MS = 5000;

  constructor() {
    this.ipcDir = resolveIpcDir();
  }

  /** The directory this client reads/writes IPC files in. */
  getIpcDir(): string {
    return this.ipcDir;
  }

  private statusPath(): string {
    return path.join(this.ipcDir, "status.json");
  }

  /** Read + classify the editor's status heartbeat. Never throws. */
  readStatus(): StatusClassification {
    const none: StatusClassification = {
      running: false,
      fresh: false,
      heartbeatMs: null,
      processHeartbeatMs: null,
      blockedBy: null,
      protocolVersion: null,
    };
    let raw: string;
    try {
      // Strip a UTF-8 BOM in case an older addon wrote one.
      raw = stripBom(fs.readFileSync(this.statusPath(), "utf8"));
    } catch (err) {
      // A missing status.json is the normal "editor not running" state — not
      // worth a log line. Anything else (permissions, EISDIR) is.
      const code = (err as NodeJS.ErrnoException)?.code;
      if (code !== "ENOENT") ipcWarn(`cannot read ${this.statusPath()}: ${errText(err)}`, true);
      return none;
    }
    try {
      return classifyStatus(JSON.parse(raw), Date.now(), STATUS_STALE_MS);
    } catch (err) {
      // The addon writes status.json atomically, so a malformed file is a real
      // problem (wrong encoding, a foreign file in the IPC dir) — say so.
      ipcWarn(`malformed ${this.statusPath()} (${errText(err)}) — treating the bridge as not running`, true);
      return none;
    }
  }

  /** Age of the editor's last heartbeat in ms, or null if unavailable. */
  getHeartbeatAgeMs(): number | null {
    return this.readStatus().heartbeatMs;
  }

  /**
   * Verify the s&box Bridge is live (recent heartbeat), throwing a specific
   * error if it is missing or stale.
   */
  async connect(): Promise<void> {
    if (!fs.existsSync(this.ipcDir)) {
      fs.mkdirSync(this.ipcDir, { recursive: true });
    }

    const s = this.readStatus();
    if (s.running && s.fresh) {
      this.connected = true;
      this.lastPongTime = Date.now();
      return;
    }
    this.connected = false;

    if (s.running && !s.fresh) {
      const processAlive = s.processHeartbeatMs !== null && s.processHeartbeatMs <= STATUS_STALE_MS;
      throw new Error(
        `s&box Bridge heartbeat is stale at ${this.statusPath()} (last beat ${s.heartbeatMs}ms ago, ` +
          `limit ${STATUS_STALE_MS}ms). ` +
          (processAlive
            ? `The editor process is alive but its main thread is blocked` +
              `${s.blockedBy ? ` (${s.blockedBy})` : ""} — dismiss any modal dialog in the editor. `
            : `The editor likely closed, crashed, or its frame loop stalled. `) +
          `IPC dir: ${this.ipcDir}`
      );
    }
    throw new Error(
      `Cannot connect to s&box Bridge. No live status at ${this.statusPath()}. Is s&box running with the ` +
        `Claude Bridge addon? (MCP server IPC dir: ${this.ipcDir})`
    );
  }

  /** Best-effort unlink that reports (rather than hides) a failure. */
  private tryUnlink(file: string, why: string): void {
    try {
      if (fs.existsSync(file)) fs.unlinkSync(file);
    } catch (err) {
      ipcWarn(`could not delete ${path.basename(file)} (${why}): ${errText(err)}`);
    }
  }

  /**
   * Write a request file atomically: write to a .tmp sibling, then rename into
   * place. The C# poller only consumes `req_*.json` (it ignores `*.tmp`), so
   * it can never observe a half-written request for a large payload — the
   * rename is atomic on the same volume. Returns an error string on failure.
   */
  private writeRequestFile(reqPath: string, payload: unknown): string | null {
    const tmpPath = `${reqPath}.tmp`;
    try {
      fs.writeFileSync(tmpPath, JSON.stringify(payload), "utf8");
      fs.renameSync(tmpPath, reqPath);
      return null;
    } catch (err) {
      ipcWarn(`failed to write ${path.basename(reqPath)}: ${errText(err)}`);
      // Best-effort cleanup of a partial temp file so it doesn't linger.
      this.tryUnlink(tmpPath, "partial request temp file");
      return `Failed to write request file: ${err}`;
    }
  }

  /**
   * Poll for the response file to `reqPath`. Resolves with the parsed response,
   * or with a diagnostic failure on timeout / a persistently unparseable file.
   */
  private awaitResponse(
    id: string,
    command: string,
    reqPath: string,
    resPath: string,
    timeoutMs: number
  ): Promise<BridgeResponse> {
    const startTime = Date.now();
    let parseFailures = 0;

    return new Promise((resolve) => {
      const poll = setInterval(() => {
        // Check timeout
        if (Date.now() - startTime > timeoutMs) {
          clearInterval(poll);
          // Whether the editor ever consumed the request tells us which side broke.
          const reqConsumed = !fs.existsSync(reqPath);
          const stillProcessing = fs.existsSync(reqPath + PROCESSING_SUFFIX);
          // Clean up the request file if the editor never took it (the .processing
          // sentinel belongs to the editor — it removes it when the handler returns).
          this.tryUnlink(reqPath, "timed-out request");
          resolve({
            id,
            success: false,
            error: describeTimeout({
              reqConsumed,
              stillProcessing,
              ipcDir: this.ipcDir,
              timeoutMs,
              command,
              status: this.readStatus(),
            }),
          });
          return;
        }

        // Check for response file
        if (!fs.existsSync(resPath)) return;

        let responseJson: string;
        try {
          // Defensively strip a BOM in case an older addon wrote one.
          responseJson = stripBom(fs.readFileSync(resPath, "utf8"));
        } catch (err) {
          // Can race the addon's rename on some filesystems — retry next poll.
          ipcWarn(`transient read failure on ${path.basename(resPath)}: ${errText(err)}`, true);
          return;
        }

        let response: BridgeResponse;
        try {
          response = JSON.parse(responseJson) as BridgeResponse;
        } catch (err) {
          // The addon writes responses atomically, so this is not a half-written
          // file. Log it, allow a few polls of grace for exotic filesystems, then
          // fail LOUDLY instead of spinning until the timeout (the UTF-8 BOM bug
          // hid behind exactly this silence — issue #18).
          parseFailures++;
          if (parseFailures < MAX_RESPONSE_PARSE_FAILURES) return;
          clearInterval(poll);
          ipcWarn(`unparseable response ${path.basename(resPath)} for '${command}': ${errText(err)}`);
          this.tryUnlink(resPath, "unparseable response");
          resolve({
            id,
            success: false,
            error:
              `Response for '${command}' could not be parsed as JSON (${errText(err)}). ` +
              `First 200 chars: ${JSON.stringify(responseJson.slice(0, 200))}. ` +
              `This usually means an addon/server version mismatch or a foreign file in ${this.ipcDir}.`,
          });
          return;
        }

        // Clean up response file
        this.tryUnlink(resPath, "consumed response");

        clearInterval(poll);
        this.lastPongTime = Date.now();
        resolve(response);
      }, BridgeClient.POLL_INTERVAL_MS);
    });
  }

  /**
   * Send a command to the s&box Bridge and wait for its response.
   */
  async send(
    command: string,
    params: Record<string, unknown> = {},
    timeoutMs = 30000
  ): Promise<BridgeResponse> {
    // Try to connect if not connected
    if (!this.connected) {
      try {
        await this.connect();
      } catch (err) {
        return {
          id: "",
          success: false,
          error:
            err instanceof Error
              ? err.message
              : "Not connected to s&box Bridge. Make sure s&box is running with the Claude Bridge addon installed.",
        };
      }
    }

    const id = `${++this.requestCounter}_${Date.now()}`;
    const request: BridgeRequest = { id, command, params, protocolVersion: IPC_PROTOCOL_VERSION };

    // Ensure IPC directory exists
    if (!fs.existsSync(this.ipcDir)) {
      fs.mkdirSync(this.ipcDir, { recursive: true });
    }

    const reqPath = path.join(this.ipcDir, `req_${id}.json`);
    const resPath = path.join(this.ipcDir, `res_${id}.json`);
    const writeErr = this.writeRequestFile(reqPath, request);
    if (writeErr) return { id, success: false, error: writeErr };

    return this.awaitResponse(id, command, reqPath, resPath, timeoutMs);
  }

  /**
   * Send multiple commands as a batch.
   */
  async sendBatch(
    commands: Array<{ command: string; params?: Record<string, unknown> }>,
    timeoutMs = 30000
  ): Promise<BridgeResponse> {
    if (!this.connected) {
      try {
        await this.connect();
      } catch (err) {
        return {
          id: "",
          success: false,
          error: err instanceof Error ? err.message : "Not connected to s&box Bridge.",
        };
      }
    }

    const id = `batch_${++this.requestCounter}_${Date.now()}`;
    const request = { id, commands, protocolVersion: IPC_PROTOCOL_VERSION };

    if (!fs.existsSync(this.ipcDir)) {
      fs.mkdirSync(this.ipcDir, { recursive: true });
    }

    const reqPath = path.join(this.ipcDir, `req_${id}.json`);
    const resPath = path.join(this.ipcDir, `res_${id}.json`);
    const writeErr = this.writeRequestFile(reqPath, request);
    if (writeErr) return { id, success: false, error: writeErr };

    return this.awaitResponse(id, "batch", reqPath, resPath, timeoutMs);
  }

  /**
   * Liveness check. Returns elapsed ms if the heartbeat is recent, else -1.
   */
  async ping(): Promise<number> {
    const start = Date.now();
    const s = this.readStatus();
    if (s.running && s.fresh) {
      this.lastPongTime = Date.now();
      return Date.now() - start;
    }
    return -1;
  }

  isConnected(): boolean {
    const s = this.readStatus();
    this.connected = s.running && s.fresh;
    return this.connected;
  }

  getLastPongTime(): number {
    return this.lastPongTime;
  }

  disconnect(): void {
    this.connected = false;
  }
}
