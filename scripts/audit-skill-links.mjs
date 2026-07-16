#!/usr/bin/env node

import {
  existsSync,
  readFileSync,
  readdirSync,
  statSync,
} from "node:fs";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { collectHeadings } from "./lib/markdown-headings.mjs";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const CANONICAL_SKILLS_ROOT = join(ROOT, "plugins", "sbox-claude", "skills");
const GENERATED_SKILLS_ROOT = join(ROOT, "plugins", "sbox-codex-bridge", "skills");
const SKILLS_ROOTS = [
  CANONICAL_SKILLS_ROOT,
  GENERATED_SKILLS_ROOT,
].filter((path) => existsSync(path));

function walk(directory) {
  const files = [];
  for (const entry of readdirSync(directory)) {
    const fullPath = join(directory, entry);
    if (statSync(fullPath).isDirectory()) files.push(...walk(fullPath));
    else files.push(fullPath);
  }
  return files;
}

function lineAt(text, index) {
  return text.slice(0, index).split(/\r?\n/).length;
}

function skillRootFor(skillsRoot, path) {
  const rel = relative(skillsRoot, path);
  const skillName = rel.split(/[\\/]/)[0];
  return join(skillsRoot, skillName);
}

function inside(parent, child) {
  const rel = relative(parent, child);
  return rel === "" || (rel !== ".." && !rel.startsWith(".." + sep) && !isAbsolute(rel));
}

const markdownFiles = SKILLS_ROOTS
  .flatMap((skillsRoot) => walk(skillsRoot)
    .filter((file) => file.toLowerCase().endsWith(".md"))
    .map((file) => ({ file, skillsRoot })))
  .sort((a, b) => a.file.localeCompare(b.file));
const findings = [];
const headingSlugsByFile = new Map();

const slugFixtures = new Map([
  ["Combat & Weapons", "combat--weapons"],
  ["TimeUntil + AddShootDelay", "timeuntil--addshootdelay"],
  ["Corpus Index — Cross-Reference", "corpus-index--cross-reference"],
  ["sphere query → falloff", "sphere-query--falloff"],
  ["Привет non-latin 你好", "привет-non-latin-你好"],
  ["😄 emoji", "-emoji"],
]);
for (const [heading, expected] of slugFixtures) {
  const actual = collectHeadings("# " + heading)[0]?.slug;
  if (actual !== expected) {
    throw new Error(`GitHub heading slug fixture failed for ${JSON.stringify(heading)}: ${actual} != ${expected}`);
  }
}
const collisionSlugs = collectHeadings("# Foo\n## Foo!\n### Foo").map((heading) => heading.slug);
if (collisionSlugs.join("|") !== "foo|foo-1|foo-2") {
  throw new Error(`GitHub duplicate heading slug fixture failed: ${collisionSlugs.join("|")}`);
}

function headingSlugsFor(path) {
  if (!headingSlugsByFile.has(path)) {
    const text = readFileSync(path, "utf8");
    headingSlugsByFile.set(
      path,
      new Set(collectHeadings(text).map((heading) => heading.slug)),
    );
  }
  return headingSlugsByFile.get(path);
}

for (const { file, skillsRoot } of markdownFiles) {
  const text = readFileSync(file, "utf8");
  const skillRoot = skillRootFor(skillsRoot, file);
  const headingSlugs = headingSlugsFor(file);
  const seen = new Set();

  if (skillsRoot === CANONICAL_SKILLS_ROOT) {
    const cookbookReferencePattern = /references\/(?:engine|genres|systems)\/[A-Za-z0-9_.-]+\.md/g;
    for (const match of text.matchAll(cookbookReferencePattern)) {
      const target = resolve(skillRoot, match[0]);
      const key = match.index + "|" + target;
      if (seen.has(key)) continue;
      seen.add(key);
      if (!existsSync(target)) {
        findings.push({
          file,
          line: lineAt(text, match.index),
          target: match[0],
          reason: "missing bundled reference",
        });
      }
    }
  }

  const markdownLinkPattern = /!?\[[^\]]*\]\(([^)\s]+)(?:\s+["'][^"']*["'])?\)/g;
  for (const match of text.matchAll(markdownLinkPattern)) {
    const rawTarget = match[1];
    if (/^[a-z][a-z0-9+.-]*:/i.test(rawTarget)) continue;

    const hashIndex = rawTarget.indexOf("#");
    const encodedPath = hashIndex < 0 ? rawTarget : rawTarget.slice(0, hashIndex);
    const encodedFragment = hashIndex < 0 ? null : rawTarget.slice(hashIndex + 1);
    let pathOnly;
    let fragment;
    try {
      pathOnly = decodeURIComponent(encodedPath);
      fragment = encodedFragment === null ? null : decodeURIComponent(encodedFragment);
    } catch {
      findings.push({
        file,
        line: lineAt(text, match.index),
        target: rawTarget,
        reason: "malformed percent-encoding",
      });
      continue;
    }

    if (!pathOnly) {
      if (fragment && !headingSlugs.has(fragment)) {
        findings.push({
          file,
          line: lineAt(text, match.index),
          target: rawTarget,
          reason: "broken same-file fragment",
        });
      }
      continue;
    }

    const target = resolve(dirname(file), pathOnly);
    const key = match.index + "|" + target;
    if (seen.has(key)) continue;
    seen.add(key);

    if (!inside(skillRoot, target)) {
      findings.push({
        file,
        line: lineAt(text, match.index),
        target: rawTarget,
        reason: "link escapes its skill directory",
      });
    } else if (!existsSync(target)) {
      findings.push({
        file,
        line: lineAt(text, match.index),
        target: rawTarget,
        reason: "broken Markdown link",
      });
    } else if (
      fragment !== null &&
      target.toLowerCase().endsWith(".md") &&
      !headingSlugsFor(target).has(fragment)
    ) {
      findings.push({
        file,
        line: lineAt(text, match.index),
        target: rawTarget,
        reason: "broken target-file fragment",
      });
    }
  }
}

if (findings.length > 0) {
  console.error("Skill link audit failed:");
  for (const finding of findings) {
    console.error(
      "  " + relative(ROOT, finding.file) + ":" + finding.line +
      " [" + finding.reason + "] " + finding.target,
    );
  }
  process.exit(1);
}

console.log(
  "Skill link audit passed (" + markdownFiles.length + " Markdown files checked).",
);
