# s&box Codex Bridge — first-party Codex support

**Date:** 2026-07-03
**Status:** approved (autonomous build — "loop until complete")
**Author:** sboxskins.gg (+ Claude)

## Goal

Ship **official, first-party Codex CLI support** for the s&box Bridge from our own
codebase — not a fork of `LatterDay/Sbox-Codex`. Because the MCP server and the s&box
editor addon are client-agnostic, this is a **packaging + docs** effort, not a rewrite.
Competitive thesis: one codebase serving both Claude Code and Codex means our Codex
support is always current with our 206 tools, while a downstream fork always lags.

## The three decisions

1. **Same repo, not separate.** Add `plugins/sbox-codex/` alongside `plugins/sbox-claude/`
   in `LouSputthole/Sbox-Claude`. The MCP server (`sbox-mcp-server`) and the s&box addon are
   shared and unchanged. Rationale: zero drift, single source of truth, every tool update ships
   to both clients at once. (A separate repo would recreate exactly the lag/drift problem the
   competing fork has.)
2. **Name:** display name **"s&box Codex Bridge"** (by sboxskins.gg — our product family);
   plugin ident **`sbox-codex-bridge`** (deliberately distinct from the fork's `sbox-codex` to
   avoid a marketplace/install collision); Codex marketplace name **`sboxskins`**.
3. **Divergence: near-identical twins.** The Codex plugin is *generated* from the canonical
   Claude plugin. Only genuinely client-specific surface differs: install commands
   (`codex mcp add` / `codex plugin marketplace add`), skill discovery path (`.agents/skills`),
   `AGENTS.md` vs `CLAUDE.md`, and the Codex plugin-manifest/marketplace formats.

## Codex integration surface (verified against developers.openai.com/codex)

- **MCP:** Codex reads `~/.codex/config.toml` `[mcp_servers.<name>]` (command/args/env/timeouts),
  or `codex mcp add sbox -- npx -y sbox-mcp-server@latest`. **No server changes needed.**
- **Skills:** `SKILL.md` (frontmatter `name` + `description`) discovered from `.agents/skills/`
  (repo/user/system). Same SKILL.md format as Claude. Skill-list budget ≤ 8 000 chars / 2% context.
- **Instructions:** `AGENTS.md` (Codex's `CLAUDE.md` equivalent).
- **Plugin:** `.codex-plugin/plugin.json` (name/version/description + `skills`, `mcpServers`,
  `apps`, `hooks` pointers + `interface{}` presentation block + author/license/keywords).
  Marketplace manifest at `.agents/plugins/marketplace.json` (git-subdir source). Installed via
  `codex plugin marketplace add LouSputthole/Sbox-Claude` → `codex plugin add sbox-codex-bridge`.

## Architecture

```
LouSputthole/Sbox-Claude
├── sbox-mcp-server/            # SHARED, unchanged — client-agnostic MCP server
├── sbox-bridge-addon/ (addon)  # SHARED, unchanged — s&box editor Library
├── plugins/
│   ├── sbox-claude/            # existing Claude Code plugin (canonical source)
│   └── sbox-codex/             # NEW — GENERATED, do not hand-edit
│       ├── .codex-plugin/plugin.json
│       ├── .mcp.json           # sbox → npx sbox-mcp-server@<ver>
│       ├── README.md
│       ├── AGENTS.md           # Codex onboarding guidance
│       ├── agents/…            # ported
│       └── skills/…            # ported (cookbook verbatim; 4 skills token-mapped)
├── .agents/plugins/marketplace.json   # NEW — Codex marketplace (git-subdir → ./plugins/sbox-codex)
└── scripts/gen-codex-plugin.mjs       # NEW — the generator (single source of truth)
```

### Generator (`scripts/gen-codex-plugin.mjs`)

Deterministically produces `plugins/sbox-codex/` from `plugins/sbox-claude/`:

- **Skills:** copy the tree; `sbox-cookbook` + all `references/` copied **verbatim** (client-neutral
  s&box knowledge, 0 client mentions). For the other skills apply a **protected token map**:
  - protect `Claude Bridge` and `claudebridge` (shared addon — must NOT be renamed)
  - `Claude Code` → `Codex`; standalone `Claude` → `Codex`
  - `claude mcp add` → `codex mcp add`; `CLAUDE.md` → `AGENTS.md`; `.claude/skills` → `.agents/skills`
  - `/plugin marketplace add …` / `/plugin install …` → `codex plugin …` equivalents
- **Manifest:** read `.claude-plugin/plugin.json`, emit `.codex-plugin/plugin.json` with the Codex
  `interface{}` block, `sbox-codex-bridge` ident, source-available license, sboxskins.gg author.
- **.mcp.json / README / AGENTS.md:** emit Codex variants.

Re-running the generator must produce a clean `git diff` (parity), mirroring the existing
`scripts/audit-parity.mjs` discipline.

## Licensing / branding

Same **source-available (no-redistribution)** license as the rest of the repo. "s&box Codex Bridge"
and "sboxskins.gg" are reserved marks. No content from the competing fork is used (clean-room:
generated from *our* Claude plugin).

## Verification & honest limits

Verifiable here: generator runs deterministically, all JSON valid, skills present with correct
frontmatter, `sbox-mcp-server` still builds, existing parity green, Claude plugin untouched.

**Cannot** verify here (no Codex CLI + no s&box editor in this environment): a real
`codex plugin marketplace add` install and a live end-to-end bridge call. **User gate:** run the
Codex install path once against a real project, and publish to a Codex marketplace channel.

## Out of scope (this iteration)

- Renaming the shared s&box addon dock ("Claude Bridge") — it stays shared; Codex docs note it.
- Publishing to OpenAI's curated directory (needs the user's account).
- npm version bump (tracked separately; do on next publish → 1.19.0).
