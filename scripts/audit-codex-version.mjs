#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const manifestRelative = "plugins/sbox-codex-bridge/.codex-plugin/plugin.json";
const pluginRelative = "plugins/sbox-codex-bridge";
const manifest = JSON.parse(readFileSync(join(repoRoot, manifestRelative), "utf8"));
const changelog = readFileSync(join(repoRoot, pluginRelative, "CHANGELOG.md"), "utf8");

function git(args, options = {}) {
  return execFileSync("git", args, {
    cwd: repoRoot,
    encoding: "utf8",
    stdio: ["ignore", "pipe", options.quiet ? "ignore" : "inherit"],
  }).trim();
}

function parseStableVersion(value, label) {
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.exec(value ?? "");
  if (!match) throw new Error(`${label} must be a stable X.Y.Z version`);
  return match.slice(1).map(Number);
}

function compareVersions(left, right) {
  for (let index = 0; index < 3; index += 1) {
    if (left[index] !== right[index]) return left[index] - right[index];
  }
  return 0;
}

const currentParts = parseStableVersion(manifest.version, "current plugin version");
if (!new RegExp(`^## \\[${manifest.version.replaceAll(".", "\\.")}\\] - \\d{4}-\\d{2}-\\d{2}$`, "m").test(changelog)) {
  console.error(`Codex changelog is missing a dated ${manifest.version} heading.`);
  process.exit(1);
}

let base = process.env.CODEX_BASE_SHA?.trim();
if (!base || /^0+$/.test(base)) {
  try {
    base = git(["merge-base", "origin/main", "HEAD"], { quiet: true });
  } catch {
    try {
      base = git(["rev-parse", "HEAD^"], { quiet: true });
    } catch {
      base = "";
    }
  }
}

if (!base) {
  console.log(`Codex version audit passed (${manifest.version}; no base revision available).`);
  process.exit(0);
}

try {
  git(["cat-file", "-e", `${base}^{commit}`], { quiet: true });
} catch {
  console.error(`Codex version audit cannot resolve base commit ${base}.`);
  process.exit(1);
}

try {
  git(["cat-file", "-e", `${base}:${manifestRelative}`], { quiet: true });
} catch {
  console.log(
    `Codex version audit passed (${manifest.version}; first distribution relative to ${base.slice(0, 12)}).`,
  );
  process.exit(0);
}

let baseManifestText;
try {
  baseManifestText = git(["show", `${base}:${manifestRelative}`], { quiet: true });
} catch {
  console.error(`Codex version audit could not read the existing base manifest at ${base.slice(0, 12)}.`);
  process.exit(1);
}

let baseManifest;
try {
  baseManifest = JSON.parse(baseManifestText);
} catch {
  console.error(`Codex version audit found malformed JSON in the base manifest at ${base.slice(0, 12)}.`);
  process.exit(1);
}

const changedFiles = git(["diff", "--name-only", base, "--", pluginRelative], { quiet: true })
  .split(/\r?\n/)
  .filter(Boolean);

if (changedFiles.length === 0) {
  console.log(`Codex version audit passed (plugin payload unchanged from ${base.slice(0, 12)}).`);
  process.exit(0);
}

const baseParts = parseStableVersion(baseManifest.version, "base plugin version");
if (compareVersions(currentParts, baseParts) <= 0) {
  console.error(
    `Codex plugin payload changed in ${changedFiles.length} file(s), but version ` +
    `${manifest.version} is not greater than base ${baseManifest.version}.`,
  );
  for (const path of changedFiles.slice(0, 20)) console.error(`  - ${path}`);
  process.exit(1);
}

console.log(
  `Codex version audit passed (${baseManifest.version} -> ${manifest.version}; ` +
  `${changedFiles.length} payload file(s) changed).`,
);
