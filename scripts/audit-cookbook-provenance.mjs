#!/usr/bin/env node

import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const CANONICAL = join(ROOT, "plugins", "sbox-claude", "skills", "sbox-cookbook");
const GENERATED = join(ROOT, "plugins", "sbox-codex-bridge", "skills", "sbox-cookbook");
const REGISTRY_REL = "references/SOURCE-REGISTRY.json";
const PROVENANCE_REL = "references/SOURCE-PROVENANCE.md";
const LICENSE_STATUSES = new Set(["unverified", "not-identified", "verified"]);
const USES = new Set(["research-citation-only", "licensed-reuse"]);
const SOURCE_ID = /^[a-z0-9][a-z0-9._/-]*$/;

const DERIVATION_RULES = [
  ["verbatim-from assertion", /\bverbatim\s+(?:from|copy|code|source|implementation)\b/gi],
  ["not-verbatim assertion", /\bnot[- ]verbatim\b/gi],
  ["lifted-from assertion", /\blifted\s+(?:straight\s+)?from\b/gi],
  ["parenthesized derivation label", /\((?:condensed|adapted|paraphrased)(?:\s+from[^)]*)?\)/gi],
  ["adapted/paraphrased assertion", /\b(?:adapted[- ]from|paraphrased(?:[- ]from)?)\b/gi],
  ["condensed derivation assertion", /\bcondensed\s+(?:from|version|copy|code|implementation)\b/gi],
  ["copyable source-derived material", /\bcopyable\s+(?:algorithm|pattern|code|implementation|recipe|block)\b/gi],
  ["most-copied assertion", /\bmost[- ]copied\b/gi],
  ["codifying verbatim material", /\bcodif(?:y|ies|ied|ying)\s+verbatim\b/gi],
  ["source-faithful reproduction", /\bfaithful(?:ly)?\s+(?:to\s+(?:the\s+)?source|copy|reproduction|port|translation)\b/gi],
];

const ABSOLUTE_PATH_RULES = [
  ["Windows drive path", /(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\s`"'<>]*/g],
  ["Windows UNC path", /\\\\[A-Za-z0-9._-]+[\\/][^\s`"'<>]*/g],
  ["Unix user-home path", /(?:^|[\s`"'(])\/(?:Users|home)\/[^/\s]+\//gm],
  ["tilde-home path", /(?:^|[\s`"'(])~[\\/][^\s`"'<>]*/gm],
  ["internal sbox-lessons path", /(?:[A-Za-z]:[\\/])?sbox-lessons[\\/][^\s`"'<>]*/gi],
];

const normalizeLf = (value) => value.replace(/\r\n?/g, "\n");
const lineAt = (text, index) => text.slice(0, index).split("\n").length;

function walkFiles(dir, base = dir) {
  const files = [];
  if (!existsSync(dir)) return files;
  for (const entry of readdirSync(dir).sort()) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) files.push(...walkFiles(full, base));
    else files.push(relative(base, full).replaceAll("\\", "/"));
  }
  return files;
}

function readCookbook(dir) {
  const files = new Map();
  for (const rel of walkFiles(dir)) {
    if (rel.toLowerCase().endsWith(".md") || rel === REGISTRY_REL) {
      files.set(rel, normalizeLf(readFileSync(join(dir, rel), "utf8")));
    }
  }
  return files;
}

function parseJson(text, label, failures) {
  try {
    return JSON.parse(text);
  } catch (error) {
    failures.push(`${label} is not valid JSON: ${error.message}`);
    return null;
  }
}

function checkKeys(value, allowed, label, failures) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    failures.push(`${label} must be an object`);
    return false;
  }
  for (const key of Object.keys(value)) {
    if (!allowed.has(key)) failures.push(`${label} has unknown field ${key}`);
  }
  return true;
}

function parseInventory(markdown, failures) {
  const inventory = new Map();
  const row = /^\|\s*([a-z0-9][a-z0-9._/-]*)\s*\|\s*\[[^\]]+\]\((https:\/\/[^)\s]+)\)\s*\|\s*$/;
  for (const [index, line] of normalizeLf(markdown ?? "").split("\n").entries()) {
    const match = line.match(row);
    if (!match) continue;
    if (inventory.has(match[1])) failures.push(`${PROVENANCE_REL}:${index + 1} duplicates ${match[1]}`);
    inventory.set(match[1], match[2]);
  }
  if (inventory.size !== 51) failures.push(`${PROVENANCE_REL} must declare exactly 51 package rows (found ${inventory.size})`);
  return inventory;
}

function validateRegistry(registry, inventory, failures) {
  if (!checkKeys(registry, new Set(["schemaVersion", "sources"]), "registry", failures)) {
    return;
  }
  if (registry.schemaVersion !== 1) failures.push("registry.schemaVersion must equal 1");
  if (!Array.isArray(registry.sources)) {
    failures.push("registry.sources must be an array");
    return;
  }

  const ids = new Map();
  const urls = new Map();
  const aliases = new Map();
  let previousId = "";

  for (const [index, source] of registry.sources.entries()) {
    const label = `registry.sources[${index}]`;
    if (!checkKeys(source, new Set([
      "id", "url", "licenseStatus", "licenseExpression",
      "licenseEvidenceUrl", "use", "aliases",
    ]), label, failures)) continue;

    const id = typeof source.id === "string" ? source.id : "";
    const url = source.url;
    const status = source.licenseStatus;
    const use = source.use;
    const sourceAliases = Array.isArray(source.aliases) ? source.aliases : null;

    if (!SOURCE_ID.test(id)) failures.push(`${label}.id is invalid`);
    if (id && id.localeCompare(previousId) <= 0) failures.push("registry.sources must be sorted by id with no duplicates");
    previousId = id;
    if (url !== undefined && !/^https:\/\/[^\s]+$/.test(url)) failures.push(`${label}.url must be HTTPS when present`);
    if (!LICENSE_STATUSES.has(status)) failures.push(`${label}.licenseStatus is invalid`);
    if (!USES.has(use)) failures.push(`${label}.use is invalid`);
    if (!sourceAliases || sourceAliases.some((alias) => !SOURCE_ID.test(alias))) {
      failures.push(`${label}.aliases must be an array of valid source identifiers`);
    }
    if (sourceAliases && new Set(sourceAliases).size !== sourceAliases.length) failures.push(`${label}.aliases contains duplicates`);

    if (status === "verified") {
      if (typeof source.licenseExpression !== "string" || source.licenseExpression.trim() === "") {
        failures.push(`${label}.licenseExpression is required for verified status`);
      }
      if (!/^https:\/\/[^\s]+$/.test(source.licenseEvidenceUrl ?? "")) {
        failures.push(`${label}.licenseEvidenceUrl is required for verified status`);
      }
    } else if (source.licenseExpression !== undefined || source.licenseEvidenceUrl !== undefined) {
      failures.push(`${label} must not assert license details while status is ${status}`);
    }

    if (use === "licensed-reuse" && status !== "verified") {
      failures.push(`${label} may use licensed-reuse only with verified license evidence`);
    }
    if (status !== "verified" && use !== "research-citation-only") {
      failures.push(`${label} with unverified terms must use research-citation-only`);
    }

    if (ids.has(id)) failures.push(`${label}.id duplicates ${id}`);
    if (url !== undefined && urls.has(url)) failures.push(`${label}.url duplicates ${url}`);
    if (id) ids.set(id, source);
    if (url !== undefined) urls.set(url, source);

    for (const alias of [id, ...(sourceAliases ?? [])]) {
      const key = alias.toLowerCase();
      if (aliases.has(key) && aliases.get(key) !== id) {
        failures.push(`${label} alias ${alias} is already owned by ${aliases.get(key)}`);
      } else aliases.set(key, id);
    }
  }

  for (const [id, url] of inventory) {
    const source = ids.get(id);
    if (!source) failures.push(`declared source ${id} is missing from SOURCE-REGISTRY.json`);
    else if (source.url !== url) failures.push(`declared source ${id} has a different registry URL`);
    else if (source.use !== "research-citation-only") failures.push(`declared source ${id} must remain research-citation-only unless separately verified`);
  }
}

function checkFenceComments(rel, text, failures) {
  const lines = text.split("\n");
  let fence = null;
  for (const [index, line] of lines.entries()) {
    if (!fence) {
      const opening = line.match(/^\s*([~`]{3,})/);
      if (opening) fence = { marker: opening[1][0], length: opening[1].length };
      continue;
    }
    const closing = line.match(/^\s*([~`]{3,})\s*$/);
    if (closing && closing[1][0] === fence.marker && closing[1].length >= fence.length) {
      fence = null;
      continue;
    }
    if (!/^\s*(?:\/\/|#|<!--|\/\*|\*|@\*)/.test(line)) continue;
    const attribution = /\b(?:source|from|repo|repository|verbatim|lifted|adapted|paraphrased|mirrors?)\b/i;
    const sourcePath = /(?:\brepo[\\/][a-z0-9_.-]+|(?:[A-Za-z0-9_.-]+[\\/])+(?:[A-Za-z0-9_.-]+\.(?:cs|razor|scss|css|json|md|ts|js))|\b[a-z0-9_-]+\.[a-z0-9_.-]+\s*:)/i;
    if (attribution.test(line) && sourcePath.test(line)) {
      failures.push(`${rel}:${index + 1} has a source-path attribution comment inside a fence`);
    }
  }
}

function checkText(rel, text, failures) {
  for (const [label, pattern] of DERIVATION_RULES) {
    pattern.lastIndex = 0;
    for (const match of text.matchAll(pattern)) {
      failures.push(`${rel}:${lineAt(text, match.index)} has forbidden ${label}: ${JSON.stringify(match[0])}`);
    }
  }
  for (const [label, pattern] of ABSOLUTE_PATH_RULES) {
    pattern.lastIndex = 0;
    for (const match of text.matchAll(pattern)) {
      failures.push(`${rel}:${lineAt(text, match.index)} contains a machine-local ${label}`);
    }
  }
  checkFenceComments(rel, text, failures);
}

function checkParity(canonical, generated, failures) {
  if (generated.size === 0) return;
  const canonicalRegistry = canonical.get(REGISTRY_REL);
  const generatedRegistry = generated.get(REGISTRY_REL);
  if (generatedRegistry === undefined) failures.push(`missing generated ${REGISTRY_REL}`);
  else if (canonicalRegistry !== generatedRegistry) failures.push("canonical/generated SOURCE-REGISTRY.json differ");
}

function auditBundle({ canonical, generated }) {
  const failures = [];
  const canonicalRegistryText = canonical.get(REGISTRY_REL);
  if (canonicalRegistryText === undefined) failures.push(`missing canonical ${REGISTRY_REL}`);

  const inventory = parseInventory(canonical.get(PROVENANCE_REL), failures);
  const registry = canonicalRegistryText === undefined ? null : parseJson(canonicalRegistryText, REGISTRY_REL, failures);
  if (registry) validateRegistry(registry, inventory, failures);

  for (const [rel, text] of canonical) {
    if (rel.toLowerCase().endsWith(".md")) checkText(`canonical/${rel}`, text, failures);
  }
  for (const [rel, text] of generated) {
    if (rel.toLowerCase().endsWith(".md")) checkText(`generated/${rel}`, text, failures);
  }
  checkParity(canonical, generated, failures);
  return failures;
}

function fixture() {
  const sources = [];
  const rows = [];
  for (let i = 0; i < 51; i++) {
    const id = `fixture.source${String(i).padStart(2, "0")}`;
    const url = `https://sbox.game/fixture/source${i}/source`;
    sources.push({ id, url, licenseStatus: "unverified", use: "research-citation-only", aliases: [] });
    rows.push(`| ${id} | [source](${url}) |`);
  }
  const registry = { schemaVersion: 1, sources };
  const registryText = JSON.stringify(registry, null, 2) + "\n";
  const provenance = [
    "# Source provenance", "", "| Package identifier | Live source |", "|---|---|", ...rows, "",
  ].join("\n");
  const canonical = new Map([
    [REGISTRY_REL, registryText],
    [PROVENANCE_REL, provenance],
    ["references/example.md", "# Example\n\nResearch-only pattern outline.\n"],
  ]);
  return { canonical, generated: new Map(canonical), registry };
}

function selfTest() {
  const valid = fixture();
  const validFailures = auditBundle(valid);
  if (validFailures.length) throw new Error(`valid fixture failed:\n${validFailures.join("\n")}`);

  const expectFailure = (name, mutate, needle) => {
    const test = fixture();
    mutate(test);
    const failures = auditBundle(test);
    if (!failures.some((failure) => failure.includes(needle))) {
      throw new Error(`${name} did not fail for ${JSON.stringify(needle)}:\n${failures.join("\n")}`);
    }
  };

  expectFailure("derivation assertion", ({ canonical, generated }) => {
    canonical.set("references/example.md", "Verbatim from source.\n");
    generated.set("references/example.md", "Verbatim from source.\n");
  }, "verbatim-from assertion");

  expectFailure("source-path fence comment", ({ canonical, generated }) => {
    const changed = "```csharp\n// Source: foo.bar/Code/Thing.cs\nRun();\n```\n";
    canonical.set("references/example.md", changed);
    generated.set("references/example.md", changed);
  }, "source-path attribution comment");

  expectFailure("absolute path", ({ canonical, generated }) => {
    const windowsHome = ["C:", "Users", "alice", "private.txt"].join("\\");
    const changed = `Never publish ${windowsHome}.\n`;
    canonical.set("references/example.md", changed);
    generated.set("references/example.md", changed);
  }, "machine-local Windows drive path");

  expectFailure("registry drift", ({ generated }) => {
    generated.set(REGISTRY_REL, generated.get(REGISTRY_REL) + " ");
  }, "SOURCE-REGISTRY.json differ");

  expectFailure("unverified reuse", ({ canonical, generated, registry }) => {
    registry.sources[0].use = "licensed-reuse";
    const changed = JSON.stringify(registry, null, 2) + "\n";
    canonical.set(REGISTRY_REL, changed);
    generated.set(REGISTRY_REL, changed);
  }, "licensed-reuse only with verified");

  expectFailure("licensed reuse without evidence", ({ canonical, generated, registry }) => {
    registry.sources[0].licenseStatus = "verified";
    registry.sources[0].licenseExpression = "MIT";
    registry.sources[0].use = "licensed-reuse";
    const changed = JSON.stringify(registry, null, 2) + "\n";
    canonical.set(REGISTRY_REL, changed);
    generated.set(REGISTRY_REL, changed);
  }, "licenseEvidenceUrl is required");

  const ordinary = fixture();
  const safe = [
    "# Safe semantics", "", "The struct is copied by value.",
    "Restore the copied save.", "Use physically faithful motion.", "",
  ].join("\n");
  ordinary.canonical.set("references/example.md", safe);
  ordinary.generated.set("references/example.md", safe);
  const ordinaryFailures = auditBundle(ordinary);
  if (ordinaryFailures.length) throw new Error(`ordinary-language fixture failed:\n${ordinaryFailures.join("\n")}`);

  console.log("PASS - cookbook provenance audit fixtures");
}

if (process.argv.includes("--self-test")) {
  selfTest();
  process.exit(0);
}

const failures = auditBundle({
  canonical: readCookbook(CANONICAL),
  generated: readCookbook(GENERATED),
});

if (failures.length) {
  console.error("FAIL - cookbook provenance audit");
  for (const failure of failures) console.error(`- ${failure}`);
  process.exit(1);
}

console.log("PASS - cookbook provenance registry, language, privacy, and parity");
