#!/usr/bin/env node

import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const fixGenerated = process.argv.includes("--fix-generated");
const unknownArgs = process.argv.slice(2).filter((arg) => arg !== "--fix-generated");

if (unknownArgs.length > 0) {
  console.error("Unknown argument(s): " + unknownArgs.join(", "));
  process.exit(2);
}

const graphFiles = [
  resolve(repoRoot, "docs", "graph", "graph.json"),
  resolve(repoRoot, "docs", "graph", "graph.html"),
];

function graphifyId(value) {
  return value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "");
}

function sanitizeGeneratedGraph(text) {
  const currentRepoPrefix = new RegExp("\\b" + graphifyId(repoRoot) + "_", "gi");

  return text
    .replace(currentRepoPrefix, "repo_")
    // Older graph builds encoded an absolute Windows checkout path into node IDs.
    // Strip any such prefix through the repository name without retaining a username.
    .replace(/\b[a-z]_users_[a-z0-9]+_(?:[a-z0-9]+_)*?sbox_claude_/gi, "repo_");
}

if (fixGenerated) {
  for (const file of graphFiles) {
    if (!existsSync(file)) continue;
    const before = readFileSync(file, "utf8");
    const after = sanitizeGeneratedGraph(before);
    if (after !== before) {
      writeFileSync(file, after, "utf8");
      console.log("Sanitized generated graph paths: " + file.slice(repoRoot.length + 1));
    }
  }
}

const repositoryFiles = [...new Set(execFileSync(
  "git",
  ["ls-files", "--cached", "--others", "--exclude-standard", "-z"],
  {
  cwd: repoRoot,
  encoding: "utf8",
  },
).split("\0").filter(Boolean))];

const unixHomePattern =
  /(?<![A-Za-z0-9+./-])\/(?:Users|home)\/(?![<%${])[^/\s"'\x60]+/gim;
const privacyPatterns = [
  {
    label: "Windows user-home path",
    pattern: /\b[A-Za-z]:[\\/]+Users[\\/]+(?![<%${])[^\\/\s"']+/gi,
  },
  {
    label: "encoded Windows user-home path",
    pattern: /\b[a-z]_users_[a-z0-9]+_/gi,
  },
  {
    label: "Unix user-home path",
    pattern: unixHomePattern,
  },
];

function patternMatches(pattern, value) {
  pattern.lastIndex = 0;
  return pattern.test(value);
}

const privateHomeFixture = "/" + "home" + "/" + "alice";
for (const [value, expected] of [
  [String.fromCharCode(96) + privateHomeFixture + "/project", true],
  ["path:" + privateHomeFixture + "/project", true],
  ["https://example.com" + privateHomeFixture + "/project", false],
  ["/" + "home" + "/{username}/project", false],
]) {
  if (patternMatches(unixHomePattern, value) !== expected) {
    console.error("Repository privacy audit internal Unix-path fixture failed.");
    process.exit(2);
  }
}

function decodeText(buffer) {
  if (buffer.length >= 2 && buffer[0] === 0xff && buffer[1] === 0xfe) {
    return buffer.subarray(2).toString("utf16le");
  }
  if (buffer.length >= 2 && buffer[0] === 0xfe && buffer[1] === 0xff) {
    const payload = buffer.subarray(2);
    const evenLength = payload.length - (payload.length % 2);
    const swapped = Buffer.alloc(evenLength);
    for (let index = 0; index < evenLength; index += 2) {
      swapped[index] = payload[index + 1];
      swapped[index + 1] = payload[index];
    }
    return swapped.toString("utf16le");
  }
  if (buffer.includes(0)) return null;
  return buffer.toString("utf8");
}

const findings = [];
let textFileCount = 0;

for (const relativePath of repositoryFiles) {
  for (const { label, pattern } of privacyPatterns) {
    if (patternMatches(pattern, relativePath)) {
      findings.push({ relativePath, line: 1, label: label + " in filename" });
    }
  }

  const absolutePath = resolve(repoRoot, relativePath);
  if (!existsSync(absolutePath)) continue;

  const buffer = readFileSync(absolutePath);
  const text = decodeText(buffer);
  if (text === null) continue;

  textFileCount += 1;

  for (const { label, pattern } of privacyPatterns) {
    pattern.lastIndex = 0;
    for (const match of text.matchAll(pattern)) {
      const line = text.slice(0, match.index).split(/\r?\n/).length;
      findings.push({
        relativePath,
        line,
        label,
      });
    }
  }
}

if (findings.length > 0) {
  console.error("Repository privacy audit failed:");
  for (const finding of findings) {
    const displayPath = finding.label.endsWith(" in filename")
      ? "<redacted-path>"
      : finding.relativePath;
    console.error(
      "  " + displayPath + ":" + finding.line +
      " [" + finding.label + "] <redacted>",
    );
  }
  console.error(
    "Use repository-relative paths or placeholders such as <repo-root> and <username>.",
  );
  process.exit(1);
}

console.log(
  "Repository privacy audit passed (" + textFileCount +
  " tracked or untracked nonignored text files).",
);
