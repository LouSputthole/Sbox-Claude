#!/usr/bin/env node
/**
 * audit-mcp-quality.mjs — quality gate for the native-MCP tool surface.
 *
 * Lints scripts/tools-manifest.json (and the emitter's exclusion/read-only config)
 * for the things that make tools unusable to agents:
 *   - name collisions with the native server's built-in tools (SILENT tool loss)
 *   - toolset name collisions with built-in toolsets
 *   - vague/short tool descriptions (the description IS the API)
 *   - descriptions with no return-value or next-step guidance
 *   - params with missing/short descriptions
 *   - list-shaped tools that don't mention truncation/limits
 *
 * Exit code 1 on ERROR-severity findings (collisions), 0 otherwise (warnings listed).
 * Usage: node scripts/audit-mcp-quality.mjs [--verbose]
 */

import { readFileSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const verbose = process.argv.includes("--verbose");

const manifest = JSON.parse(
  readFileSync(join(__dirname, "tools-manifest.json"), "utf-8")
);

// Built-in inventory, Phase 0 live sweep (engine 26.07.08b). Re-inventory each
// engine update: list_toolsets + search_tools "" against http://127.0.0.1:7269/mcp.
const BUILTIN_TOOLS = new Set([
  // asset
  "asset_compile", "asset_dependencies", "asset_files", "asset_find_by_file",
  "asset_info", "asset_read", "asset_search", "asset_thumbnail", "asset_types",
  "asset_write", "create_asset",
  // component
  "get_component_type",
  // editor
  "call_tool", "call_tools", "compile_status", "console_command", "describe_toolset",
  "editor_status", "list_toolsets", "read_console", "search_tools",
  // log
  "log_error", "log_info", "log_warning",
  // package
  "find_packages", "get_package", "install_package",
  // play
  "play_pause", "play_start", "play_stop",
  // scene
  "add_component", "camera_screenshot", "create_game_object", "delete_game_object",
  "editor_camera_screenshot", "find_game_objects", "get_editor_camera", "get_game_object",
  "get_selection", "list_scenes", "redo", "remove_component", "save_scene", "scene_trace",
  "scene_tree", "set_component", "set_editor_camera", "set_game_object", "set_selection",
  "spawn_model", "spawn_models", "undo",
]);
const BUILTIN_TOOLSETS = new Set([
  "asset", "component", "editor", "log", "package", "play", "scene",
]);

// Tools we deliberately DON'T ship natively (must match emit-mcp-wrappers.mjs).
const EXPECTED_DROPPED = new Set([
  "spawn_model", "list_scenes", "save_scene", "undo", "redo", "remove_component",
]);
const SERVER_SIDE = new Set([
  "read_log", "get_compile_errors", "execute_csharp", "search_docs",
  "get_doc_page", "list_doc_categories", "run_self_test",
]);

const errors = [];
const warnings = [];

// Words that signal "what do I do next" guidance in a description.
const NEXT_STEP_RX = /returns|pass |use |follow|guid|path|id[s)\s,.]|read |then /i;
const LIST_SHAPED_RX = /^(list_|search_|find_)/;

const seen = new Set();
for (const t of manifest.tools) {
  if (seen.has(t.name)) continue;
  seen.add(t.name);
  if (SERVER_SIDE.has(t.name)) continue;

  // 1. built-in collisions
  if (BUILTIN_TOOLS.has(t.name) && !EXPECTED_DROPPED.has(t.name)) {
    errors.push(`COLLISION: '${t.name}' collides with a native built-in and is not in the drop list — it will be SILENTLY SKIPPED`);
  }

  // 2. vague descriptions
  const desc = (t.description ?? "").trim();
  if (desc.length < 40)
    warnings.push(`vague-desc: ${t.name} — description is ${desc.length} chars: "${desc}"`);

  // 3. next-step guidance
  if (!NEXT_STEP_RX.test(desc))
    warnings.push(`no-next-step: ${t.name} — description names no return value / follow-up tool`);

  // 4. param docs
  for (const p of t.params) {
    const pd = (p.description ?? "").trim();
    if (pd.length === 0)
      warnings.push(`no-param-doc: ${t.name}.${p.name}`);
    else if (pd.length < 8)
      warnings.push(`short-param-doc: ${t.name}.${p.name} — "${pd}"`);
  }

  // 5. list tools should mention limits/truncation
  if (LIST_SHAPED_RX.test(t.name) && !/limit|max|first \d|top \d|truncat|all /i.test(desc + t.params.map((p) => p.description).join(" ")))
    warnings.push(`no-limit-note: ${t.name} — list-shaped tool with no limit/truncation note`);
}

// 6. toolset collisions (read from the emitter's mapping)
const emitterSrc = readFileSync(join(__dirname, "emit-mcp-wrappers.mjs"), "utf-8");
for (const m of emitterSrc.matchAll(/"(bridge_\w+)"/g)) {
  if (BUILTIN_TOOLSETS.has(m[1]))
    errors.push(`TOOLSET COLLISION: '${m[1]}' collides with a built-in toolset`);
}

// ── report ─────────────────────────────────────────────────────────

const byCat = {};
for (const w of warnings) {
  const cat = w.split(":")[0];
  byCat[cat] = (byCat[cat] ?? 0) + 1;
}

console.log(`audited ${seen.size} tools`);
console.log(`errors: ${errors.length}`);
for (const e of errors) console.log(`  ERROR ${e}`);
console.log(`warnings: ${warnings.length}`);
for (const [cat, n] of Object.entries(byCat)) console.log(`  ${cat}: ${n}`);
if (verbose) for (const w of warnings) console.log(`  WARN ${w}`);

process.exit(errors.length ? 1 : 0);
