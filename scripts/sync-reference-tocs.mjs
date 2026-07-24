#!/usr/bin/env node

import {
  existsSync,
  readFileSync,
  readdirSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { collectHeadings } from "./lib/markdown-headings.mjs";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const SKILLS_ROOT = join(ROOT, "plugins", "sbox-claude", "skills");
const WRITE = process.argv.includes("--write");
const unknownArgs = process.argv.slice(2).filter((arg) => arg !== "--write");

if (unknownArgs.length > 0) {
  console.error("Unknown argument(s): " + unknownArgs.join(", "));
  process.exit(2);
}

const START = "<!-- reference-toc:start -->";
const END = "<!-- reference-toc:end -->";
const LONG_REFERENCE_LINES = 100;

function walk(directory) {
  if (!existsSync(directory)) return [];
  const files = [];
  for (const entry of readdirSync(directory)) {
    const fullPath = join(directory, entry);
    if (statSync(fullPath).isDirectory()) files.push(...walk(fullPath));
    else files.push(fullPath);
  }
  return files;
}

function stripExistingToc(text) {
  const start = text.indexOf(START);
  if (start < 0) return text;
  const end = text.indexOf(END, start);
  if (end < 0) throw new Error("Unclosed reference TOC marker");

  const before = text.slice(0, start).replace(/\n+$/, "\n");
  const after = text.slice(end + END.length).replace(/^\n+/, "");
  return before + after;
}

function withSyncedToc(input) {
  const normalized = input.replace(/\r\n/g, "\n");
  const stripped = stripExistingToc(normalized);
  const lines = stripped.split("\n");

  if (lines.length <= LONG_REFERENCE_LINES) return stripped;

  const headings = collectHeadings(stripped).filter((heading) =>
    (heading.depth === 2 || heading.depth === 3) &&
    heading.title.toLowerCase() !== "contents"
  );
  if (headings.length === 0) {
    throw new Error("Long reference has no level-two or level-three headings");
  }

  const toc = [
    START,
    "## Contents",
    "",
    ...headings.map((heading) =>
      (heading.depth === 3 ? "  " : "") +
      "- [" + heading.title + "](#" + heading.slug + ")"
    ),
    END,
  ];

  const h1 = lines.findIndex((line) => /^#\s+/.test(line));
  const insertAt = h1 >= 0 ? h1 + 1 : 0;
  const before = lines.slice(0, insertAt);
  const after = lines.slice(insertAt);
  while (after[0] === "") after.shift();

  return [...before, "", ...toc, "", ...after].join("\n");
}

const references = walk(SKILLS_ROOT)
  .filter((path) => path.toLowerCase().endsWith(".md"))
  .filter((path) => path.split(/[\\/]/).includes("references"))
  .sort();

let drift = 0;
let longReferenceCount = 0;

for (const path of references) {
  const before = readFileSync(path, "utf8");
  const after = withSyncedToc(before);
  if (after.split("\n").length > LONG_REFERENCE_LINES) longReferenceCount += 1;
  if (after === before) continue;

  const rel = relative(ROOT, path);
  if (WRITE) {
    writeFileSync(path, after, "utf8");
    console.log("Updated reference TOC: " + rel);
  } else {
    console.error("DRIFT: " + rel);
    drift += 1;
  }
}

if (drift > 0) {
  console.error(
    "\n" + drift + " reference TOC(s) are stale; run: node scripts/sync-reference-tocs.mjs --write",
  );
  process.exit(1);
}

console.log(
  "Reference TOCs are synchronized (" + longReferenceCount + " long references).",
);
