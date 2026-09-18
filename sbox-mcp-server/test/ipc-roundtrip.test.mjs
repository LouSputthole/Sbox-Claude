// Integration tests for the file-IPC round-trip (GitHub issue #21, item 1).
//
// A FAKE EDITOR runs in-process: it publishes status.json like the addon does
// and answers req_*.json files the way the C# poller does after issue #20 —
// response written temp+rename FIRST, request file deleted AFTER. No s&box
// needed; everything runs against a scratch temp dir.

import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

import * as bc from "../dist/transport/bridge-client.js";

function scratchDir() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sbox-ipc-test-"));
  return dir;
}

function writeStatus(dir, extra = {}) {
  const now = new Date().toISOString();
  fs.writeFileSync(
    path.join(dir, "status.json"),
    JSON.stringify({ running: true, version: "test", heartbeat: now, processHeartbeat: now, ...extra })
  );
}

/** Minimal stand-in for the addon's poller + main thread. */
function fakeEditor(dir, handler) {
  const seen = new Set();
  const timer = setInterval(() => {
    for (const f of fs.readdirSync(dir)) {
      if (!f.startsWith("req_") || !f.endsWith(".json") || seen.has(f)) continue;
      seen.add(f);
      const reqPath = path.join(dir, f);
      const req = JSON.parse(fs.readFileSync(reqPath, "utf8"));
      // Claim it exactly like the addon: rename to the .processing sentinel.
      const processing = reqPath + bc.PROCESSING_SUFFIX;
      fs.renameSync(reqPath, processing);
      const out = handler(req);
      if (out === undefined) continue; // simulate "picked up, never answers"
      const resPath = path.join(dir, `res_${req.id}.json`);
      const body = typeof out === "string" ? out : JSON.stringify({ id: req.id, ...out });
      fs.writeFileSync(resPath + ".tmp", body, "utf8");
      fs.renameSync(resPath + ".tmp", resPath);
      // Response is on disk — only NOW remove the sentinel (issue #20 ordering).
      fs.unlinkSync(processing);
    }
  }, 10);
  return () => clearInterval(timer);
}

let dir;
let logs = [];
before(() => {
  dir = scratchDir();
  process.env.SBOX_BRIDGE_IPC_DIR = dir;
  bc.setIpcLogger((m) => logs.push(m));
});
after(() => {
  bc.setIpcLogger(null);
  delete process.env.SBOX_BRIDGE_IPC_DIR;
  fs.rmSync(dir, { recursive: true, force: true });
});

test("round-trip: request is written atomically, carries protocolVersion, and the response comes back", async () => {
  writeStatus(dir);
  let captured = null;
  const stop = fakeEditor(dir, (req) => {
    captured = req;
    return { success: true, data: { echoed: req.params } };
  });
  try {
    const client = new bc.BridgeClient();
    const res = await client.send("echo", { hello: "world" }, 2000);
    assert.equal(res.success, true);
    assert.deepEqual(res.data, { echoed: { hello: "world" } });
    assert.equal(captured.command, "echo");
    assert.equal(captured.protocolVersion, bc.IPC_PROTOCOL_VERSION, "every request must stamp the wire-contract version");
    // Nothing left behind: no req/res/tmp files.
    const leftovers = fs.readdirSync(dir).filter((f) => f !== "status.json");
    assert.deepEqual(leftovers, []);
  } finally {
    stop();
  }
});

test("batch round-trip uses the same envelope", async () => {
  writeStatus(dir);
  const stop = fakeEditor(dir, (req) => ({ success: true, data: { n: req.commands.length } }));
  try {
    const client = new bc.BridgeClient();
    const res = await client.sendBatch([{ command: "a" }, { command: "b" }], 2000);
    assert.equal(res.success, true);
    assert.deepEqual(res.data, { n: 2 });
  } finally {
    stop();
  }
});

test("timeout with a live process but stalled main thread names the modal-dialog cause (issue #14)", async () => {
  // Main-thread heartbeat 30 s stale, poll-thread heartbeat fresh, addon says why.
  const stale = new Date(Date.now() - 30_000).toISOString();
  writeStatus(dir, { heartbeat: stale, blockedBy: "main thread stalled for 30 s — a modal editor dialog" });
  const client = new bc.BridgeClient();
  // connect() must refuse (stale main-thread heartbeat) and explain.
  const res = await client.send("anything", {}, 300);
  assert.equal(res.success, false);
  assert.match(res.error, /main thread is blocked/i);
  assert.match(res.error, /modal/i);
});

test("timeout diagnostics: process alive + main thread stalled AFTER connecting", async () => {
  writeStatus(dir);
  const client = new bc.BridgeClient();
  await client.connect();
  // Now the editor 'hangs': main heartbeat stops, poll thread keeps beating, nobody answers.
  const stale = new Date(Date.now() - 20_000).toISOString();
  writeStatus(dir, { heartbeat: stale, blockedBy: "main thread stalled for 20 s" });
  const res = await client.send("hang", {}, 250);
  assert.equal(res.success, false);
  assert.match(res.error, /PROCESS is alive/);
  assert.match(res.error, /main thread stalled for 20 s/);
  // The timed-out request file must be cleaned up by the client.
  assert.equal(fs.readdirSync(dir).some((f) => f.startsWith("req_")), false);
});

test("timeout while the editor is STILL inside the handler names that, not 'never picked up'", async () => {
  writeStatus(dir);
  const stop = fakeEditor(dir, () => undefined); // claims the file, never answers
  try {
    const client = new bc.BridgeClient();
    const res = await client.send("slow", {}, 300);
    assert.equal(res.success, false);
    assert.match(res.error, /STILL inside the handler/);
    assert.doesNotMatch(res.error, /never picked up/);
    // The sentinel belongs to the editor: the client must not delete it.
    assert.ok(fs.readdirSync(dir).some((f) => f.endsWith(bc.PROCESSING_SUFFIX)));
  } finally {
    stop();
    for (const f of fs.readdirSync(dir)) if (f.endsWith(bc.PROCESSING_SUFFIX)) fs.unlinkSync(path.join(dir, f));
  }
});

test("an unparseable response fails LOUDLY well before the timeout (issue #18)", async () => {
  writeStatus(dir);
  logs = [];
  const stop = fakeEditor(dir, () => "﻿{not json at all");
  try {
    const client = new bc.BridgeClient();
    const t0 = Date.now();
    const res = await client.send("broken", {}, 10_000);
    const elapsed = Date.now() - t0;
    assert.equal(res.success, false);
    assert.match(res.error, /could not be parsed as JSON/);
    assert.match(res.error, /First 200 chars/);
    assert.ok(elapsed < 5_000, `should fail fast, took ${elapsed}ms`);
    assert.ok(logs.some((m) => /unparseable response/.test(m)), "must log the parse failure to the diagnostics sink");
    assert.equal(fs.readdirSync(dir).some((f) => f.startsWith("res_")), false, "bad response file is cleaned up");
  } finally {
    stop();
  }
});

test("malformed status.json is reported, not silently treated as 'no editor'", () => {
  logs = [];
  fs.writeFileSync(path.join(dir, "status.json"), "{ this is not json");
  const client = new bc.BridgeClient();
  const s = client.readStatus();
  assert.equal(s.running, false);
  assert.ok(logs.some((m) => /malformed/.test(m)), "expected a 'malformed status.json' diagnostic");
  // ...and only once — repeats are collapsed.
  client.readStatus();
  assert.equal(logs.filter((m) => /malformed/.test(m)).length, 1);
});

test("classifyStatus surfaces processHeartbeat, blockedBy and protocolVersion", () => {
  const now = 1_000_000;
  const r = bc.classifyStatus(
    {
      running: true,
      heartbeat: new Date(now - 9_000).toISOString(),
      processHeartbeat: new Date(now - 400).toISOString(),
      blockedBy: "main thread stalled for 9 s",
      protocolVersion: 1,
    },
    now,
    bc.STATUS_STALE_MS
  );
  assert.equal(r.fresh, false, "main-thread heartbeat is stale → not connected");
  assert.equal(r.processHeartbeatMs, 400);
  assert.equal(r.blockedBy, "main thread stalled for 9 s");
  assert.equal(r.protocolVersion, 1);
});
