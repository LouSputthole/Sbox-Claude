#!/usr/bin/env node
// gen-codex-plugin.mjs — deterministically generate the Codex plugin (plugins/sbox-codex/)
// from the canonical Claude Code plugin (plugins/sbox-claude/), plus the repo-scoped Codex
// marketplace manifest. Single source of truth: the Claude plugin. Re-running must produce a
// clean `git diff` (parity), same discipline as scripts/audit-parity.mjs.
//
// Usage:  node scripts/gen-codex-plugin.mjs           # write
//         node scripts/gen-codex-plugin.mjs --check    # verify in sync (CI); exit 1 if drift
//
// DO NOT hand-edit plugins/sbox-codex/ — edit the Claude plugin (or scripts/codex-overrides/)
// and regenerate.

import { readFileSync, writeFileSync, mkdirSync, rmSync, existsSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const SRC = join(ROOT, 'plugins', 'sbox-claude');
const DST = join(ROOT, 'plugins', 'sbox-codex');
const OVERRIDES = join(ROOT, 'scripts', 'codex-overrides');
const CHECK = process.argv.includes('--check');

const IDENT = 'sbox-codex-bridge';
const DISPLAY = 's&box Codex Bridge';

// ---- text transform: flip client-specific tokens; protect shared/repo/addon names ----
// The s&box editor addon (the "Claude Bridge" dock, ident `claudebridge` /
// `sboxskinsgg.claudebridge`) is SHARED and unchanged, and our repo slug `Sbox-Claude` /
// canonical plugin `sbox-claude` must survive untouched — otherwise links would wrongly point
// at the competing fork. Only the PRODUCT name and true client references are flipped.
function toCodex(text) {
  const prot = (k) => `@@${k}@@`; // sentinel that never appears in source
  // 1) rename the PRODUCT (we DO want this flipped)
  let t = text.replaceAll('s&box Claude Bridge', 's&box Codex Bridge');
  // 2) protect what must survive
  t = t
    .replaceAll('Claude Bridge', prot('DOCK'))    // shared s&box editor dock, still named "Claude Bridge"
    .replaceAll('Sbox-Claude', prot('REPO'))     // our GitHub repo slug (Codex plugin lives IN it)
    .replaceAll('sbox-claude', prot('CANON'))    // canonical plugin ident / repo paths
    .replaceAll('claudebridge', prot('ADDON'));  // s&box addon ident
  // 3) flip client-specific tokens
  t = t
    .replaceAll('Claude Code', 'Codex')
    .replaceAll('claude mcp add', 'codex mcp add')
    .replaceAll('CLAUDE.md', 'AGENTS.md')
    .replaceAll('.claude/skills', '.agents/skills')
    .replace(/\bClaude\b/g, 'Codex');
  // 4) restore protected tokens
  t = t
    .replaceAll(prot('DOCK'), 'Claude Bridge')
    .replaceAll(prot('REPO'), 'Sbox-Claude')
    .replaceAll(prot('CANON'), 'sbox-claude')
    .replaceAll(prot('ADDON'), 'claudebridge');
  return t;
}

// files whose CONTENT is client-neutral s&box knowledge — copied verbatim, never transformed
function isVerbatim(relPath) {
  const p = relPath.replaceAll('\\', '/');
  return p.includes('/references/') ||
         p.startsWith('skills/sbox-cookbook/') ||
         /\.(png|jpg|jpeg|gif|webp|vpcf|vmdl|vmat|sound)$/i.test(p);
}

function walk(dir, base = dir) {
  const out = [];
  for (const e of readdirSync(dir)) {
    const full = join(dir, e);
    if (statSync(full).isDirectory()) out.push(...walk(full, base));
    else out.push(relative(base, full));
  }
  return out;
}

// ---- build the file set in memory ----
const files = new Map(); // relPathInDst -> string | Buffer

// 1) skills + agents: copy; transform only client-specific markdown
for (const sub of ['skills', 'agents']) {
  const srcSub = join(SRC, sub);
  if (!existsSync(srcSub)) continue;
  for (const rel of walk(srcSub)) {
    const relInPlugin = join(sub, rel);
    const raw = readFileSync(join(srcSub, rel));
    if (relInPlugin.endsWith('.md') && !isVerbatim(relInPlugin)) {
      files.set(relInPlugin, toCodex(raw.toString('utf8')));
    } else {
      files.set(relInPlugin, raw); // verbatim (Buffer)
    }
  }
}

// 2) .codex-plugin/plugin.json — derived from the Claude manifest
const cm = JSON.parse(readFileSync(join(SRC, '.claude-plugin', 'plugin.json'), 'utf8'));
const toolsPhrase = (cm.description.match(/\d+\s+tools/) || ['the full toolset'])[0];
const codexManifest = {
  name: IDENT,
  version: cm.version,
  description: toCodex(cm.description),
  author: { name: 'sboxskins.gg', url: 'https://sboxskins.gg' },
  homepage: 'https://github.com/LouSputthole/Sbox-Claude/tree/main/plugins/sbox-codex',
  repository: 'https://github.com/LouSputthole/Sbox-Claude',
  license: 'LicenseRef-Sbox-Claude-Bridge-Source-Available-1.0',
  keywords: [...new Set([...(cm.keywords || []).filter((k) => k !== 'claude-bridge'), 'codex', 'openai-codex', 'codex-bridge'])],
  skills: './skills/',
  mcpServers: './.mcp.json',
  interface: {
    displayName: DISPLAY,
    shortDescription: 'Build s&box games by talking to Codex — the s&box Codex Bridge.',
    longDescription: toCodex(cm.description),
    developerName: 'sboxskins.gg',
    category: 'game-development',
    capabilities: ['Read', 'Write'],
    websiteURL: 'https://sboxskins.gg',
    defaultPrompt: [
      'Use the s&box Codex Bridge to build a scene from a description.',
      'Check the s&box bridge status.',
    ],
    brandColor: '#10A37F',
  },
};
files.set(join('.codex-plugin', 'plugin.json'), JSON.stringify(codexManifest, null, 2) + '\n');

// 3) .mcp.json — same MCP server, same pinned version
const mcp = JSON.parse(readFileSync(join(SRC, '.mcp.json'), 'utf8'));
files.set('.mcp.json', JSON.stringify(mcp, null, 2) + '\n');

// 4) README.md + AGENTS.md — Codex-authored onboarding
const ver = cm.version;
files.set('README.md', `# ${DISPLAY}

> **Build s&box games by talking to Codex.** Describe what you want — Codex writes the C#, builds the scenes, wires up components, and iterates until it works.

**Source-available (no redistribution)** · built by [sboxskins.gg](https://sboxskins.gg) · the official first-party Codex build of the s&box Bridge.

This is the **same bridge** as the [s&box Claude Bridge](https://github.com/LouSputthole/Sbox-Claude) — same MCP server, same editor addon, same ${toolsPhrase} — packaged for the Codex CLI. Because it is generated from the same source, it never lags behind the Claude version.

## Install

**Every path needs both halves** — the MCP server *and* the s&box editor addon.

### A. Codex plugin — easiest

\`\`\`bash
codex plugin marketplace add LouSputthole/Sbox-Claude
codex plugin add ${IDENT}
\`\`\`

Then install the editor addon (section C below) and open the bridge dock in s&box.

### B. Register the MCP server directly

\`\`\`bash
codex mcp add sbox -- npx -y sbox-mcp-server@latest
\`\`\`

…or add it to \`~/.codex/config.toml\`:

\`\`\`toml
[mcp_servers.sbox]
command = "npx"
args = ["-y", "sbox-mcp-server@${ver}"]
\`\`\`

### C. The s&box editor addon (required for every path)

1. In the s&box editor open **Editor → Library Manager** (this is *not* the Asset Browser — the bridge is a **Library**), search for **\`sboxskinsgg.claudebridge\`**, and install it *into your project*. It lands in \`<your-project>/Libraries/\`.
2. Open your project, open the **View → Claude Bridge** dock (the s&box-side dock keeps its shared name across both builds), and leave it open.
3. **Verify:** ask Codex to *"check the s&box bridge status."* You want \`connected: true\` and a non-zero \`handlerCount\`.

## License

Source-available (no redistribution) — see the repo [LICENSE](https://github.com/LouSputthole/Sbox-Claude/blob/main/LICENSE) and [NOTICE](https://github.com/LouSputthole/Sbox-Claude/blob/main/NOTICE). "s&box Codex Bridge" / "sboxskins.gg" are reserved marks.

<!-- Generated by scripts/gen-codex-plugin.mjs from plugins/sbox-claude/. Do not hand-edit. -->
`);

files.set('AGENTS.md', `# s&box Codex Bridge — agent guidance

You can build s&box games by driving the **live s&box editor** through this bridge (an MCP server named \`sbox\`). It exposes ${toolsPhrase} for scenes, scripts, components, physics, networking, UI, characters, world-gen, and a play-mode playtest harness.

Key disciplines:
- **After visual changes, look.** Use \`screenshot_from\` to aim the camera at what you changed, then read the image — don't guess.
- **Before touching an unfamiliar s&box type, call \`describe_type\` / \`search_types\`.** Live reflection is the source of truth; training data goes stale across SDK versions.
- **The s&box-side dock is named "Claude Bridge"** (a shared component across the Claude and Codex builds) and must stay open, or tool calls time out.
- Skills live under \`.agents/skills/\`; invoke with \`/skills\` or \`$skill-name\`.

<!-- Generated by scripts/gen-codex-plugin.mjs. Do not hand-edit. -->
`);

// 5) overrides win (scripts/codex-overrides/<relpath> replaces a generated file)
if (existsSync(OVERRIDES)) {
  for (const rel of walk(OVERRIDES)) files.set(rel, readFileSync(join(OVERRIDES, rel)));
}

// 6) repo-scoped Codex marketplace manifest (git-subdir → this repo)
const marketplace = {
  name: 'sboxskins',
  interface: { displayName: 'sboxskins.gg s&box plugins' },
  plugins: [
    {
      name: IDENT,
      source: {
        source: 'git-subdir',
        url: 'https://github.com/LouSputthole/Sbox-Claude.git',
        path: './plugins/sbox-codex',
        ref: 'main',
      },
      policy: { installation: 'AVAILABLE', authentication: 'ON_INSTALL' },
      category: 'game-development',
    },
  ],
};
const marketplaceRel = join('.agents', 'plugins', 'marketplace.json');
const marketplaceContent = JSON.stringify(marketplace, null, 2) + '\n';

// ---- emit or check ----
const normalize = (v) => (Buffer.isBuffer(v) ? v : Buffer.from(v, 'utf8'));

if (CHECK) {
  let drift = 0;
  const check = (absPath, content) => {
    const want = normalize(content);
    if (!existsSync(absPath) || !readFileSync(absPath).equals(want)) {
      console.error('DRIFT:', relative(ROOT, absPath));
      drift++;
    }
  };
  for (const [rel, content] of files) check(join(DST, rel), content);
  check(join(ROOT, marketplaceRel), marketplaceContent);
  // also flag stray files in DST that the generator no longer produces
  if (existsSync(DST)) {
    for (const rel of walk(DST)) {
      if (!files.has(rel)) { console.error('STRAY:', join('plugins/sbox-codex', rel)); drift++; }
    }
  }
  if (drift) { console.error(`\n${drift} file(s) out of sync — run: node scripts/gen-codex-plugin.mjs`); process.exit(1); }
  console.log('PASS — Codex plugin in sync with the Claude plugin.');
  process.exit(0);
}

rmSync(DST, { recursive: true, force: true });
let n = 0;
for (const [rel, content] of files) {
  const abs = join(DST, rel);
  mkdirSync(dirname(abs), { recursive: true });
  writeFileSync(abs, normalize(content));
  n++;
}
mkdirSync(dirname(join(ROOT, marketplaceRel)), { recursive: true });
writeFileSync(join(ROOT, marketplaceRel), marketplaceContent);
console.log(`Generated plugins/sbox-codex/ (${n} files) + ${marketplaceRel} — plugin: ${IDENT} @ ${ver}`);
