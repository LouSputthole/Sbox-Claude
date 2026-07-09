#!/usr/bin/env node
/**
 * extract-manifest.mjs — Phase 1 of the native-MCP migration.
 *
 * Imports every compiled tool module (sbox-mcp-server/dist/tools/*.js), calls its
 * register*() functions against a FAKE McpServer that records (name, description,
 * zod shape), introspects the zod schemas directly, and writes tools-manifest.json.
 *
 * The manifest is the single source of truth the C# emitter (emit-mcp-wrappers.mjs)
 * consumes. Run `npm run build` in sbox-mcp-server first so dist/ is fresh.
 *
 * Usage: node scripts/extract-manifest.mjs [outPath]
 */

import { readdirSync, writeFileSync } from "fs";
import { fileURLToPath, pathToFileURL } from "url";
import { dirname, join, basename } from "path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(__dirname, "..");
const toolsDir = join(repoRoot, "sbox-mcp-server", "dist", "tools");
const outPath = process.argv[2] ?? join(__dirname, "tools-manifest.json");

// ── zod introspection ──────────────────────────────────────────────

/** Unwrap Optional/Default/Nullable/Effects wrappers, collecting metadata. */
function unwrap(schema) {
  let s = schema;
  let optional = false;
  let hasDefault = false;
  let def = undefined;
  let description = s?._def?.description;
  for (;;) {
    const t = s?._def?.typeName;
    if (t === "ZodOptional" || t === "ZodNullable") {
      optional = true;
      s = s._def.innerType;
    } else if (t === "ZodDefault") {
      optional = true;
      hasDefault = true;
      def = s._def.defaultValue();
      s = s._def.innerType;
    } else if (t === "ZodEffects") {
      s = s._def.schema;
    } else {
      break;
    }
    description = description ?? s?._def?.description;
  }
  return { inner: s, optional, hasDefault, default: def, description };
}

/** Shape-detect vector-ish objects by their exact key set. */
function vectorKind(shape) {
  const keys = Object.keys(shape).sort().join(",");
  if (keys === "x,y,z") return "vector3";
  if (keys === "pitch,roll,yaw") return "rotation";
  if (keys === "b,g,r" || keys === "a,b,g,r") return "color";
  return null;
}

/** Introspect an unwrapped zod schema into a manifest type node. */
function typeOf(s) {
  const t = s?._def?.typeName;
  switch (t) {
    case "ZodString":
      return { kind: "string" };
    case "ZodNumber": {
      const isInt = (s._def.checks ?? []).some((c) => c.kind === "int");
      return { kind: isInt ? "integer" : "number" };
    }
    case "ZodBoolean":
      return { kind: "boolean" };
    case "ZodEnum":
      return { kind: "enum", values: s._def.values };
    case "ZodLiteral":
      return { kind: "enum", values: [s._def.value] };
    case "ZodArray": {
      const item = unwrap(s._def.type);
      return { kind: "array", items: typeOf(item.inner) };
    }
    case "ZodObject": {
      const shape = s._def.shape();
      const vk = vectorKind(shape);
      if (vk) return { kind: vk };
      return { kind: "object" };
    }
    case "ZodUnion": {
      const opts = s._def.options.map((o) => typeOf(unwrap(o).inner));
      // A union containing a vector-shaped object (with or without a string
      // alternative) is the cross-language vector contract → treat as vector.
      const vec = opts.find((o) =>
        ["vector3", "rotation", "color"].includes(o.kind)
      );
      if (vec) return { kind: vec.kind };
      // Union of primitives collapses to string (loosest wire form).
      if (opts.every((o) => ["string", "number", "integer", "boolean", "enum"].includes(o.kind)))
        return { kind: "string", unionOf: opts.map((o) => o.kind) };
      return { kind: "any", unionOf: opts.map((o) => o.kind) };
    }
    case "ZodRecord":
    case "ZodAny":
    case "ZodUnknown":
      return { kind: "any" };
    default:
      return { kind: "any", note: `unhandled zod type ${t}` };
  }
}

// ── fake server + dummy bridge ─────────────────────────────────────

const tools = [];
let currentModule = "";

const fakeServer = {
  tool(name, description, schemaOrHandler, maybeHandler) {
    let shape = {};
    if (typeof schemaOrHandler === "object" && schemaOrHandler !== null) {
      shape = schemaOrHandler;
    }
    const params = Object.entries(shape).map(([key, zschema]) => {
      const meta = unwrap(zschema);
      const type = typeOf(meta.inner);
      return {
        name: key,
        description: meta.description ?? zschema?._def?.description ?? "",
        optional: meta.optional,
        ...(meta.hasDefault ? { default: meta.default } : {}),
        type,
      };
    });
    tools.push({ module: currentModule, name, description, params });
  },
};

// Register functions only touch the bridge inside handlers, which never run
// here — a permissive Proxy satisfies any property access at registration time.
const dummyBridge = new Proxy(
  {},
  {
    get: () => () => undefined,
  }
);

// ── walk modules ───────────────────────────────────────────────────

// Only modules actually imported by index.ts are part of the MCP surface —
// dist/tools/ can hold stale modules (e.g. console.js, removed in v1.3.0).
import { readFileSync } from "fs";
const indexSrc = readFileSync(
  join(repoRoot, "sbox-mcp-server", "src", "index.ts"),
  "utf-8"
);
const registered = new Set(
  [...indexSrc.matchAll(/from "\.\/tools\/(\w+)\.js"/g)].map((m) => m[1])
);

const files = readdirSync(toolsDir).filter(
  (f) => f.endsWith(".js") && registered.has(basename(f, ".js"))
);
for (const file of files.sort()) {
  currentModule = basename(file, ".js");
  const mod = await import(pathToFileURL(join(toolsDir, file)).href);
  for (const [exportName, fn] of Object.entries(mod)) {
    if (typeof fn === "function" && exportName.startsWith("register")) {
      fn(fakeServer, dummyBridge);
    }
  }
}

tools.sort((a, b) => a.module.localeCompare(b.module) || a.name.localeCompare(b.name));

// No timestamp — output must be deterministic so CI can diff regenerated vs committed.
const manifest = {
  toolCount: tools.length,
  tools,
};

writeFileSync(outPath, JSON.stringify(manifest, null, 2));
console.log(`${tools.length} tools from ${files.length} modules → ${outPath}`);

// Sanity: warn on any param that fell through to "any" with a note.
for (const t of tools) {
  for (const p of t.params) {
    if (p.type.note) console.warn(`  WARN ${t.name}.${p.name}: ${p.type.note}`);
  }
}
