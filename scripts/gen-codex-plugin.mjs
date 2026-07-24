#!/usr/bin/env node
// gen-codex-plugin.mjs — deterministically generate the Codex plugin (plugins/sbox-codex-bridge/)
// from the canonical Claude Code plugin (plugins/sbox-claude/), plus the repo-scoped Codex
// marketplace manifest. Single source of truth: the Claude plugin. Re-running must produce a
// clean `git diff` (parity), same discipline as scripts/audit-parity.mjs.
//
// Usage:  node scripts/gen-codex-plugin.mjs           # write
//         node scripts/gen-codex-plugin.mjs --check    # verify in sync (CI); exit 1 if drift
//
// DO NOT hand-edit plugins/sbox-codex-bridge/ — edit the Claude plugin (or scripts/codex-overrides/)
// and regenerate.

import { readFileSync, writeFileSync, mkdirSync, rmSync, existsSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, relative, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const SRC = join(ROOT, 'plugins', 'sbox-claude');
const DST = join(ROOT, 'plugins', 'sbox-codex-bridge');
const LEGACY_DST = join(ROOT, 'plugins', 'sbox-codex');
const OVERRIDES = join(ROOT, 'scripts', 'codex-overrides');
const CHECK = process.argv.includes('--check');

const IDENT = 'sbox-codex-bridge';
const DISPLAY = 's&box Codex Bridge';
const cm = JSON.parse(readFileSync(join(SRC, '.claude-plugin', 'plugin.json'), 'utf8'));
const ver = cm.version;
const codexTag = `codex-v${ver}`;
const codexReleaseDate = `2026-07-24`;
const codexArchiveUrl = `https://github.com/LouSputthole/Sbox-Claude/archive/refs/tags/${codexTag}.zip`;

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

function toCodexSkill(text) {
  return toCodex(text)
    .replaceAll('Claude Bridge', 's&box Codex Bridge')
    .replaceAll('sbox-claude:sbox-build-feature', '$sbox-build-feature')
    .replaceAll('superpowers:brainstorming', '$sbox-design-feature')
    .replaceAll('use WebFetch on https://wiki.facepunch.com/sbox/ or search Discord', 'browse https://wiki.facepunch.com/sbox/ and prefer official or primary sources')
    .replaceAll('One change per `Edit` call.', 'One logical change per patch.')
    .replaceAll('(Edit/Write)', '(file-edit tools)')
    .replaceAll('`sbox-api`', '`$sbox-api`')
    .replaceAll('`sbox-build-feature`', '`$sbox-build-feature`')
    .replaceAll('`sbox-scaffold-game`', '`$sbox-scaffold-game`')
    .replaceAll('codex mcp add --transport http sbox http://127.0.0.1:7269/mcp', 'codex mcp add sbox --url http://127.0.0.1:7269/mcp')
    .replaceAll('sbox-mcp-server@2 --lifeline', `sbox-mcp-server@${ver} --lifeline`)
    .replaceAll('/sbox-setup', '$sbox-setup');
}

function makePortableSkill(text) {
  return text
    .replace(
      /Then tail the log:\n\n```bash\n[\s\S]*?\n```/,
      "If the native editor server is unavailable, use the `sbox-lifeline` server's `get_compile_errors` and `read_log` tools. Only use a shell fallback after locating the actual s&box install log; never assume a drive letter or Steam library path."
    )
    .replace(
      /## Project-level AGENTS\.md\n\nIf the project you're working on has its own `AGENTS\.md`, \*\*read it first\*\*\.[^\n]*/,
      '## Project instructions\n\nRead the nearest project `AGENTS.md` first. If the project also has a `CLAUDE.md`, use it as secondary project-specific context. Current user instructions win.'
    )
    .replaceAll(
      'Project-level `AGENTS.md` files contain hard-won facts (input bindings, sound paths, role logic). Read them first.',
      'Read the nearest project `AGENTS.md` first. If only `CLAUDE.md` records those project-specific facts, read it as secondary context.'
    )
    .replace(
      /\n## (?:The bridge map \(knowledge graph\)|Bridge map for maintainers)\n[\s\S]*?\n## Project instructions/,
      '\n## Project instructions'
    );
}

const internalResearchRoot = ["sbox", "lessons"].join("-");
const internalMiningRoot = ["mining", "v2"].join("-");
const escapeRegex = (value) => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const internalSepPattern = "[\\\\/]";
const internalDrivePattern =
  "D:" + internalSepPattern + escapeRegex(internalResearchRoot);
const internalRelativeGamesPattern = [
  escapeRegex(internalResearchRoot),
  escapeRegex(internalMiningRoot),
  "games",
].join("/");

function makePortableReference(text) {
  const repaired = new Map([
    ['\u00e2\u20ac\u201d', '—'],
    ['\u00c2\u00b7', '·'],
    ['\u00e2\u2020\u2019', '→'],
    ['\u00e2\u20ac\u00a6', '…'],
    ['\u00c3\u2014', '×'],
    ['\u00e2\u2030\u00a0', '≠'],
    ['\u00c2\u00b1', '±'],
    ['\u00e2\u2020\u201d', '↔'],
    ['\u00c2\u00b2', '²'],
    ['\u00e2\u20ac\u201c', '–'],
    ['\u00e2\u2021\u2019', '⇒'],
    ['\ufeff', ''],
  ]);
  let normalized = text;
  for (const [bad, good] of repaired) normalized = normalized.replaceAll(bad, good);
  return normalized
    .replaceAll('sbox-api', '$sbox-api')
    .replaceAll('sbox-build-feature', '$sbox-build-feature')
    .replaceAll('sbox-scaffold-game', '$sbox-scaffold-game')
    .replace(
      new RegExp(
        "`?" + internalDrivePattern + internalSepPattern +
        "zips-code" + internalSepPattern + "<game>" + internalSepPattern + "?`?",
        "g",
      ),
      'the named upstream game repository',
    )
    .replace(
      new RegExp(
        "`?" + internalDrivePattern + internalSepPattern +
        escapeRegex(internalMiningRoot) + internalSepPattern + "games" +
        internalSepPattern + "([A-Za-z0-9_.-]+\\.md)`?",
        "g",
      ),
      '`upstream:$1`',
    )
    .replace(
      new RegExp(
        internalDrivePattern + internalSepPattern +
        escapeRegex(internalMiningRoot) + internalSepPattern + "games" +
        internalSepPattern + "?",
        "g",
      ),
      'the named upstream repositories',
    )
    .replace(
      new RegExp(
        "`?" + internalRelativeGamesPattern +
        "/([A-Za-z0-9_.-]+\\.md)`?",
        "g",
      ),
      '`upstream:$1`',
    );
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

function normalizeLf(text) {
  return text.replace(/\r\n/g, '\n');
}

function isBinaryAsset(relPath) {
  return /\.(png|jpg|jpeg|gif|webp|vpcf|vmdl|vmat|sound)$/i.test(relPath);
}

function addCodexMcpMap(skillName, text) {
  if (!['sbox-api', 'sbox-build-feature', 'sbox-cookbook', 'sbox-game-dev', 'sbox-scaffold-game', 'sbox-setup'].includes(skillName)) return text;
  const block = `\n## Codex MCP tool mapping\n\nThe plugin registers the enabled-by-default \`sbox\` server and the optional, disabled-by-default \`sbox-lifeline\` server. Codex exposes native-server wrappers such as \`mcp__sbox__search_tools\`, \`mcp__sbox__call_tool\`, and \`mcp__sbox__call_tools\`. Bridge names such as \`get_bridge_status\`, \`describe_type\`, and \`capture_view\` are arguments passed through \`call_tool\`; they are not assumed to be standalone host tools. If Codex exposes a slightly different normalized tool name, trust the current tool inventory.\n`;
  return text.replace(/^(# .+\n)/m, `$1${block}`);
}

// ---- build the file set in memory ----
const files = new Map(); // relPathInDst -> string | Buffer

// 1) Copy the canonical skills. Preserve reference meaning while repairing known encoding
// damage and local provenance paths; apply host-specific wording to discoverable instructions.
const srcSkills = join(SRC, 'skills');
for (const rel of walk(srcSkills)) {
  const relInPlugin = join('skills', rel);
  const raw = readFileSync(join(srcSkills, rel));
  if (isBinaryAsset(relInPlugin)) {
    files.set(relInPlugin, raw);
    continue;
  }
  let text = normalizeLf(raw.toString('utf8'));
  const portableRel = relInPlugin.replaceAll('\\', '/');
  if (
    portableRel.startsWith('skills/sbox-cookbook/references/') &&
    !portableRel.endsWith('/SOURCE-REGISTRY.json')
  ) text = makePortableReference(text);
  if (relInPlugin.endsWith('SKILL.md')) text = makePortableSkill(toCodexSkill(text));
  const skillName = rel.split(/[\\/]/)[0];
  if (relInPlugin.endsWith('SKILL.md')) text = addCodexMcpMap(skillName, text);
  files.set(relInPlugin, text);
}

const cookbookSkillPath = join('skills', 'sbox-cookbook', 'SKILL.md');
files.set(cookbookSkillPath, files.get(cookbookSkillPath).replace(
  /^description:.*$/m,
  'description: Route s&box whole-game and system design to code-grounded recipes. Use for genre playbooks (tycoon, shopkeeper, arena, platformer, survival, card battler, social, puzzle, sandbox, vehicles, roleplay) or systems such as inventory, economy, shops, saves, progression, loot, leaderboards, building, crafting, dialogue, rounds, waves, networking authority, anti-cheat, UI, physics, world generation, and performance. Open the matched reference and verify exact APIs live; do not answer from the router alone.'
));

const apiSkillPath = join('skills', 'sbox-api', 'SKILL.md');
files.set(apiSkillPath, files.get(apiSkillPath).replace(
  /^description:.*$/m,
  'description: Use when writing or modifying C#, Razor UI, networking, components, physics, input, navigation, or scene code for an s&box project. Triggers on s&box or Source 2 files such as .sbproj, using Sandbox, Sandbox.Component, GameObject, PanelComponent, Scene.Trace, Sync, or RPC attributes. Routes to curated references and requires live bridge reflection for exact SDK signatures. Writes idiomatic s&box code and prevents Unity-pattern leakage.'
));

// Claude's Markdown specialist agent is not a Codex plugin component. Package the same
// focused behavior as a normal Codex skill so it is discoverable and invocable.
const specialistSource = normalizeLf(readFileSync(join(SRC, 'agents', 'sbox-game-dev.md'), 'utf8'));
files.set(join('skills', 'sbox-game-dev', 'SKILL.md'), addCodexMcpMap('sbox-game-dev', makePortableSkill(toCodexSkill(specialistSource))));

files.set(join('skills', 'sbox-design-feature', 'SKILL.md'), `---
name: sbox-design-feature
description: Use before implementing a non-trivial s&box feature that needs design choices, state transitions, networking authority, animation or camera behavior, UI interaction, or visual acceptance criteria. Produces a compact implementation and verification design that hands off to sbox-build-feature.
---

# Design an s&box Feature

Create the smallest design that removes expensive ambiguity before code changes.

1. Read the nearest project \`AGENTS.md\` and any project-specific \`CLAUDE.md\` that records s&box facts. Current user instructions win.
2. State the player-visible outcome and the explicit non-goals.
3. Identify the owning components, state transitions, data flow, and multiplayer authority boundary.
4. List unfamiliar s&box types or signatures that must be verified through live reflection before implementation.
5. Split the work into small implementation slices. Keep parallel agents on disjoint source files; keep scene mutation, play mode, and screenshots under one coordinator.
6. Define proof: clean compile, structural inspection, runtime assertions where applicable, and framed screenshots for every visual outcome.

Return a concise design with assumptions and acceptance checks. Then follow \`$sbox-build-feature\` to implement and verify it.
`);

const skillInterfaces = {
  'sbox-api': ['S&box API Brain', 'Write idiomatic s&box C# with live API checks', 'Use $sbox-api to write an idiomatic s&box component for this feature.'],
  'sbox-build-feature': ['Build an S&box Feature', 'Build and visually verify s&box game features', 'Use $sbox-build-feature to implement and verify this s&box change.'],
  'sbox-cookbook': ['S&box Cookbook', 'Route to proven s&box genre and system recipes', 'Use $sbox-cookbook to design this s&box game or system.'],
  'sbox-design-feature': ['Design an S&box Feature', 'Design s&box systems before implementation', 'Use $sbox-design-feature to design this non-trivial s&box feature.'],
  'sbox-game-dev': ['S&box Game Developer', 'Run focused s&box game-development workflows', 'Use $sbox-game-dev to own this focused s&box development task.'],
  'sbox-scaffold-game': ['Scaffold an S&box Game', 'Scaffold a playable first-person s&box starter', 'Use $sbox-scaffold-game to create a playable s&box starter.'],
  'sbox-setup': ['Set Up the S&box Bridge', 'Connect and orient the s&box editor bridge', 'Use $sbox-setup to check and orient the s&box bridge.'],
};

for (const [skillName, [displayName, shortDescription, defaultPrompt]] of Object.entries(skillInterfaces)) {
  files.set(join('skills', skillName, 'agents', 'openai.yaml'), `interface:\n  display_name: ${JSON.stringify(displayName)}\n  short_description: ${JSON.stringify(shortDescription)}\n  default_prompt: ${JSON.stringify(defaultPrompt)}\n`);
}

// 2) .codex-plugin/plugin.json — derived from the Claude manifest
const codexManifest = {
  name: IDENT,
  version: cm.version,
  description: 'Build s&box games with Codex through the native editor MCP bridge, an API brain, a code-grounded cookbook, and screenshot-driven workflows.',
  author: { name: 'sboxskins.gg', url: 'https://sboxskins.gg' },
  homepage: 'https://github.com/LouSputthole/Sbox-Claude/tree/main/plugins/sbox-codex-bridge',
  repository: 'https://github.com/LouSputthole/Sbox-Claude',
  license: 'LicenseRef-Sbox-Bridge-Source-Available-1.0',
  keywords: [...new Set([...(cm.keywords || []).filter((k) => k !== 'claude-bridge'), 'codex', 'openai-codex', 'codex-bridge'])],
  skills: './skills/',
  mcpServers: './.mcp.json',
  interface: {
    displayName: DISPLAY,
    shortDescription: 'Build and verify s&box games from Codex.',
    longDescription: 'Connect Codex to the native s&box editor MCP server, write idiomatic s&box C# with a schema-grounded API brain, route through a code-grounded cookbook, scaffold playable starters, and verify runtime or visual changes through inline screenshots and lifeline diagnostics.',
    developerName: 'sboxskins.gg',
    category: 'Developer Tools',
    capabilities: ['Interactive', 'Read', 'Write'],
    websiteURL: 'https://sboxskins.gg',
    defaultPrompt: [
      'Use the s&box Codex Bridge to build a scene from a description.',
      'Check the s&box bridge status.',
      'Use the s&box cookbook to design an inventory system.',
    ],
    brandColor: '#10A37F',
  },
};
files.set(join('.codex-plugin', 'plugin.json'), JSON.stringify(codexManifest, null, 2) + '\n');

// 3) .mcp.json — same MCP servers and pinned version. Codex can reach the
// native editor immediately; keep the Node-based lifeline opt-in so installs
// do not spawn npx or require registry access unless the user asks for it.
const mcp = JSON.parse(readFileSync(join(SRC, '.mcp.json'), 'utf8'));
mcp.mcpServers.sbox.enabled = true;
mcp.mcpServers['sbox-lifeline'].enabled = false;
files.set('.mcp.json', JSON.stringify(mcp, null, 2) + '\n');

files.set('LICENSE', normalizeLf(readFileSync(join(ROOT, 'LICENSE'), 'utf8')));
files.set('NOTICE', `s&box Codex Bridge
Copyright (c) 2026 sboxskins.gg
All rights reserved.

This plugin is SOURCE-AVAILABLE, not open source. See LICENSE for the full
s&box Bridge Source-Available License 1.0 terms.

Separately licensed material
----------------------------
- The bundled skills/sbox-api component is adapted from claude-sbox under the
  MIT License. See skills/sbox-api/LICENSE and
  skills/sbox-api/ATTRIBUTION.md.
- The optional sbox-mcp-server@${ver} lifeline is fetched externally by npx
  only when a user enables it; it is not bundled in this plugin. It is
  licensed under the MIT License. See:
  https://github.com/LouSputthole/Sbox-Claude/blob/main/sbox-mcp-server/LICENSE

Names, branding & trademark
---------------------------
"s&box Bridge", "s&box Claude Bridge", "s&box Codex Bridge", and
"sboxskins.gg", and their logos and branding, are trademarks of sboxskins.gg
and are not licensed for reuse.
`);
files.set('THIRD_PARTY_NOTICES.md', `# Third-Party Notices

## sbox-api

The \`sbox-api\` skill is adapted from \`claude-sbox\` by David Ryan under the MIT License. See \`skills/sbox-api/ATTRIBUTION.md\` and \`skills/sbox-api/LICENSE\`.

## sbox-cookbook

The cookbook is independently authored synthesis and language-neutral pseudocode informed by cited public-source s&box projects. No upstream project source code is bundled in this plugin. \`skills/sbox-cookbook/references/CORPUS-INDEX.md\` summarizes the cited patterns, \`SOURCE-PROVENANCE.md\` explains the research boundary, and \`SOURCE-REGISTRY.json\` records the machine-readable source and license status. Entries marked \`research-citation-only\` are technical citations, not grants to reuse upstream code. Visible source is not by itself a license grant; review the current upstream license before reusing upstream material.
`);

// 4) Codex-authored onboarding. Runtime guidance lives in skills; a plugin-root
// AGENTS.md would not apply to a consumer's project.
files.set('README.md', `# ${DISPLAY}

> **Build s&box games with Codex.** Describe the result; Codex can write the C#, drive the editor, wire components, playtest, and inspect screenshots.

First-party Codex distribution from [sboxskins.gg](https://sboxskins.gg), generated from the same source as the s&box Claude Bridge.

## Included

- Native s&box editor MCP at \`http://127.0.0.1:7269/mcp\`.
- Optional \`sbox-lifeline\` diagnostics while the editor is unavailable, bundled but disabled by default.
- The \`sbox-api\` brain and code-grounded \`sbox-cookbook\`.
- Design, build/verify, playable-scaffold, setup, and focused game-development skills.
- Product license, notice, and third-party provenance inside the package.

## Prerequisites

- A current Codex release with plugin support. In Codex CLI, \`codex plugin --help\` must succeed and \`/plugins\` must open the plugin browser.
- s&box with the \`sboxskinsgg.claudebridge\` editor library installed in the project and the native MCP server enabled.
- Node.js 18 or newer, npm/npx, and npm registry access only if you enable the bundled \`sbox-lifeline\` diagnostics server. The native editor bridge and bundled skills do not require Node.js.

## Install

### Rolling channel (default)

The default Git-backed install tracks the repository main branch. This is a rolling channel: marketplace upgrades fetch the latest compatible package from main.

~~~bash
codex plugin marketplace add LouSputthole/Sbox-Claude --ref main
codex plugin add ${IDENT}@sboxskins
~~~

### Immutable release

After ${codexTag} is published, pin that tag for a reproducible 2.2.0 install. A pinned marketplace does not advance when main changes.

~~~bash
codex plugin marketplace add LouSputthole/Sbox-Claude --ref ${codexTag}
codex plugin add ${IDENT}@sboxskins
~~~

After adding either marketplace, you can also launch Codex, enter /plugins, select the **sboxskins.gg s&box plugins** marketplace, and install **${DISPLAY}** from the browser.

### Manual GitHub download

After ${codexTag} is published, if the owner/repository install is unavailable, download and extract the [\`${codexTag}\` source archive](${codexArchiveUrl}), or clone that tag. Then register the extracted repository folder and install the plugin:

\`\`\`bash
codex plugin marketplace add <extracted-repo-folder>
codex plugin add ${IDENT}@sboxskins
\`\`\`

Then install or update the editor addon, confirm the native MCP server is enabled, and start a **new Codex session** so the plugin's skills and native MCP server load. The optional lifeline remains disabled until you enable it.

## Update

### Rolling channel

If the marketplace was added with --ref main, refresh it in place and reinstall the current plugin package:

~~~bash
codex plugin marketplace upgrade sboxskins
codex plugin add ${IDENT}@sboxskins
~~~

### Pinned release

A marketplace pinned to ${codexTag} is immutable. To move to a later Codex release, remove the installed plugin and marketplace, then add the next published tag and reinstall:

~~~bash
codex plugin remove ${IDENT}@sboxskins
codex plugin marketplace remove sboxskins
codex plugin marketplace add LouSputthole/Sbox-Claude --ref <next-codex-release-tag>
codex plugin add ${IDENT}@sboxskins
~~~

### Manual archive

For a manual archive installation, remove the old local marketplace, download and extract the new immutable release archive, then register that new extracted repository folder and reinstall:

\`\`\`bash
codex plugin remove ${IDENT}@sboxskins
codex plugin marketplace remove sboxskins
codex plugin marketplace add <new-extracted-repo-folder>
codex plugin add ${IDENT}@sboxskins
\`\`\`

Update the \`sboxskinsgg.claudebridge\` library through s&box's Library Manager as well, then start a new Codex session.

## Uninstall

\`\`\`bash
codex plugin remove ${IDENT}@sboxskins
\`\`\`

If you no longer use anything from the sboxskins marketplace, remove that marketplace too:

\`\`\`bash
codex plugin marketplace remove sboxskins
\`\`\`

Removing the Codex plugin does not remove the editor library from an s&box project. Remove \`sboxskinsgg.claudebridge\` separately through the Library Manager when you no longer need it.

## Enable lifeline diagnostics (optional)

The bundled \`sbox-lifeline\` server is disabled by default. Enable it in Codex's MCP settings when you want log and compile diagnostics to remain available while the editor is down, then start a new Codex session. Enabling it requires Node.js 18 or newer, npm/npx, and network access to fetch the pinned package on first use.

If your Codex surface cannot toggle a plugin-provided MCP server, register the same pinned server as a user-level fallback:

\`\`\`bash
codex mcp add sbox-lifeline -- npx -y sbox-mcp-server@${ver} --lifeline
\`\`\`

Remove that user-level fallback separately when you no longer need it:

\`\`\`bash
codex mcp remove sbox-lifeline
\`\`\`

## MCP-only fallback

If you want the live editor tools without the bundled skills:

\`\`\`bash
codex mcp add sbox --url http://127.0.0.1:7269/mcp
\`\`\`

Optionally add editor-down diagnostics; this separate lifeline command requires Node.js, npm/npx, and registry access:

\`\`\`bash
codex mcp add sbox-lifeline -- npx -y sbox-mcp-server@${ver} --lifeline
\`\`\`

## Editor addon (required)

1. In the s&box editor open **Editor → Library Manager** (this is *not* the Asset Browser — the bridge is a **Library**), search for **\`sboxskinsgg.claudebridge\`**, and install it *into your project*. It lands in \`<your-project>/Libraries/\`.
2. In **Editor → Preferences → MCP Server**, confirm the native server is enabled on port 7269. The legacy Claude Bridge dock is optional on v2 and does not need to remain open.
3. Start a new Codex session and ask: **"Use $sbox-setup to check the bridge and orient me."**

## Verify

- \`codex plugin list\` shows \`${IDENT}@sboxskins\` installed.
- A new session exposes the bundled s&box skills.
- \`mcp__sbox__search_tools\` answers, and a wrapped \`get_bridge_status\` call reports the addon compiled and connected.
- A visual smoke test returns an inline image from \`capture_view\` or \`screenshot_orbit\`.

## Development

Do not hand-edit this generated directory. Update \`plugins/sbox-claude/\` or the generator's explicit Codex transforms, then run \`node scripts/gen-codex-plugin.mjs\` and \`node scripts/gen-codex-plugin.mjs --check\`.

## License

Source-available. See the bundled [LICENSE](LICENSE), [NOTICE](NOTICE), and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). "s&box Codex Bridge" / "sboxskins.gg" are reserved marks.

<!-- Generated by scripts/gen-codex-plugin.mjs from plugins/sbox-claude/. Do not hand-edit. -->
`);

// First Codex distribution. The existing repository tag `v2.1.0` predates this
// package, so publish this commit under the distinct immutable Codex tag derived above.
files.set('CHANGELOG.md', `# Changelog

## [${ver}] - ${codexReleaseDate}

First public Codex distribution of the s&box Bridge product family.

- Added the GitHub-backed \`sboxskins\` Codex marketplace and \`${IDENT}\` plugin.
- Bundled the enabled native s&box MCP endpoint and the pinned, opt-in \`sbox-mcp-server@${ver}\` lifeline.
- Bundled the API brain, code-grounded cookbook, setup, design, build/verify, scaffold, and focused game-development skills.
- Added reproducible generation, parity checks, product licensing, third-party notices, and install troubleshooting.

**Recommended immutable release tag:** \`${codexTag}\`. The repository tag \`v2.1.0\` predates the Codex package; Codex releases use the separate \`codex-v*\` tag series.

<!-- Generated by scripts/gen-codex-plugin.mjs. Do not hand-edit. -->
`);

files.set('TROUBLESHOOTING.md', [
  "# Troubleshooting the s&box Codex Bridge",
  "",
  "## Port 7269 does not answer",
  "",
  "The native MCP server lives inside the s&box editor. Start s&box, open the project, and confirm **Editor → Preferences → MCP Server** is enabled at `http://127.0.0.1:7269/mcp`. If the editor log says the port could not start, wait for any stale s&box process to exit and restart the editor.",
  "",
  "## Native tools appear, but bridge tools do not",
  "",
  "Install `sboxskinsgg.claudebridge` into the open project's `Libraries/` folder through **Editor → Library Manager**. Call `compile_status`; if editor tools are unavailable, enable the bundled `sbox-lifeline` in plugin MCP settings, or register the fallback below, then use `get_compile_errors`. Bridge toolsets register after a clean addon compile.",
  "",
  "## Project or addon edits do not recompile",
  "",
  "For project `Code/` edits, call `trigger_hotload`, then `compile_status`. If an external edit was missed, `start_play` forces a project compile; stop play before more scene mutations. For addon files under `Libraries/claudebridge/Editor/`, use `restart_editor` and verify the live version through `get_bridge_status`.",
  "",
  "## The editor crashed or every native tool is unavailable",
  "",
  "The native server dies with the editor. The bundled `sbox-lifeline` is disabled by default; enable it in plugin MCP settings. If that surface cannot toggle it, register this user-level fallback:",
  "",
  "```bash",
  "codex mcp add sbox-lifeline -- npx -y sbox-mcp-server@" + ver + " --lifeline",
  "```",
  "",
  "## A scene mutation is refused",
  "",
  "Stop play with `stop_play` (or the native `play_stop`) before persistent scene edits. Use `set_runtime_property` only for temporary play-mode tuning.",
  "",
  "## Calls time out while the editor is open",
  "",
  "Dismiss modal dialogs or save prompts in the editor; they block its main thread. If none is visible, restart the editor and, when the optional lifeline is enabled, use its log to distinguish a compile failure from a render or GPU stall.",
  "",
  "When reporting a bug, include `get_bridge_status`, the recent lifeline log when available, the exact tool call, and whether the project or addon was recompiled: https://github.com/LouSputthole/Sbox-Claude/issues",
  "",
  "<!-- Generated by scripts/gen-codex-plugin.mjs. Do not hand-edit. -->",
  "",
].join('\n'));

// 5) overrides win (scripts/codex-overrides/<relpath> replaces a generated file)
if (existsSync(OVERRIDES)) {
  for (const rel of walk(OVERRIDES)) {
    const raw = readFileSync(join(OVERRIDES, rel));
    files.set(rel, isBinaryAsset(rel) ? raw : normalizeLf(raw.toString('utf8')));
  }
}

// 6) repo-scoped Codex marketplace manifest. A local source is resolved from
// the fetched marketplace snapshot, so installs preserve the selected ref.
const marketplace = {
  name: 'sboxskins',
  interface: { displayName: 'sboxskins.gg s&box plugins' },
  plugins: [
    {
      name: IDENT,
      source: {
        source: 'local',
        path: './plugins/sbox-codex-bridge',
      },
      policy: { installation: 'AVAILABLE', authentication: 'ON_INSTALL' },
      category: 'Developer Tools',
    },
  ],
};
const marketplaceRel = join('.agents', 'plugins', 'marketplace.json');
const marketplaceContent = JSON.stringify(marketplace, null, 2) + '\n';

function validateGenerated() {
  const asText = (value) => Buffer.isBuffer(value) ? value.toString('utf8') : value;
  const fail = (message) => { throw new Error(`Codex plugin invariant failed: ${message}`); };

  if (basename(DST) !== IDENT || codexManifest.name !== IDENT) fail('directory and manifest names must match');
  if (dirname(LEGACY_DST) !== join(ROOT, 'plugins')) fail('legacy migration path escaped the plugins directory');
  if (codexManifest.version !== cm.version) fail('Codex and Claude plugin versions must match');
  if (files.has('AGENTS.md')) fail('runtime instructions must live in skills, not a plugin-root AGENTS.md');
  if ([...files.keys()].some((rel) => rel.replaceAll('\\', '/').startsWith('agents/'))) fail('Claude agent folders are not Codex plugin components');

  const expectedSkills = Object.keys(skillInterfaces).sort();
  const skillFiles = [...files.keys()].filter((rel) => rel.replaceAll('\\', '/').endsWith('/SKILL.md'));
  const actualSkills = skillFiles.map((rel) => rel.replaceAll('\\', '/').split('/')[1]).sort();
  if (JSON.stringify(actualSkills) !== JSON.stringify(expectedSkills)) fail(`expected skills ${expectedSkills.join(', ')}`);

  const internalForwardDrive = ["D:", internalResearchRoot].join("/");
  const internalBackwardDrive = ["D:", internalResearchRoot].join("\\");
  const internalGamesPath = [
    internalResearchRoot,
    internalMiningRoot,
    "games",
    "",
  ].join("/");
  const forbidden = [
    'superpowers:brainstorming',
    'sbox-claude:sbox-build-feature',
    'claude mcp add',
    '--transport http',
    'A:/SteamLibrary',
    'A:\\SteamLibrary',
    internalForwardDrive,
    internalBackwardDrive,
    internalGamesPath,
    '/sbox-setup',
    'WebFetch',
    'Edit/Write',
  ];
  for (const rel of skillFiles) {
    const text = asText(files.get(rel));
    const frontmatter = text.match(/^---\n([\s\S]*?)\n---\n/);
    if (!frontmatter) fail(`${rel} has no YAML frontmatter`);
    const description = frontmatter[1].match(/^description:\s*(.+)$/m)?.[1]?.trim();
    if (!description) fail(`${rel} has no description`);
    if (description.length > 1024) fail(`${rel} description exceeds 1024 characters`);
    if (/[<>]/.test(description)) fail(`${rel} description contains a forbidden angle bracket`);
    for (const token of forbidden) if (text.includes(token)) fail(`${rel} contains unsupported token ${JSON.stringify(token)}`);
  }

  const forbiddenPluginText = [
    'mcp__sbox__describe_type',
    '/graphify',
    'docs/graph/',
    'sbox-mcp-server@2 --lifeline',
    '\u00e2\u20ac\u201d',
    '\u00e2\u2020\u2019',
    '\u00e2\u20ac\u00a6',
    '\u00c3\u2014',
    '\u00e2\u20ac\u201c',
    '\u00e2\u2021\u2019',
  ];
  const machineLocalPath = /(?<![A-Za-z0-9+.-])[A-Za-z]:[\\/]/;
  const staleScreenshotTarget = /\bscreenshot_from\b[^\r\n]{0,500}\btarget\s*=/;
  const staleScreenshotTargetArgument = /\bscreenshot_from\b[^\r\n]{0,500}\barguments\s*=\s*\{[^\r\n}]{0,500}["']?target["']?\s*:/;
  for (const [rel, value] of files) {
    if (Buffer.isBuffer(value)) continue;
    for (const token of forbiddenPluginText) if (value.includes(token)) fail(`${rel} contains unpackaged or stale instruction ${JSON.stringify(token)}`);
    if (staleScreenshotTarget.test(value)) fail(`${rel} uses target= with screenshot_from; use id=`);
    if (staleScreenshotTargetArgument.test(value)) fail(`${rel} passes a target argument to screenshot_from; use id`);
    if (machineLocalPath.test(value)) fail(`${rel} contains a machine-local absolute path`);
    if (rel.replaceAll('\\', '/').startsWith('skills/sbox-cookbook/references/') && /(?<!\$)sbox-(?:api|build-feature|scaffold-game)/.test(value)) {
      fail(`${rel} contains a non-invocable sibling skill reference`);
    }
  }

  const generatedReadme = asText(files.get('README.md'));
  const lifecycleCommands = [
    'codex plugin marketplace add LouSputthole/Sbox-Claude --ref main',
    `codex plugin marketplace add LouSputthole/Sbox-Claude --ref ${codexTag}`,
    'codex plugin marketplace add LouSputthole/Sbox-Claude --ref <next-codex-release-tag>',
    'codex plugin marketplace add <extracted-repo-folder>',
    `codex plugin add ${IDENT}@sboxskins`,
    'codex plugin marketplace upgrade sboxskins',
    `codex plugin remove ${IDENT}@sboxskins`,
    'codex plugin marketplace remove sboxskins',
    `codex mcp add sbox-lifeline -- npx -y sbox-mcp-server@${ver} --lifeline`,
    'codex mcp remove sbox-lifeline',
  ];
  for (const command of lifecycleCommands) {
    if (!generatedReadme.includes(command)) fail(`README is missing lifecycle command ${JSON.stringify(command)}`);
  }
if (!files.has('CHANGELOG.md')) fail('generated Codex plugin must include CHANGELOG.md');
  if (!generatedReadme.includes(codexArchiveUrl)) fail(`README is missing immutable archive URL ${codexArchiveUrl}`);

  const generatedMcp = JSON.parse(asText(files.get('.mcp.json')));
  if (generatedMcp.mcpServers?.sbox?.url !== 'http://127.0.0.1:7269/mcp') fail('native MCP URL drifted');
  if (generatedMcp.mcpServers?.sbox?.enabled !== true) fail('native MCP server must be enabled by default');
  if (!generatedMcp.mcpServers?.['sbox-lifeline']?.args?.includes(`sbox-mcp-server@${cm.version}`)) fail('lifeline version must match the plugin version');
  if (generatedMcp.mcpServers?.['sbox-lifeline']?.enabled !== false) fail('lifeline MCP server must be disabled by default');
  if (marketplace.plugins[0].name !== IDENT || marketplace.plugins[0].source.path !== './plugins/sbox-codex-bridge') fail('marketplace path must target this plugin');

  for (const [rel, value] of files) {
    if (!Buffer.isBuffer(value) && value.includes('\r')) fail(`${rel} is not LF-normalized`);
  }
}

validateGenerated();

// ---- emit or check ----
const normalize = (v) => (Buffer.isBuffer(v) ? v : Buffer.from(v, 'utf8'));
const normalizeTextForCompare = (v) => Buffer.from(v.toString('utf8').replace(/\r\n?/g, '\n'), 'utf8');

if (CHECK) {
  let drift = 0;
  const check = (absPath, content) => {
    const want = normalize(content);
    const actual = existsSync(absPath) ? readFileSync(absPath) : null;
    const matches = actual && (
      Buffer.isBuffer(content)
        ? actual.equals(want)
        : normalizeTextForCompare(actual).equals(normalizeTextForCompare(want))
    );
    if (!matches) {
      console.error('DRIFT:', relative(ROOT, absPath));
      drift++;
    }
  };
  for (const [rel, content] of files) check(join(DST, rel), content);
  check(join(ROOT, marketplaceRel), marketplaceContent);
  if (existsSync(LEGACY_DST)) {
    console.error('STRAY: plugins/sbox-codex (obsolete generated prototype; regenerate to migrate)');
    drift++;
  }
  // also flag stray files in DST that the generator no longer produces
  if (existsSync(DST)) {
    for (const rel of walk(DST)) {
      if (!files.has(rel)) { console.error('STRAY:', join('plugins/sbox-codex-bridge', rel)); drift++; }
    }
  }
  if (drift) { console.error(`\n${drift} file(s) out of sync — run: node scripts/gen-codex-plugin.mjs`); process.exit(1); }
  console.log('PASS — Codex plugin in sync with the Claude plugin.');
  process.exit(0);
}

rmSync(DST, { recursive: true, force: true });
rmSync(LEGACY_DST, { recursive: true, force: true });
let n = 0;
for (const [rel, content] of files) {
  const abs = join(DST, rel);
  mkdirSync(dirname(abs), { recursive: true });
  writeFileSync(abs, normalize(content));
  n++;
}
mkdirSync(dirname(join(ROOT, marketplaceRel)), { recursive: true });
writeFileSync(join(ROOT, marketplaceRel), marketplaceContent);
console.log(`Generated plugins/sbox-codex-bridge/ (${n} files) + ${marketplaceRel} — plugin: ${IDENT} @ ${ver}`);
