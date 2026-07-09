#!/usr/bin/env node
/**
 * verify-native-mcp.mjs — Phase 1 verify-gate against the LIVE native MCP server.
 *
 * Talks streamable-HTTP JSON-RPC to http://127.0.0.1:7269/mcp (editor must be open):
 *   1. initialize handshake
 *   2. list_toolsets → all bridge_* toolsets present
 *   3. search_tools → finds a bridge tool by natural terms
 *   4. spot-run read-only tools across families (incl. an int? nullable-binding check)
 *   5. mutating round-trip: create_gameobject → delete_gameobject
 *   6. take_screenshot → returns an inline image content block
 *
 * Usage: node scripts/verify-native-mcp.mjs [--port 7269]
 */

const port = process.argv.includes("--port")
  ? process.argv[process.argv.indexOf("--port") + 1]
  : "7269";
const URL = `http://127.0.0.1:${port}/mcp`;

let sessionId = null;
let nextId = 1;

async function rpc(method, params) {
  const headers = {
    "content-type": "application/json",
    accept: "application/json, text/event-stream",
  };
  if (sessionId) headers["mcp-session-id"] = sessionId;
  const res = await fetch(URL, {
    method: "POST",
    headers,
    body: JSON.stringify({ jsonrpc: "2.0", id: nextId++, method, params }),
  });
  if (res.headers.get("mcp-session-id")) sessionId = res.headers.get("mcp-session-id");
  const text = await res.text();
  // Streamable HTTP may answer as SSE — take the last data: line.
  const dataLines = text.split("\n").filter((l) => l.startsWith("data:"));
  const payload = dataLines.length ? dataLines[dataLines.length - 1].slice(5) : text;
  const msg = JSON.parse(payload);
  if (msg.error) throw new Error(`${method}: ${JSON.stringify(msg.error)}`);
  return msg.result;
}

async function notify(method, params) {
  const headers = {
    "content-type": "application/json",
    accept: "application/json, text/event-stream",
  };
  if (sessionId) headers["mcp-session-id"] = sessionId;
  await fetch(URL, {
    method: "POST",
    headers,
    body: JSON.stringify({ jsonrpc: "2.0", method, params }),
  });
}

/** call_tool through the native entry point; returns { text, structured, images }. */
async function callTool(name, args) {
  const r = await rpc("tools/call", {
    name: "call_tool",
    arguments: { name, arguments: args ?? {} },
  });
  const text = (r.content ?? []).filter((c) => c.type === "text").map((c) => c.text).join("\n");
  const images = (r.content ?? []).filter((c) => c.type === "image");
  if (r.isError) throw new Error(`call_tool ${name} → ${text}`);
  return { text, images, raw: r };
}

const results = [];
const check = (label, ok, detail = "") => {
  results.push({ label, ok, detail });
  console.log(`${ok ? "PASS" : "FAIL"}  ${label}${detail ? " — " + detail : ""}`);
};

// ── run ────────────────────────────────────────────────────────────

try {
  const init = await rpc("initialize", {
    protocolVersion: "2025-03-26",
    capabilities: {},
    clientInfo: { name: "verify-native-mcp", version: "1.0" },
  });
  await notify("notifications/initialized");
  check("initialize", !!init.serverInfo, init.serverInfo?.name ?? "");

  // 2. toolsets
  const ts = await rpc("tools/call", { name: "list_toolsets", arguments: {} });
  const tsText = (ts.content ?? []).map((c) => c.text).join("\n");
  const expected = [
    "bridge_asset", "bridge_audio", "bridge_character", "bridge_component", "bridge_debug",
    "bridge_discovery", "bridge_gameobject", "bridge_material", "bridge_moviemaker",
    "bridge_navigation", "bridge_networking", "bridge_npc", "bridge_physics",
    "bridge_playmode", "bridge_playtest", "bridge_prefab", "bridge_project",
    "bridge_scaffold_gameplay", "bridge_scaffold_polish", "bridge_scene", "bridge_screenshot",
    "bridge_ui", "bridge_validation", "bridge_visuals", "bridge_world",
  ];
  const missing = expected.filter((e) => !tsText.includes(e));
  check("list_toolsets has all 25 bridge_* toolsets", missing.length === 0,
    missing.length ? `missing: ${missing.join(", ")}` : "25/25");

  // 3. search
  const st = await rpc("tools/call", {
    name: "search_tools",
    arguments: { query: "create gameobject" },
  });
  const stText = (st.content ?? []).map((c) => c.text).join("\n");
  check("search_tools finds create_gameobject", stText.includes("create_gameobject"));

  // 4. read-only spot-runs
  const spots = [
    ["get_bridge_status", {}],
    ["get_project_info", {}],
    ["is_playing", {}],
    ["get_scene_hierarchy", { maxDepth: 1 }],
    ["list_prefabs", {}],
    ["describe_type", { name: "ModelRenderer" }],
    ["validate_project", {}],
  ];
  for (const [name, args] of spots) {
    try {
      const r = await callTool(name, args);
      check(`call ${name}`, r.text.length > 0, r.text.slice(0, 60).replace(/\n/g, " "));
    } catch (e) {
      check(`call ${name}`, false, e.message.slice(0, 120));
    }
  }

  // 4b. nullable-binding: find_objects has int? limit — pass it AND omit it.
  try {
    const omit = await callTool("find_objects", { name: "a" });
    check("find_objects (int? limit OMITTED)", omit.text.length > 0);
    const given = await callTool("find_objects", { name: "a", limit: 3 });
    check("find_objects (int? limit = 3)", given.text.length > 0);
  } catch (e) {
    check("find_objects nullable binding", false, e.message.slice(0, 160));
  }

  // 5. mutating round-trip
  try {
    const created = await callTool("create_gameobject", {
      name: "__mcp_verify", position: "0,0,5000",
    });
    const idMatch = created.text.match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i);
    check("create_gameobject returns GUID", !!idMatch, idMatch?.[0] ?? created.text.slice(0, 80));
    if (idMatch) {
      const del = await callTool("delete_gameobject", { id: idMatch[0] });
      check("delete_gameobject round-trip", true, del.text.slice(0, 60).replace(/\n/g, " "));
    }
  } catch (e) {
    check("mutating round-trip", false, e.message.slice(0, 160));
  }

  // 6. inline image
  try {
    const shot = await callTool("take_screenshot", { width: 320, height: 180 });
    check("take_screenshot returns inline image", shot.images.length > 0,
      shot.images.length ? `${shot.images[0].data.length} b64 chars, ${shot.images[0].mimeType}` : shot.text.slice(0, 120));
  } catch (e) {
    check("take_screenshot inline image", false, e.message.slice(0, 160));
  }

  // 7. error semantics: unknown GUID should be a readable tool error
  try {
    await callTool("delete_gameobject", { id: "00000000-0000-0000-0000-000000000000" });
    check("error semantics (bad GUID throws)", false, "no error raised");
  } catch (e) {
    check("error semantics (bad GUID throws)", /not found|error/i.test(e.message), e.message.slice(0, 100));
  }

  // 8. create_sound_event accepts `path` (v2 fix): aiming at an EXISTING .sound must
  // produce the "already exists" error — the pre-fix handler would fail on a missing
  // `name` property instead. No file is written either way.
  try {
    await callTool("create_sound_event", { path: "Assets/audio/fx/dig.sound" });
    check("create_sound_event path param honored", false, "expected already-exists error");
  } catch (e) {
    check("create_sound_event path param honored", /already exists/i.test(e.message), e.message.slice(0, 100));
  }

  // 9. undo convention: a scene-mutating bridge tool pushes an undo step — the built-in
  // `undo` must remove the object the bridge created.
  try {
    const created = await callTool("create_gameobject", { name: "__undo_probe", position: "0,0,6000" });
    const guid = created.text.match(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i)?.[0];
    if (!guid) throw new Error("no GUID from create_gameobject");
    const undo = await rpc("tools/call", { name: "call_tool", arguments: { name: "undo", arguments: {} } });
    const undoText = (undo.content ?? []).map((c) => c.text).join(" ");
    let gone = false;
    try {
      await callTool("delete_gameobject", { id: guid });
      // delete succeeded → object still existed → undo did NOT remove it (cleaned up though)
    } catch {
      gone = true;
    }
    check("scene mutation pushes undo step (built-in undo removes it)", gone,
      gone ? "object gone after undo" : `object survived undo (${undoText.slice(0, 60)})`);
  } catch (e) {
    check("scene mutation pushes undo step", false, e.message.slice(0, 120));
  }
} catch (e) {
  console.error(`FATAL: ${e.message}`);
  process.exit(2);
}

const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} checks passed`);
process.exit(failed ? 1 : 0);
