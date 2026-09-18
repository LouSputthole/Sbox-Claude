// Tool-schema tests (GitHub issue #21, item 2): the shared Zod contract
// (issue #22) plus a sweep over EVERY registered tool's parameter schema.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { dirname, join, basename } from "node:path";

import { Vector3Schema, RotationSchema, RotationOrStringSchema } from "../dist/shared/schemas.js";

const here = dirname(fileURLToPath(import.meta.url));
const distTools = join(here, "..", "dist", "tools");

// ── shared schemas ─────────────────────────────────────────────────

test("Vector3Schema accepts the object form and the comma-string form", () => {
  assert.ok(Vector3Schema.safeParse({ x: 1, y: 2, z: 3 }).success);
  assert.ok(Vector3Schema.safeParse("0,0,200").success);
});

test("Vector3Schema rejects wrong shapes", () => {
  assert.equal(Vector3Schema.safeParse({ x: 1, y: 2 }).success, false);
  assert.equal(Vector3Schema.safeParse({ x: "1", y: 2, z: 3 }).success, false);
  assert.equal(Vector3Schema.safeParse(42).success, false);
  assert.equal(Vector3Schema.safeParse(null).success, false);
});

test("RotationSchema is object-only; RotationOrStringSchema also takes 'pitch,yaw,roll'", () => {
  assert.ok(RotationSchema.safeParse({ pitch: 0, yaw: 90, roll: 0 }).success);
  assert.equal(RotationSchema.safeParse("0,90,0").success, false);
  assert.ok(RotationOrStringSchema.safeParse("0,90,0").success);
  assert.ok(RotationOrStringSchema.safeParse({ pitch: 0, yaw: 90, roll: 0 }).success);
  assert.equal(RotationOrStringSchema.safeParse({ pitch: 0, yaw: 90 }).success, false);
});

// ── every tool ─────────────────────────────────────────────────────

/** Register every tool module against a recording fake server (mirrors scripts/extract-manifest.mjs). */
async function collectTools() {
  const indexSrc = readFileSync(join(here, "..", "src", "index.ts"), "utf-8");
  const registered = new Set([...indexSrc.matchAll(/from "\.\/tools\/(\w+)\.js"/g)].map((m) => m[1]));
  const tools = [];
  const fake = {
    tool(name, description, schemaOrHandler, maybeHandler) {
      const shape = typeof schemaOrHandler === "object" && schemaOrHandler !== null ? schemaOrHandler : {};
      const handler = typeof schemaOrHandler === "function" ? schemaOrHandler : maybeHandler;
      tools.push({ name, description, shape, handler });
    },
  };
  const bridge = new Proxy({}, { get: () => () => undefined });
  for (const f of readdirSync(distTools).filter((f) => f.endsWith(".js") && registered.has(basename(f, ".js")))) {
    const mod = await import(pathToFileURL(join(distTools, f)).href);
    for (const [k, fn] of Object.entries(mod)) if (typeof fn === "function" && k.startsWith("register")) fn(fake, bridge);
  }
  return tools;
}

const toolsPromise = collectTools();

test("every registered tool has a unique name, a real description, and a handler", async () => {
  const tools = await toolsPromise;
  assert.ok(tools.length > 250, `expected the full surface, got ${tools.length}`);
  const names = new Set();
  for (const t of tools) {
    assert.ok(!names.has(t.name), `duplicate tool name ${t.name}`);
    names.add(t.name);
    assert.ok(typeof t.description === "string" && t.description.length >= 40, `${t.name}: description too short`);
    assert.equal(typeof t.handler, "function", `${t.name}: no handler`);
  }
});

test("every parameter schema is a Zod schema with a description, and optional-only tools accept {}", async () => {
  const { z } = await import("zod");
  const tools = await toolsPromise;
  for (const t of tools) {
    let allOptional = true;
    for (const [param, schema] of Object.entries(t.shape)) {
      assert.ok(schema && typeof schema.safeParse === "function", `${t.name}.${param}: not a zod schema`);
      assert.ok(typeof schema.description === "string" && schema.description.length > 0, `${t.name}.${param}: missing .describe()`);
      if (!schema.isOptional()) allOptional = false;
    }
    if (allOptional) {
      const r = z.object(t.shape).safeParse({});
      assert.ok(r.success, `${t.name}: all params optional but {} does not parse: ${r.success ? "" : r.error.message}`);
    }
  }
});

test("vector-shaped params round-trip both wire forms (the cross-language contract)", async () => {
  const tools = await toolsPromise;
  let checked = 0;
  for (const t of tools) {
    for (const [param, schema] of Object.entries(t.shape)) {
      const d = (schema.description ?? "").toLowerCase();
      // Heuristic: params documented as {x,y,z} objects OR comma strings must accept both.
      if (/\{x,y,z\}/.test(d) && /comma string/.test(d)) {
        assert.ok(schema.safeParse({ x: 0, y: 0, z: 200 }).success, `${t.name}.${param} rejects the object form`);
        assert.ok(schema.safeParse("0,0,200").success, `${t.name}.${param} rejects the comma-string form`);
        checked++;
      }
    }
  }
  assert.ok(checked >= 10, `expected many vector params, checked ${checked}`);
});

test("the committed manifest has no unhandled zod types", () => {
  const manifest = JSON.parse(readFileSync(join(here, "..", "..", "scripts", "tools-manifest.json"), "utf-8"));
  const bad = [];
  for (const t of manifest.tools) for (const p of t.params) if (p.type?.note) bad.push(`${t.name}.${p.name}: ${p.type.note}`);
  assert.deepEqual(bad, []);
});
