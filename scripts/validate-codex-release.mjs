#!/usr/bin/env node

import { existsSync, lstatSync, readFileSync, readdirSync } from "node:fs";
import { dirname, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const marketplacePath = resolve(repoRoot, ".agents", "plugins", "marketplace.json");
const pluginRoot = resolve(repoRoot, "plugins", "sbox-codex-bridge");
const manifestPath = resolve(pluginRoot, ".codex-plugin", "plugin.json");
const mcpPath = resolve(pluginRoot, ".mcp.json");
const claudeManifestPath = resolve(
  repoRoot,
  "plugins",
  "sbox-claude",
  ".claude-plugin",
  "plugin.json",
);

const failures = [];
const fail = (message) => failures.push(message);

function readJson(path) {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (error) {
    fail(`${relative(repoRoot, path)} is not valid JSON: ${error.message}`);
    return {};
  }
}

function requireFile(path) {
  if (!existsSync(path) || !lstatSync(path).isFile()) {
    fail(`missing required file: ${relative(repoRoot, path)}`);
  }
}

function resolvePluginPath(value, label) {
  if (typeof value !== "string" || value.length === 0) {
    fail(`${label} must be a non-empty relative path`);
    return null;
  }

  if (/^(?:[A-Za-z]:|[\\/])/.test(value) || value.includes("\\")) {
    fail(`${label} must be a portable relative path: ${JSON.stringify(value)}`);
    return null;
  }

  const target = resolve(pluginRoot, value);
  const rel = relative(pluginRoot, target);
  if (rel === ".." || rel.startsWith(`..${sep}`)) {
    fail(`${label} escapes the plugin root: ${JSON.stringify(value)}`);
    return null;
  }
  return target;
}

function parseFrontmatter(path) {
  const text = readFileSync(path, "utf8").replace(/^\uFEFF/, "");
  const match = text.match(/^---\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/);
  if (!match) {
    fail(`${relative(repoRoot, path)} is missing YAML frontmatter`);
    return {};
  }

  const values = {};
  for (const line of match[1].split(/\r?\n/)) {
    const field = line.match(/^([A-Za-z][A-Za-z0-9_-]*):\s*(.+?)\s*$/);
    if (field) values[field[1]] = field[2].replace(/^['"]|['"]$/g, "");
  }
  return values;
}

for (const path of [
  marketplacePath,
  manifestPath,
  mcpPath,
  claudeManifestPath,
  resolve(pluginRoot, "README.md"),
  resolve(pluginRoot, "CHANGELOG.md"),
  resolve(pluginRoot, "LICENSE"),
  resolve(pluginRoot, "NOTICE"),
  resolve(pluginRoot, "THIRD_PARTY_NOTICES.md"),
]) {
  requireFile(path);
}

const marketplace = readJson(marketplacePath);
const manifest = readJson(manifestPath);
const mcp = readJson(mcpPath);
const claudeManifest = readJson(claudeManifestPath);

if (marketplace.name !== "sboxskins") fail("marketplace name must be sboxskins");
if (!Array.isArray(marketplace.plugins) || marketplace.plugins.length !== 1) {
  fail("marketplace must contain exactly one plugin entry");
}

const listing = marketplace.plugins?.[0] ?? {};
if (listing.name !== manifest.name) fail("marketplace and manifest plugin names differ");
if (listing.source?.source !== "local") fail("marketplace plugin source must be local");
if (listing.source?.path !== "./plugins/sbox-codex-bridge") {
  fail("marketplace source path must be ./plugins/sbox-codex-bridge");
}

if (manifest.name !== "sbox-codex-bridge") fail("unexpected Codex plugin name");
if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(manifest.version ?? "")) {
  fail(`plugin version is not semver: ${JSON.stringify(manifest.version)}`);
}
if (manifest.version !== claudeManifest.version) {
  fail("Codex and Claude plugin versions must match for this shared release");
}
if (manifest.license !== "LicenseRef-Sbox-Bridge-Source-Available-1.0") {
  fail("Codex manifest license identifier drifted");
}
if (manifest.repository !== "https://github.com/LouSputthole/Sbox-Claude") {
  fail("Codex manifest repository must point to the public source repository");
}
if (
  process.env.GITHUB_REF_TYPE === "tag" &&
  process.env.GITHUB_REF_NAME !== "codex-v" + manifest.version
) {
  fail(
    "release tag must equal codex-v" + manifest.version +
    "; received " + JSON.stringify(process.env.GITHUB_REF_NAME),
  );
}

for (const [field, kind] of [
  ["skills", "directory"],
  ["mcpServers", "file"],
]) {
  const target = resolvePluginPath(manifest[field], `manifest.${field}`);
  if (!target || !existsSync(target)) {
    if (target) fail(`manifest.${field} target does not exist`);
  } else if (kind === "directory" && !lstatSync(target).isDirectory()) {
    fail(`manifest.${field} must point to a directory`);
  } else if (kind === "file" && !lstatSync(target).isFile()) {
    fail(`manifest.${field} must point to a file`);
  }
}

const serverNames = Object.keys(mcp.mcpServers ?? {}).sort();
if (JSON.stringify(serverNames) !== JSON.stringify(["sbox", "sbox-lifeline"])) {
  fail("MCP configuration must contain exactly sbox and sbox-lifeline");
}

const nativeServer = mcp.mcpServers?.sbox;
if (nativeServer?.type !== "http") fail("native sbox MCP server must use HTTP");
if (nativeServer?.url !== "http://127.0.0.1:7269/mcp") {
  fail("native sbox MCP URL drifted");
}
if (nativeServer?.enabled !== true) {
  fail("native sbox MCP server must be enabled by default");
}

const lifeline = mcp.mcpServers?.["sbox-lifeline"];
if (lifeline?.command !== "npx") fail("lifeline must launch through npx");
const expectedLifelineArgs = [
  "-y",
  "sbox-mcp-server@" + manifest.version,
  "--lifeline",
];
if (JSON.stringify(lifeline?.args) !== JSON.stringify(expectedLifelineArgs)) {
  fail("lifeline args must exactly match the pinned release invocation");
}
if (!lifeline?.args?.includes(`sbox-mcp-server@${manifest.version}`)) {
  fail("lifeline package version must match the shared release version");
}
if (lifeline?.enabled !== false) {
  fail("lifeline must be disabled by default so Node and network access remain opt-in");
}

const skillsRoot = resolve(pluginRoot, "skills");
const expectedSkills = [
  "sbox-api",
  "sbox-build-feature",
  "sbox-cookbook",
  "sbox-design-feature",
  "sbox-game-dev",
  "sbox-scaffold-game",
  "sbox-setup",
];
const actualSkills = existsSync(skillsRoot)
  ? readdirSync(skillsRoot, { withFileTypes: true })
      .filter((entry) => entry.isDirectory())
      .map((entry) => entry.name)
      .sort()
  : [];

if (JSON.stringify(actualSkills) !== JSON.stringify(expectedSkills)) {
  fail(`unexpected skill set: ${actualSkills.join(", ")}`);
}

for (const skillName of actualSkills) {
  const skillFile = resolve(skillsRoot, skillName, "SKILL.md");
  const interfaceFile = resolve(skillsRoot, skillName, "agents", "openai.yaml");
  requireFile(skillFile);
  requireFile(interfaceFile);
  const interfaceText = existsSync(interfaceFile)
    ? readFileSync(interfaceFile, "utf8")
    : "";
  if (existsSync(interfaceFile)) {
    if (!/^interface:\s*$/m.test(interfaceText)) {
      fail(relative(repoRoot, interfaceFile) + " must contain an interface mapping");
    }
    for (const field of ["display_name", "short_description", "default_prompt"]) {
      const fieldPattern = new RegExp("^  " + field + ":\\s*\\S.+$", "m");
      if (!fieldPattern.test(interfaceText)) {
        fail(relative(repoRoot, interfaceFile) + " must define interface." + field);
      }
    }
  }
  if (
    skillName === "sbox-game-dev" &&
    existsSync(interfaceFile) &&
    !/^\s*allow_implicit_invocation:\s*false\s*$/m.test(interfaceText)
  ) {
    fail(
      `${relative(repoRoot, interfaceFile)} must set allow_implicit_invocation: false`,
    );
  }
  if (!existsSync(skillFile)) continue;
  const frontmatter = parseFrontmatter(skillFile);
  if (frontmatter.name !== skillName) {
    fail(`${relative(repoRoot, skillFile)} name must be ${skillName}`);
  }
  if (!frontmatter.description) {
    fail(`${relative(repoRoot, skillFile)} needs a description`);
  }
}

if (failures.length > 0) {
  console.error("Codex release validation failed:");
  for (const failure of failures) console.error(`  - ${failure}`);
  process.exit(1);
}

console.log(
  `Codex release validation passed (${manifest.name}@${manifest.version}, ` +
  `${actualSkills.length} skills, portable marketplace source).`,
);
