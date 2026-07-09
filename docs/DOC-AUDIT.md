# Documentation Audit — v2.0.0 "Native" relaunch

Every Markdown doc in the repo (excluding `node_modules/`), its status after the v2.0.0
documentation pass, who it's for, what it's for, and anything stale or missing I found but
**did not** fix (out of scope, protected file, or another owner's lane).

**Legend — Status:** `new` (created this pass) · `updated` (edited this pass) · `current`
(accurate as-is, not edited) · `generated` (machine-emitted, never hand-edited) · `historical`
(point-in-time record, intentionally frozen) · `stale` (drifted from v2 reality — needs a pass)
· `deprecated` (self-superseded).
**Audience:** `public` (creators/users) · `agent` (AI operating the bridge) · `contributor`
(building the bridge) · `internal` (maintainer notes / planning).

Final v2.0.0 numbers all docs should agree on: **232 native tools / 28 toolsets / 53 read-only /
7 lifeline / 245 total / 237 handlers.**

---

## Root docs

| Doc | Status | Audience | Purpose | Notes |
|---|---|---|---|---|
| `README.md` | **updated** | public | Front door: what the bridge is, v2 highlights, install, toolset table, quickstart. | Added the v2 relaunch banner + links (RELAUNCH/AGENT-GUIDE/ECOSYSTEM/FAQ) and fixed a stale "26 toolsets" → 28. Counts now consistent at final numbers. |
| `INSTALL.md` | **updated** | public | v2 install guide (addon + native server + optional lifeline). | Added a "Next steps" block linking the four new docs. Already v2-accurate; no count fixes needed. |
| `CHANGELOG.md` | current | public | Full release history; `[2.0.0]` is the feature record. | **Needs review:** the `[2.0.0]` prose cites in-migration handler counts (219 → 223 → 233) that don't reconcile to the final **237 handlers / 245 total**. Left as ground-truth per instructions; a maintainer should add a final-count reconciliation line. |
| `CLAUDE.md` | **stale** | contributor / internal | Architecture, verified s&box APIs, lessons learned. | **Needs review (biggest gap):** still headed **v1.20.0 / 219 handlers / 228 tools**; the "What's new" ladder stops at v1.20.0; the architecture section presents **file IPC as the working transport** (now the legacy fallback). Out of my update scope — flagged for a dedicated v2 rewrite. |
| `CONTRIBUTING.md` | **stale** | contributor | How to contribute / add a tool. | **Needs review:** describes the **v1 two-part tool flow** (TS `server.tool()` + C# handler over file IPC) as *the* way. v2's model is one `[McpTool]` method — see `docs/ADDING-A-TOOL.md`. Should point at ADDING-A-TOOL and describe the native flow. |
| `TROUBLESHOOTING.md` (root) | current | public | Legacy **file-IPC** transport troubleshooting. | Correctly scoped as the legacy-fallback doc; `docs/TROUBLESHOOTING.md` links to it as such. Accurate for the v2.0.x fallback path (retires v2.1.0). |
| `TESTING.md` | **stale** | contributor / internal | Manual smoke-test plan. | **Needs review:** written for the file-IPC surface (`[SboxBridge] … registered` console check, v1.4/v1.5 batch references, handler-count language). No native-server (`search_tools` / `call_tool` / inline-image) test path. |
| `NEW_TOOLS_HANDOFF.md` | **deprecated** | internal | Old tool-handoff note. | Self-marked "superseded"; points at v1.12.0 state. Candidate for deletion — a maintainer call, not fixed here. |
| `REVIEW-2026-06-09.md` | historical | internal | Point-in-time health/gap review (post-v1.11.0). | Frozen snapshot; fine to keep as history. Not stale in the "needs fixing" sense — just dated. |

---

## `docs/` — public & agent

| Doc | Status | Audience | Purpose | Notes |
|---|---|---|---|---|
| `docs/RELAUNCH.md` | **new** | public | The relaunch hub: what v2 is, why the rebuild (transport/discovery/honesty/safety), concrete wins, the working loop, roadmap, honest limits. | Hub page; links every other doc. |
| `docs/AGENT-GUIDE.md` | **new** | agent | How an agent works the platform: discovery, the inspect→checkpoint→modify→validate→test loop, read-only vs mutating, dry-runs, error/chaining semantics, lifeline, 4 worked examples. | Grounded in real tool names from TOOLSETS.md. |
| `docs/ECOSYSTEM.md` | **new** | public / agent | Plain-English tour of all 28 `bridge_*` toolsets + the 7 lifeline tools: purpose, use cases, example prompt, safety note, related toolsets. | Companion to the generated inventory; defers to TOOLSETS.md on conflict. |
| `docs/FAQ.md` | **new** | public | Practical Q&A: what's the relaunch, do old workflows work, what's MCP, can an agent modify my project, how to add a tool, what's next, the old npx server. | — |
| `docs/DOC-AUDIT.md` | **new** | internal | This file. | — |
| `docs/TOOLSETS.md` | generated | public / agent | The authoritative tool inventory (232 tools / 28 toolsets / 53 read-only). | **Do not hand-edit** (emitted by `scripts/emit-mcp-wrappers.mjs`). Header numbers are the source of truth. |
| `docs/V2-MIGRATION.md` | **updated** | public | v1.x → v2.0.0 upgrade guide (transport, six built-in replacements, invocation pattern). | Added a RELAUNCH link, a "New tools in the v2 surface (waves 1-4)" section, and fixed a stale "25 described toolsets" → 28. |
| `docs/ADDING-A-TOOL.md` | **updated** | contributor | The new-tool factory: template, checklist, naming, regeneration. | Added a **"Documentation standards"** section (5-point `<summary>` rules, param-doc rules, and a good-vs-bad `find_objects` description contrast). |
| `docs/TROUBLESHOOTING.md` | **updated** | public | v2 native-transport failure modes (port 7269, stale HTTP.sys, missing tools, recompile, play-mode, modal stalls). | Added RELAUNCH/FAQ/INSTALL to the companion-docs list. Already v2-accurate; cites the final 232-tool count. |
| `docs/BRIDGE_GOTCHAS.md` | current | public / agent | Engine limitations you work *around*, not fix (input synthesis, asset shadowing, Razor quirks, whitelist, GPU stalls). | v2-aware (native-server phrasing, `restart_editor` over the native server, gotcha #9 on the Libraries file-watcher). Accurate. |

---

## `docs/` — internal, historical & generated

| Doc | Status | Audience | Purpose | Notes |
|---|---|---|---|---|
| `docs/TOOL_BACKLOG.md` | current | internal | Roadmap mined from the 51-game corpus; built/remaining status by tier + engine-watch. | Updated through v2.0.0 (has a v2.0.0-built section + v2.1.0 next). Minor drift: a v2.0.0 line reads "handler count now 233" vs the final 237 — cosmetic, not fixed. |
| `docs/asset-library-listing.md` | **stale?** | public / marketing | s&box Asset Library store listing copy. | **Needs review:** likely carries pre-v2 tool counts / feature framing. Marketing-flavored and not under `docs/marketing/`; I did **not** edit it (adjacent to the marketing agent's lane) — flag for a v2 refresh by whoever owns store copy. |
| `docs/blog-v1.9.0.md` | historical | public | v1.9.0 launch blog post. | Frozen marketing artifact for an old release. Keep as history. |
| `docs/release-notes-1.12.0.md` | historical | public | v1.12.0 release notes. | Point-in-time; superseded by CHANGELOG. |
| `docs/release-notes-1.13.0.md` | historical | public | v1.13.0 release notes. | Point-in-time; superseded by CHANGELOG. |
| `docs/engine-requests.md` | internal | internal | Engine feature requests to Facepunch. | Maintainer notes. |
| `docs/engine-requests-submission.md` | internal | internal | Submission draft of the above. | Maintainer notes. |
| `docs/graph/README.md` | current | contributor | How to read/regenerate the bridge knowledge graph. | Graph regen is a documented release step. |
| `docs/graph/GRAPH_REPORT.md` | generated | internal | Generated knowledge-graph report. | Can go stale between regens (check its date); regenerate at release per CLAUDE.md. |
| `docs/plans/2026-07-08-native-mcp-migration.md` | internal | internal | **The v2.0.0 architecture + verification plan.** | Primary ground-truth for the migration. **Protected — not edited** (per instructions, nothing under `docs/plans/`). Interior counts are in-progress snapshots (222/26/235), superseded by the final numbers. |
| `docs/plans/2026-07-08-v1.20.0-directors-cut.md` | internal | internal | v1.20.0 planning. | Protected plan; historical. |
| `docs/plans/2026-06-20-playtest-harness.md` | internal | internal | Playtest-harness planning. | Protected plan; historical. |
| `docs/plans/2026-06-17-unity-carryover-meta-tools.md` | internal | internal | Unity carry-over planning. | Protected plan; historical. |
| `docs/plans/2026-06-09-next-10-tools.md` | internal | internal | 10-tool wave planning. | Protected plan; historical. |
| `docs/plans/2026-06-09-roadmap-mockups.md` | internal | internal | Roadmap mockups. | Protected plan; historical. |
| `docs/plans/2026-06-02-wave1-visual-atmosphere.md` | internal | internal | Visual/atmosphere wave planning. | Protected plan; historical. |
| `docs/superpowers/specs/2026-06-04-playable-game-scaffolds-design.md` | internal | internal | Scaffold design spec. | Design record; historical. |
| `docs/superpowers/specs/2026-06-04-reliability-polish-design.md` | internal | internal | Reliability/polish design spec. | Design record; historical. |
| `docs/superpowers/specs/2026-06-04-npc-brains-design.md` | internal | internal | NPC-brains design spec. | Design record; historical. |

---

## Out of lane (noted, not audited/edited)

- **`docs/marketing/`** — owned by the marketing agent; not created or touched.
- **`plugins/sbox-claude/README.md`** — plugin-scoped docs (referenced from the root README).
  Not in the bridge-docs lane; a maintainer should confirm it reflects the v2 dual-server
  `.mcp.json` and the native invocation pattern.

---

## Needs-review summary (found, not fixed)

Priority order for a follow-up docs pass:

1. **`CLAUDE.md`** — still v1.20.0 throughout (counts, "what's new" ladder, file-IPC-as-primary
   architecture). The single biggest stale doc; wants a dedicated v2 rewrite.
2. **`CONTRIBUTING.md`** — v1 two-part tool flow + file IPC as primary; should defer to
   `docs/ADDING-A-TOOL.md` and describe the native one-method flow.
3. **`TESTING.md`** — no native-server test path; still checks the file-IPC console fingerprint.
4. **`CHANGELOG.md` `[2.0.0]`** — in-migration handler counts (219/223/233) don't reconcile to the
   final 237/245; add a reconciliation line. (Ground-truth file — left for a maintainer.)
5. **`docs/asset-library-listing.md`** — likely pre-v2 counts/framing; refresh store copy (marketing-adjacent).
6. **`NEW_TOOLS_HANDOFF.md`** — self-deprecated; consider deleting.
7. **Minor:** `docs/TOOL_BACKLOG.md` "handler count now 233" and `docs/graph/GRAPH_REPORT.md`
   freshness — cosmetic; fold into the next release's graph regen + backlog update.
