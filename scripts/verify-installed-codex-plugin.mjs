#!/usr/bin/env node

import { existsSync, lstatSync, readFileSync, readdirSync } from "node:fs";
import { dirname, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const sourceRoot = resolve(repoRoot, "plugins", "sbox-codex-bridge");
const manifest = JSON.parse(
  readFileSync(resolve(sourceRoot, ".codex-plugin", "plugin.json"), "utf8"),
);
const codexHome = process.env.CODEX_HOME;

if (!codexHome) {
  console.error("CODEX_HOME must point at the isolated Codex home used for installation.");
  process.exit(2);
}

const cacheRoot = resolve(
  codexHome,
  "plugins",
  "cache",
  "sboxskins",
  manifest.name,
);

if (!existsSync(cacheRoot) || !lstatSync(cacheRoot).isDirectory()) {
  console.error(`Installed plugin cache root not found: ${cacheRoot}`);
  process.exit(1);
}

const matchingRoots = [];
for (const entry of readdirSync(cacheRoot, { withFileTypes: true })) {
  if (!entry.isDirectory()) continue;
  const candidateRoot = resolve(cacheRoot, entry.name);
  const candidateManifestPath = resolve(
    candidateRoot,
    ".codex-plugin",
    "plugin.json",
  );
  if (!existsSync(candidateManifestPath)) continue;
  try {
    const candidateManifest = JSON.parse(
      readFileSync(candidateManifestPath, "utf8"),
    );
    if (
      candidateManifest.name === manifest.name &&
      candidateManifest.version === manifest.version
    ) {
      matchingRoots.push(candidateRoot);
    }
  } catch {
    // A malformed or unrelated cache entry is not the reviewed plugin payload.
  }
}

if (matchingRoots.length !== 1) {
  console.error(
    `Expected exactly one cache directory for ${manifest.name}@${manifest.version}; ` +
    `found ${matchingRoots.length}.`,
  );
  process.exit(1);
}

const installedRoot = matchingRoots[0];

function inventory(root) {
  const files = new Map();

  function visit(directory) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const absolutePath = resolve(directory, entry.name);
      const relativePath = relative(root, absolutePath).replaceAll("\\", "/");
      const stats = lstatSync(absolutePath);

      if (stats.isSymbolicLink()) {
        throw new Error(`symbolic link is not allowed in plugin payload: ${relativePath}`);
      }
      if (entry.isDirectory()) visit(absolutePath);
      else if (entry.isFile()) files.set(relativePath, readFileSync(absolutePath));
    }
  }

  visit(root);
  return files;
}

let sourceFiles;
let installedFiles;
try {
  sourceFiles = inventory(sourceRoot);
  installedFiles = inventory(installedRoot);
} catch (error) {
  console.error(error.message);
  process.exit(1);
}

const failures = [];
for (const path of sourceFiles.keys()) {
  if (!installedFiles.has(path)) failures.push(`missing from cache: ${path}`);
}
for (const path of installedFiles.keys()) {
  if (!sourceFiles.has(path)) failures.push(`unexpected cache file: ${path}`);
}
for (const [path, expected] of sourceFiles) {
  const actual = installedFiles.get(path);
  if (actual && !expected.equals(actual)) failures.push(`content mismatch: ${path}`);
}

if (failures.length > 0) {
  console.error("Installed Codex plugin differs from the reviewed source:");
  for (const failure of failures) console.error(`  - ${failure}`);
  process.exit(1);
}

console.log(
  `Installed Codex plugin matches source byte-for-byte ` +
  `(${sourceFiles.size} files, ${manifest.name}@${manifest.version}).`,
);
