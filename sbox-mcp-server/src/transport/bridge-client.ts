import WebSocket from "ws";

/**
 * WebSocket transport layer for communicating with the s&box Bridge Addon.
 *
 * This module provides the sole communication channel between the MCP server
 * and the s&box editor. All tool calls flow through {@link BridgeClient.send}
 * as JSON messages over a WebSocket connection to localhost:29015 (configurable).
 *
 * Protocol: Each request gets a unique ID. The Bridge responds with the same ID,
 * allowing multiple in-flight requests. See {@link BridgeRequest} and {@link BridgeResponse}.
 */

/** A single command request sent to the s&box Bridge over WebSocket. */
export interface BridgeRequest {
  id: string;
  command: string;
  params: Record<string, unknown>;
}

/** Response from the s&box Bridge. Check `success` before reading `data`. */
export interface BridgeResponse {
  id: string;
  success: boolean;
  data?: unknown;
  error?: string;
}

/**
 * WebSocket client that connects to the s&box Bridge Addon running inside the editor.
 *
 * Handles the full connection lifecycle: initial connect, auto-reconnect on
 * disconnect (3s delay), ping/pong keepalive (15s interval, disconnects after
 * 3 missed pings), per-request timeouts (default 30s), and graceful rejection
 * of all in-flight requests when the connection drops.
 *
 * Usage: Instantiate once, call {@link connect}, then use {@link send} for each tool call.
 * The client auto-reconnects on failure, so callers rarely need to call connect() directly.
 */
export class BridgeClient {
  private ws: WebSocket | null = null;
  private pendingRequests = new Map<
    string,
    {
      resolve: (value: BridgeResponse) => void;
      reject: (reason: Error) => void;
      timer: ReturnType<typeof setTimeout>;
    }
  >();
  private requestCounter = 0;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private pingTimer: ReturnType<typeof setTimeout> | null = null;
  private missedPings = 0;
  private url: string;
  private host: string;
  private port: number;
  private connected = false;
  private lastPongTime = 0;

  static readonly PING_INTERVAL_MS = 15000;
  static readonly MAX_MISSED_PINGS = 3;
  static readonly RECONNECT_DELAY_MS = 3000;

  /**
   * @param host - WebSocket host, defaults to 127.0.0.1
   * @param port - WebSocket port, defaults to 29015 (configurable via SBOX_BRIDGE_PORT)
   */
  constructor(host = "127.0.0.1", port = 29015) {
    this.host = host;
    this.port = port;
    this.url = `ws://${host}:${port}`;
  }

  /**
   * Establish the WebSocket connection to the s&box Bridge.
   * Sets up message, pong, close, and error handlers, and starts the ping keepalive loop.
   * Rejects if the initial connection fails (e.g., s&box is not running).
   */
  async connect(): Promise<void> {
    return new Promise((resolve, reject) => {
      try {
        this.ws = new WebSocket(this.url);

        this.ws.on("open", () => {
          this.connected = true;
          this.missedPings = 0;
          this.lastPongTime = Date.now();
          this.startPingLoop();
          resolve();
        });

        this.ws.on("pong", () => {
          this.missedPings = 0;
          this.lastPongTime = Date.now();
        });

        this.ws.on("message", (data: WebSocket.RawData) => {
          try {
            const response = JSON.parse(data.toString()) as BridgeResponse;
            const pending = this.pendingRequests.get(response.id);
            if (pending) {
              clearTimeout(pending.timer);
              this.pendingRequests.delete(response.id);
              pending.resolve(response);
            }
          } catch {
            // Ignore malformed messages
          }
        });

        this.ws.on("close", () => {
          this.connected = false;
          this.stopPingLoop();
          this.rejectAllPending("Connection closed");
          this.scheduleReconnect();
        });

        this.ws.on("error", (err: Error) => {
          if (!this.connected) {
            reject(
              new Error(
                `Cannot connect to s&box Bridge at ${this.url}. Is s&box running with the Bridge Addon? (${err.message})`
              )
            );
          } else {
            console.error(`[sbox-mcp] WebSocket error: ${err.message}`);
          }
        });
      } catch (err) {
        reject(err);
      }
    });
  }

  /**
   * Send a command to the s&box Bridge and wait for its response.
   * Auto-reconnects if the WebSocket is not currently connected.
   * @param command - Bridge command name (matches the MCP tool name 1:1)
   * @param params - Command parameters, forwarded as JSON to the Bridge handler
   * @param timeoutMs - Per-request timeout in ms (default 30s)
   * @returns The Bridge response; check `success` before reading `data`
   */
  async send(
    command: string,
    params: Record<string, unknown> = {},
    timeoutMs = 30000
  ): Promise<BridgeResponse> {
    if (!this.ws || !this.connected) {
      try {
        await this.connect();
      } catch {
        return {
          id: "",
          success: false,
          error:
            "Not connected to s&box Bridge. Make sure s&box is running with the Bridge Addon installed.",
        };
      }
    }

    // Guard: ensure ws is valid after reconnect
    if (!this.ws) {
      return { id: "", success: false, error: "Connection failed" };
    }

    const id = `req_${++this.requestCounter}_${Date.now()}`;
    const request: BridgeRequest = { id, command, params };
    const ws = this.ws;

    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        this.pendingRequests.delete(id);
        resolve({
          id,
          success: false,
          error: `Request timed out after ${timeoutMs}ms`,
        });
      }, timeoutMs);

      this.pendingRequests.set(id, {
        resolve,
        reject: () => {},
        timer,
      });
      ws.send(JSON.stringify(request));
    });
  }

  /**
   * Send multiple commands in a single WebSocket message for efficiency.
   * The Bridge processes them sequentially and returns all results at once.
   * @param commands - Array of command/params pairs to execute as a batch
   * @param timeoutMs - Timeout for the entire batch (default 30s)
   */
  async sendBatch(
    commands: Array<{ command: string; params?: Record<string, unknown> }>,
    timeoutMs = 30000
  ): Promise<BridgeResponse> {
    if (!this.ws || !this.connected) {
      try {
        await this.connect();
      } catch {
        return {
          id: "",
          success: false,
          error: "Not connected to s&box Bridge.",
        };
      }
    }

    if (!this.ws) {
      return { id: "", success: false, error: "Connection failed" };
    }

    const id = `batch_${++this.requestCounter}_${Date.now()}`;
    const request = { id, commands };
    const ws = this.ws;

    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        this.pendingRequests.delete(id);
        resolve({
          id,
          success: false,
          error: `Batch request timed out after ${timeoutMs}ms`,
        });
      }, timeoutMs);

      this.pendingRequests.set(id, {
        resolve,
        reject: () => {},
        timer,
      });
      ws.send(JSON.stringify(request));
    });
  }

  /**
   * Send a WebSocket ping and measure round-trip latency.
   * @returns Latency in milliseconds, or -1 if not connected or ping times out (5s)
   */
  async ping(): Promise<number> {
    if (!this.ws || !this.connected) return -1;

    const ws = this.ws;
    const start = Date.now();
    return new Promise((resolve) => {
      const onPong = () => {
        clearTimeout(timeout);
        resolve(Date.now() - start);
      };
      const timeout = setTimeout(() => {
        ws.removeListener("pong", onPong);
        resolve(-1);
      }, 5000);
      ws.once("pong", onPong);
      ws.ping();
    });
  }

  /** Whether the WebSocket is currently open and connected. */
  isConnected(): boolean {
    return this.connected;
  }

  /** The configured Bridge host address. */
  getHost(): string {
    return this.host;
  }

  /** The configured Bridge port number. */
  getPort(): number {
    return this.port;
  }

  /** Timestamp (ms since epoch) of the last pong received, or 0 if never connected. */
  getLastPongTime(): number {
    return this.lastPongTime;
  }

  /**
   * Cleanly shut down the connection: cancel any pending reconnect,
   * stop the ping loop, reject all in-flight requests, and close the socket.
   */
  disconnect(): void {
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.stopPingLoop();
    this.rejectAllPending("Client disconnecting");
    if (this.ws) {
      this.ws.close();
      this.ws = null;
    }
    this.connected = false;
  }

  /** Start the periodic ping keepalive. Terminates the socket after MAX_MISSED_PINGS unanswered pings. */
  private startPingLoop(): void {
    this.stopPingLoop();
    this.pingTimer = setInterval(() => {
      if (!this.ws || !this.connected) {
        this.stopPingLoop();
        return;
      }

      this.missedPings++;
      if (this.missedPings >= BridgeClient.MAX_MISSED_PINGS) {
        console.error(
          `[sbox-mcp] ${BridgeClient.MAX_MISSED_PINGS} pings unanswered — closing connection`
        );
        this.stopPingLoop();
        this.ws.terminate();
        return;
      }

      this.ws.ping();
    }, BridgeClient.PING_INTERVAL_MS);
  }

  /** Stop the ping interval timer. */
  private stopPingLoop(): void {
    if (this.pingTimer) {
      clearInterval(this.pingTimer);
      this.pingTimer = null;
    }
  }

  /** Schedule a reconnection attempt after RECONNECT_DELAY_MS. No-op if already scheduled. */
  private scheduleReconnect(): void {
    if (this.reconnectTimer) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.connect().catch(() => {
        // Will retry on next send()
      });
    }, BridgeClient.RECONNECT_DELAY_MS);
  }

  /** Resolve all pending requests with a failure response and clear the map. */
  private rejectAllPending(reason: string): void {
    for (const [id, pending] of this.pendingRequests) {
      clearTimeout(pending.timer);
      pending.resolve({ id, success: false, error: reason });
    }
    this.pendingRequests.clear();
  }
}
