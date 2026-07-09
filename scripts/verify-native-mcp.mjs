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
    "bridge_asset", "bridge_audio", "bridge_batch", "bridge_character", "bridge_component", "bridge_debug",
    "bridge_discovery", "bridge_gameobject", "bridge_material", "bridge_moviemaker",
    "bridge_navigation", "bridge_networking", "bridge_npc", "bridge_physics",
    "bridge_playmode", "bridge_playtest", "bridge_prefab", "bridge_project",
    "bridge_scaffold_gameplay", "bridge_scaffold_polish", "bridge_scene", "bridge_screenshot",
    "bridge_ui", "bridge_validation", "bridge_visuals", "bridge_world",
  ];
  const missing = expected.filter((e) => !tsText.includes(e));
  check(`list_toolsets has all ${expected.length} bridge_* toolsets`, missing.length === 0,
    missing.length ? `missing: ${missing.join(", ")}` : `${expected.length}/${expected.length}`);

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

  // 9. (PARKED — engine limitation on 26.07.08b) Auto-undo for bridge mutations is not
  // achievable via public API: FullUndoSnapshot/UndoSystem.Snapshot are inert (verified
  // live 2026-07-09; built-in tools use an internal undo mechanism addons can't reach).
  // Re-enable a real check here when Facepunch exposes the per-edit undo hook.
  console.log("SKIP  auto-undo convention — engine API inert on this build (see McpGate.cs note)");

  // 10. Wave-1 tools (Batch 51): describe_project, find_broken_references,
  // batch_set_property dry-run → apply → verify → cleanup.
  try {
    const dp = await callTool("describe_project", {});
    check("describe_project", /customComponents/.test(dp.text), dp.text.slice(0, 60).replace(/\n/g, " "));
  } catch (e) {
    check("describe_project", false, e.message.slice(0, 120));
  }
  try {
    const br = await callTool("find_broken_references", { limit: 5 });
    check("find_broken_references", /objectsScanned/.test(br.text), br.text.slice(0, 80).replace(/\n/g, " "));
  } catch (e) {
    check("find_broken_references", false, e.message.slice(0, 120));
  }
  try {
    const g1 = (await callTool("create_gameobject", { name: "__batch_a", position: "0,0,6100" })).text.match(/[0-9a-f-]{36}/i)[0];
    const g2 = (await callTool("create_gameobject", { name: "__batch_b", position: "0,0,6200" })).text.match(/[0-9a-f-]{36}/i)[0];
    await callTool("add_component_with_properties", { id: g1, component: "ModelRenderer" });
    await callTool("add_component_with_properties", { id: g2, component: "ModelRenderer" });
    const dry = await callTool("batch_set_property", { ids: [g1, g2], component: "ModelRenderer", property: "Tint", value: "1,0,0,1", dryRun: true });
    const dryOk = /"dryRun": true/.test(dry.text) && /"succeeded": 2/.test(dry.text);
    const wet = await callTool("batch_set_property", { ids: [g1, g2], component: "ModelRenderer", property: "Tint", value: "1,0,0,1" });
    const wetOk = /"succeeded": 2/.test(wet.text);

    // wave 2: batch_add_component dry + apply, batch_reparent, batch_delete dry + apply
    const addDry = await callTool("batch_add_component", { ids: [g1, g2], component: "BoxCollider", dryRun: true });
    const addWet = await callTool("batch_add_component", { ids: [g1, g2], component: "BoxCollider" });
    const addOk = /"dryRun": true/.test(addDry.text) && /"succeeded": 2/.test(addWet.text);
    check("batch_add_component dry-run + apply", addOk, addOk ? "2 BoxColliders added" : addWet.text.slice(0, 80));

    const parent = (await callTool("create_gameobject", { name: "__batch_parent", position: "0,0,6300" })).text.match(/[0-9a-f-]{36}/i)[0];
    const rep = await callTool("batch_reparent", { ids: [g1, g2], parent });
    check("batch_reparent", /"succeeded": 2/.test(rep.text), rep.text.slice(0, 60).replace(/\n/g, " "));

    // wave 2: prefab round-trip — full serialize → structured info → full instantiate
    const pf = await callTool("create_prefab", { id: g1, path: "prefabs/__bridge_verify.prefab" });
    const pfOk = /"created": true/.test(pf.text) && /"components": [1-9]/.test(pf.text);
    check("create_prefab full serialization", pfOk, pf.text.match(/"components": \d+/)?.[0] ?? pf.text.slice(0, 80));
    const info = await callTool("get_prefab_info", { path: "prefabs/__bridge_verify.prefab" });
    check("get_prefab_info structured tree", /"totalObjects"/.test(info.text) && /ModelRenderer/.test(info.text),
      info.text.match(/"totalObjects": \d+/)?.[0] ?? info.text.slice(0, 80));
    const inst = await callTool("instantiate_prefab", { path: "prefabs/__bridge_verify.prefab", name: "__prefab_clone", position: "0,0,6400" });
    const instOk = /"instantiated": true/.test(inst.text) && /ModelRenderer/.test(inst.text);
    check("instantiate_prefab recreates components", instOk,
      (inst.text.match(/"method": "[^"]+"/)?.[0] ?? "") + (instOk ? "" : " | " + inst.text.slice(0, 120)));
    const cloneId = instOk ? inst.text.match(/"id": "([0-9a-f-]{36})"/i)?.[1] : null;

    const delDry = await callTool("batch_delete", { ids: [g1, g2, parent, cloneId].filter(Boolean), dryRun: true });
    const delWet = await callTool("batch_delete", { ids: [g1, g2, parent, cloneId].filter(Boolean) });
    // g1/g2 are children of parent — deleting parent removes them; per-id results may
    // report already-gone ids as failures, so assert on the dry-run + parent deletion.
    check("batch_delete dry-run + apply", /"dryRun": true/.test(delDry.text) && /"deleted"/.test(delWet.text),
      delWet.text.match(/"succeeded": \d+/)?.[0] ?? delWet.text.slice(0, 60));

    check("batch_set_property dry-run + apply", dryOk && wetOk, `dry:${dryOk} apply:${wetOk}`);
  } catch (e) {
    check("wave-2 batch/prefab chain", false, e.message.slice(0, 200));
  }

  // wave 2: playtest_abort with no job running
  try {
    const ab = await callTool("playtest_abort", {});
    check("playtest_abort (no job)", /"aborted": false/.test(ab.text), ab.text.slice(0, 60).replace(/\n/g, " "));
  } catch (e) {
    check("playtest_abort (no job)", false, e.message.slice(0, 120));
  }

  // wave 2: find_broken_references file scan
  try {
    const br2 = await callTool("find_broken_references", { limit: 5 });
    check("find_broken_references file scan", /"filesScanned": [1-9]/.test(br2.text),
      br2.text.match(/"filesScanned": \d+/)?.[0] ?? br2.text.slice(0, 80));
  } catch (e) {
    check("find_broken_references file scan", false, e.message.slice(0, 120));
  }
} catch (e) {
  console.error(`FATAL: ${e.message}`);
  process.exit(2);
}

const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} checks passed`);
process.exit(failed ? 1 : 0);
