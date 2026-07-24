# s&box Codex Bridge — first-party Codex support

**Date:** 2026-07-03
**Status:** implemented for the v2.1.0 Codex distribution on 2026-07-15
**Author:** sboxskins.gg

## Goal

Ship **official, first-party Codex CLI support** for the s&box Bridge from our own
codebase — not a fork of `LatterDay/Sbox-Codex`. Because the MCP server and the s&box
editor addon are client-agnostic, this is a **packaging + docs** effort, not a rewrite.
Competitive thesis: one codebase serving both Claude Code and Codex means our Codex
support stays current without hard-coding a tool count that changes release to release.

## The three decisions

1. **Same repo, not separate.** Add `plugins/sbox-codex-bridge/` alongside `plugins/sbox-claude/`
   in `LouSputthole/Sbox-Claude`. The MCP server (`sbox-mcp-server`) and the s&box addon are
   shared and unchanged. Rationale: zero drift, single source of truth, every tool update ships
   to both clients at once. (A separate repo would recreate exactly the lag/drift problem the
   competing fork has.)
2. **Name:** display name **"s&box Codex Bridge"** (by sboxskins.gg — our product family);
   plugin ident **`sbox-codex-bridge`** (deliberately distinct from the fork's `sbox-codex` to
   avoid a marketplace/install collision); Codex marketplace name **`sboxskins`**.
3. **Divergence: near-identical twins.** The Codex plugin is *generated* from the canonical
   Claude plugin. Only genuinely client-specific surface differs: install commands,
   Codex MCP wrapper names, skill invocations, and the Codex plugin-manifest/marketplace formats.
   Runtime behavior lives in skills because a plugin-root `AGENTS.md` does not govern a
   consuming project.

## Codex integration surface (verified against developers.openai.com/codex)

- **MCP:** the plugin bundles native streamable HTTP at `http://127.0.0.1:7269/mcp` plus
  a pinned stdio lifeline. MCP-only fallback is `codex mcp add sbox --url <url>`.
- **Skills:** `SKILL.md` uses `name` and `description` frontmatter; each skill also ships
  `agents/openai.yaml` display metadata and an explicit `$skill-name` default prompt.
- **Instructions:** host-specific runtime instructions are normal skills. Project-level
  `AGENTS.md` remains consumer-owned context.
- **Plugin:** `.codex-plugin/plugin.json` (name/version/description + `skills`, `mcpServers`,
  `apps`, `hooks` pointers + `interface{}` presentation block + author/license/keywords).
  Marketplace manifest at `.agents/plugins/marketplace.json` with a repo-local source. Installed via
  `codex plugin marketplace add LouSputthole/Sbox-Claude --ref main` followed by
  `codex plugin add sbox-codex-bridge@sboxskins`.

## Architecture

```
LouSputthole/Sbox-Claude
├── sbox-mcp-server/            # SHARED, unchanged — client-agnostic MCP server
├── sbox-bridge-addon/ (addon)  # SHARED, unchanged — s&box editor Library
├── plugins/
│   ├── sbox-claude/            # existing Claude Code plugin (canonical source)
│   └── sbox-codex-bridge/      # GENERATED, do not hand-edit
│       ├── .codex-plugin/plugin.json
│       ├── .mcp.json           # sbox → npx sbox-mcp-server@<ver>
│       ├── README.md
│       ├── LICENSE / NOTICE / THIRD_PARTY_NOTICES.md
│       └── skills/…            # brain, cookbook, workflows, setup, specialist
├── .agents/plugins/marketplace.json   # Codex marketplace → local plugin snapshot
└── scripts/gen-codex-plugin.mjs       # generator and semantic release gates
```

### Generator (`scripts/gen-codex-plugin.mjs`)

Deterministically produces `plugins/sbox-codex-bridge/` from `plugins/sbox-claude/`:

- **Skills:** copy the source tree; preserve the technical knowledge while normalizing local-only
  provenance paths. For discoverable instructions apply a **protected token map**:
  - protect `Claude Bridge` and `claudebridge` (shared addon — must NOT be renamed)
  - `Claude Code` → `Codex`; standalone `Claude` → `Codex`
  - `claude mcp add` → `codex mcp add`; `CLAUDE.md` → `AGENTS.md`; `.claude/skills` → `.agents/skills`
  - external Claude skill calls become bundled `$sbox-*` skills
  - live bridge calls use Codex's `mcp__sbox__search_tools` / `call_tool` wrapper model
- **Manifest:** read `.claude-plugin/plugin.json`, emit `.codex-plugin/plugin.json` with the Codex
  `interface{}` block, `sbox-codex-bridge` ident, source-available license, sboxskins.gg author.
- **.mcp.json / README / legal notices:** emit self-contained Codex variants.
- **Specialist:** convert the Claude-only Markdown agent into the supported `sbox-game-dev` skill.
- **Release gates:** reject unsupported Claude syntax, local drive paths, overlong descriptions,
  MCP version drift, non-LF text, a folder/manifest mismatch, or the obsolete output directory.

Re-running the generator must produce a clean `git diff` (parity), mirroring the existing
`scripts/audit-parity.mjs` discipline.

## Licensing / branding

Same **source-available (no-redistribution)** license as the rest of the repo. "s&box Codex Bridge"
and "sboxskins.gg" are reserved marks. No content from the competing fork is used (clean-room:
generated from *our* Claude plugin).

## Verification & honest limits

Verifiable here: generator runs deterministically, all JSON valid, skills present with correct
frontmatter, `sbox-mcp-server` still builds, existing parity green, Claude plugin untouched.

The Codex CLI can validate the repo marketplace and local install path. A live end-to-end bridge
call and screenshot still require the s&box editor plus addon to be running in a fresh Codex session.

## Out of scope (this iteration)

- Renaming the shared s&box addon dock ("Claude Bridge") — it stays shared; Codex docs note it.
- Publishing to OpenAI's curated directory (needs the user's account).
- Publishing to a public marketplace channel or pushing the release branch.
