#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { existsSync, readdirSync, statSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), "..");
const packageRoot = join(repoRoot, "sbox-mcp-server");
const sourceRoot = join(packageRoot, "src");

function walk(directory) {
  const files = [];
  for (const entry of readdirSync(directory)) {
    const path = join(directory, entry);
    if (statSync(path).isDirectory()) files.push(...walk(path));
    else files.push(path);
  }
  return files;
}

const expected = new Set(["LICENSE", "README.md", "package.json"]);
for (const sourcePath of walk(sourceRoot)) {
  if (!sourcePath.endsWith(".ts")) continue;
  const sourceRelative = relative(sourceRoot, sourcePath).replaceAll("\\", "/");
  const base = `dist/${sourceRelative.slice(0, -3)}`;
  expected.add(`${base}.js`);
  expected.add(`${base}.d.ts`);
}

const npm = process.platform === "win32" ? "cmd.exe" : "npm";
const npmArgs = process.platform === "win32"
  ? ["/d", "/s", "/c", "npm pack --dry-run --json"]
  : ["pack", "--dry-run", "--json"];
let result;
try {
  const output = execFileSync(npm, npmArgs, {
    cwd: packageRoot,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "inherit"],
  });
  result = JSON.parse(output);
} catch (error) {
  console.error(`npm pack audit could not read the dry-run manifest: ${error.message}`);
  process.exit(1);
}

if (!Array.isArray(result) || result.length !== 1 || !Array.isArray(result[0].files)) {
  console.error("npm pack audit expected exactly one JSON package manifest.");
  process.exit(1);
}

const actual = new Map(result[0].files.map((entry) => [entry.path, entry]));
const failures = [];

for (const path of expected) {
  if (!actual.has(path)) failures.push(`missing package file: ${path}`);
}
for (const path of actual.keys()) {
  if (!expected.has(path)) failures.push(`unexpected package file: ${path}`);
}
for (const [path, entry] of actual) {
  if (!Number.isInteger(entry.size) || entry.size <= 0) {
    failures.push(`empty or invalid package file: ${path}`);
  }
}

const archivePath = join(packageRoot, result[0].filename ?? "");
if (result[0].filename && existsSync(archivePath)) {
  failures.push(`dry-run unexpectedly created an archive: ${result[0].filename}`);
}

if (result[0].name !== "sbox-mcp-server") {
  failures.push(`unexpected package name: ${JSON.stringify(result[0].name)}`);
}
if (result[0].entryCount !== actual.size) {
  failures.push(`entryCount ${result[0].entryCount} does not match ${actual.size} files`);
}

if (failures.length > 0) {
  console.error("npm pack audit failed:");
  for (const failure of failures) console.error(`  - ${failure}`);
  process.exit(1);
}

console.log(
  `npm pack audit passed (${actual.size} allowlisted files; ` +
  `${walk(sourceRoot).filter((path) => path.endsWith(".ts")).length} TypeScript sources).`,
);
