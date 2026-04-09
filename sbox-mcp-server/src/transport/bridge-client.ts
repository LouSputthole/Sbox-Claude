import WebSocket from "ws";

export interface BridgeRequest {
  id: string;
  command: string;
  params: Record<string, unknown>;
}

export interface BridgeResponse {
  id: string;
  success: boolean;
  data?: unknown;
  error?: string;
}

/**
 * WebSocket client that connects to the s&box Bridge Addon.
 * The Bridge runs inside the s&box editor on port 29015.
 *
 * Features:
 * - Auto-reconnect on disconnect (3s delay)
 * - Per-request timeouts (default 30s)
 * - Ping/pong keepalive every 15s, disconnects after 3 missed pings
 * - Graceful rejection of in-flight requests on connection loss
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

  constructor(host = "127.0.0.1", port = 29015) {
    this.host = host;
    this.port = port;
    this.url = `ws://${host}:${port}`;
  }

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
          }
        });
      } catch (err) {
        reject(err);
      }
    });
  }

  async send(
    command: string,
    params: Record<string, unknown> = {},
    timeoutMs = 30000
  ): Promise<BridgeResponse> {
    if (!this.ws || !this.connected) {
      // Try to reconnect once
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

    const id = `req_${++this.requestCounter}_${Date.now()}`;
    const request: BridgeRequest = { id, command, params };

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
        reject: () => {}, // Errors resolve as failed responses
        timer,
      });
      this.ws!.send(JSON.stringify(request));
    });
  }

  /**
   * Send a ping and measure round-trip latency in ms.
   * Returns -1 if not connected.
   */
  async ping(): Promise<number> {
    if (!this.ws || !this.connected) return -1;

    const start = Date.now();
    return new Promise((resolve) => {
      const timeout = setTimeout(() => resolve(-1), 5000);
      this.ws!.once("pong", () => {
        clearTimeout(timeout);
        resolve(Date.now() - start);
      });
      this.ws!.ping();
    });
  }

  isConnected(): boolean {
    return this.connected;
  }

  getHost(): string {
    return this.host;
  }

  getPort(): number {
    return this.port;
  }

  getLastPongTime(): number {
    return this.lastPongTime;
  }

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

  private startPingLoop(): void {
    this.stopPingLoop();
    this.pingTimer = setInterval(() => {
      if (!this.ws || !this.connected) {
        this.stopPingLoop();
        return;
      }

      this.missedPings++;
      if (this.missedPings > BridgeClient.MAX_MISSED_PINGS) {
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

  private stopPingLoop(): void {
    if (this.pingTimer) {
      clearInterval(this.pingTimer);
      this.pingTimer = null;
    }
  }

  private scheduleReconnect(): void {
    if (this.reconnectTimer) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      this.connect().catch(() => {
        // Will retry on next send()
      });
    }, BridgeClient.RECONNECT_DELAY_MS);
  }

  private rejectAllPending(reason: string): void {
    for (const [id, pending] of this.pendingRequests) {
      clearTimeout(pending.timer);
      pending.resolve({ id, success: false, error: reason });
    }
    this.pendingRequests.clear();
  }
}
