#!/usr/bin/env node
/**
 * audit-parity.mjs — CI parity audit for the s&box Claude Bridge.
 *
 * Checks:
 *   1. No duplicate server.tool() names in TS sources.
 *   2. No duplicate Register() handler names in MyEditorMenu.cs.
 *   3. Every bridge.send() command has a matching C# handler
 *      (allowlist: "get_bridge_status" — special-cased in dispatcher, not via Register).
 *   4. Every C# handler is referenced by at least one bridge.send().
 *   5. Version lock: package.json, plugin.json, BridgeVersion const, CHANGELOG.md
 *      first "## [X.Y.Z]" heading, marketplace.json plugins[0].version, and the
 *      plugin .mcp.json npm pin must all match. (marketplace.json drifted to
 *      v1.16.0 unnoticed for two releases before it joined this set.)
 *
 * Exit 0 on full pass, exit 1 on any failure.
 * Zero npm dependencies — Node built-ins only.
 */

import { readFileSync, readdirSync } from "fs";
import { join, dirname } from "path";
import { fileURLToPath } from "url";

// ---------------------------------------------------------------------------
// Paths
// ---------------------------------------------------------------------------
const __dir = dirname(fileURLToPath(import.meta.url));
const ROOT = join(__dir, "..");

const TS_TOOLS_DIR   = join(ROOT, "sbox-mcp-server", "src", "tools");
const CS_FILE        = join(ROOT, "sbox-bridge-addon", "Editor", "MyEditorMenu.cs");
const PKG_JSON       = join(ROOT, "sbox-mcp-server", "package.json");
const PLUGIN_JSON    = join(ROOT, "plugins", "sbox-claude", ".claude-plugin", "plugin.json");
const CHANGELOG_MD   = join(ROOT, "CHANGELOG.md");
const MARKETPLACE_JSON = join(ROOT, ".claude-plugin", "marketplace.json");
const MCP_JSON       = join(ROOT, "plugins", "sbox-claude", ".mcp.json");

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
/** Read a file as UTF-8, stripping BOM if present. */
function readFile(p) {
  return readFileSync(p, "utf8").replace(/^﻿/, "");
}

/** Collect all matches of a regex (with capturing group 1) from a string. */
function allMatches(re, str) {
  const results = [];
  let m;
  // Always create a fresh RegExp with the g flag to avoid state leakage.
  const g = new RegExp(re.source, re.flags.includes("g") ? re.flags : re.flags + "g");
  while ((m = g.exec(str)) !== null) {
    results.push(m[1]);
  }
  return results;
}

/** Return entries that appear more than once in an array. */
function duplicates(arr) {
  const seen = new Map();
  for (const v of arr) seen.set(v, (seen.get(v) ?? 0) + 1);
  return [...seen.entries()].filter(([, n]) => n > 1).map(([v]) => v);
}

// ---------------------------------------------------------------------------
// 1. Collect tool names and bridge.send targets from all .ts files
// ---------------------------------------------------------------------------
const tsFiles = readdirSync(TS_TOOLS_DIR)
  .filter(f => f.endsWith(".ts"))
  .map(f => join(TS_TOOLS_DIR, f));

const toolNames  = [];   // every server.tool("name") occurrence
const sendCmds   = [];   // every bridge.send("cmd") occurrence

for (const f of tsFiles) {
  const src = readFile(f);
  toolNames.push(...allMatches(/server\.tool\(\s*"([a-z_0-9]+)"/,  src));
  sendCmds.push( ...allMatches(/bridge\.send\(\s*"([a-z_0-9]+)"/,  src));
}

// ---------------------------------------------------------------------------
// 2. Collect C# handler names from MyEditorMenu.cs
// ---------------------------------------------------------------------------
const csSrc      = readFile(CS_FILE);
const handlerNames = allMatches(/Register\(\s*"([a-z_0-9]+)"/, csSrc);

// ---------------------------------------------------------------------------
// 3. Version lock
// ---------------------------------------------------------------------------
const pkgVersion      = JSON.parse(readFile(PKG_JSON)).version;
const pluginVersion   = JSON.parse(readFile(PLUGIN_JSON)).version;

// BridgeVersion = "X.Y.Z"  (const string in C#)
const bridgeVerMatch  = /BridgeVersion\s*=\s*"([^"]+)"/.exec(csSrc);
const bridgeVersion   = bridgeVerMatch ? bridgeVerMatch[1] : null;

// First "## [X.Y.Z]" heading in CHANGELOG.md. An "[Unreleased]" section on top is
// standard keep-a-changelog practice mid-cycle — skip it and lock on the first
// released version heading.
const changelogMatch = /^##\s*\[(?!Unreleased\b)([^\]]+)\]/m.exec(readFile(CHANGELOG_MD));
const changelogVersion = changelogMatch ? changelogMatch[1] : null;

// marketplace.json plugins[0].version
const marketplaceVersion = JSON.parse(readFile(MARKETPLACE_JSON)).plugins?.[0]?.version ?? null;

// .mcp.json npm pin: "sbox-mcp-server@X.Y.Z"
const mcpPinMatch = /sbox-mcp-server@([\d.]+)/.exec(readFile(MCP_JSON));
const mcpPinVersion = mcpPinMatch ? mcpPinMatch[1] : null;

// ---------------------------------------------------------------------------
// 4. Run checks, collect failures
// ---------------------------------------------------------------------------
const failures = [];

// 4a. Duplicate tool names
const dupTools = duplicates(toolNames);
if (dupTools.length > 0) {
  failures.push(`DUPLICATE tool names (${dupTools.length}): ${dupTools.join(", ")}`);
}

// 4b. Duplicate handler names
const dupHandlers = duplicates(handlerNames);
if (dupHandlers.length > 0) {
  failures.push(`DUPLICATE handler names (${dupHandlers.length}): ${dupHandlers.join(", ")}`);
}

// 4c. bridge.send commands with no matching C# handler
//     Allowlist: "get_bridge_status" is special-cased in the dispatcher (not via Register).
const SEND_ALLOWLIST = new Set(["get_bridge_status"]);
const handlerSet     = new Set(handlerNames);
const sendSet        = new Set(sendCmds);

const unmatchedSends = [...sendSet].filter(
  cmd => !handlerSet.has(cmd) && !SEND_ALLOWLIST.has(cmd)
);
if (unmatchedSends.length > 0) {
  failures.push(
    `bridge.send() commands with no C# handler (${unmatchedSends.length}): ${unmatchedSends.join(", ")}`
  );
}

// 4d. C# handlers never referenced by any bridge.send
const unsentHandlers = [...handlerSet].filter(h => !sendSet.has(h));
if (unsentHandlers.length > 0) {
  failures.push(
    `C# handlers never referenced by bridge.send (${unsentHandlers.length}): ${unsentHandlers.join(", ")}`
  );
}

// 4e. Version lock
const versions = {
  "package.json":     pkgVersion,
  "plugin.json":      pluginVersion,
  "BridgeVersion":    bridgeVersion,
  "CHANGELOG.md":     changelogVersion,
  "marketplace.json": marketplaceVersion,
  ".mcp.json pin":    mcpPinVersion,
};
const versionValues = Object.values(versions).filter(Boolean);
const allSame = versionValues.every(v => v === versionValues[0]);
if (!allSame || versionValues.length < 6) {
  const detail = Object.entries(versions)
    .map(([k, v]) => `${k}=${v ?? "(not found)"}`)
    .join(", ");
  failures.push(`VERSION MISMATCH: ${detail}`);
}

// ---------------------------------------------------------------------------
// 5. Report
// ---------------------------------------------------------------------------
// Compute orphan count: sends unmatched (excluding allowlist) + handlers unsent
const orphanCount = unmatchedSends.length + unsentHandlers.length;
const alignedVersion = allSame && versionValues.length === 6 ? versionValues[0] : "MISMATCH";

if (failures.length > 0) {
  console.error("FAIL — parity audit found issues:");
  for (const f of failures) {
    console.error(`  • ${f}`);
  }
  process.exit(1);
}

// Success
console.log(
  `PASS — ${toolNames.length} tools / ${handlerNames.length} handlers / ${orphanCount} orphans / versions aligned @ ${alignedVersion}`
);
