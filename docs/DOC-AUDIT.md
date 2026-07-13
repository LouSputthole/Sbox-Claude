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

Current numbers all docs should agree on (v2.0.0 + the v2.1.0 Tier-2 +
gameplay-recording wave (2026-07-12) + the cinematic wave, Batches 61–63 (2026-07-13)):
**262 native tools / 28 toolsets / 53 read-only / 7 lifeline / 275 total / 267 handlers.**
(The published v2.0.0 release was 232/245/237 —
docs that explicitly label a number "the v2.0.0 surface", like CONTRIBUTING/TESTING, are
correct to keep it; everything describing the *current* surface uses the new totals.)

---

## Root docs

| Doc | Status | Audience | Purpose | Notes |
|---|---|---|---|---|
| `README.md` | **updated** | public | Front door: what the bridge is, v2 highlights, install, toolset table, quickstart. | Added the v2 relaunch banner + links (RELAUNCH/AGENT-GUIDE/ECOSYSTEM/FAQ) and fixed a stale "26 toolsets" → 28. Counts now consistent at final numbers. |
| `INSTALL.md` | **updated** | public | v2 install guide (addon + native server + optional lifeline). | Added a "Next steps" block linking the four new docs. Already v2-accurate; no count fixes needed. |
| `CHANGELOG.md` | current | public | Full release history; `[2.1.0]` is the staged Tier-2 + recording wave, `[2.0.0]` the relaunch record. | `[2.0.0]` carries a final-totals reconciliation note above its wave-snapshot counts (219 → 223 → 233 were in-migration figures; the note pins 245/237). `[2.1.0]` heading is skipped by the parity gate's version lock. |
| `CLAUDE.md` | current | contributor / internal | Architecture, verified s&box APIs, lessons learned. | **Rewritten for v2** (native transport as primary, file IPC as legacy fallback). Header carries the v2.1.0 counts (262/275/267) and distinguishes them from the published v2.0.0 (245/237). |
| `CONTRIBUTING.md` | current | contributor | How to contribute / add a tool. | **Rewritten for v2**: one-`[McpTool]`-method flow, defers to `docs/ADDING-A-TOOL.md`; the TS server described in its two shrinking roles (lifeline + legacy fallback). Cites the surface explicitly labeled "v2.0.0" (232/245/237) — correct as a published-release label. |
| `TROUBLESHOOTING.md` (root) | current | public | Legacy **file-IPC** transport troubleshooting. | Correctly scoped as the legacy-fallback doc; `docs/TROUBLESHOOTING.md` links to it as such. Accurate for the v2.0.x fallback path (retires v2.1.0). |
| `TESTING.md` | current | contributor / internal | Test plan (v2, native MCP). | **Rewritten for v2**: automated gates + native-server smoke path (`search_tools` / `call_tool` / inline images); the v1 file-IPC plan explicitly retired. Cites the surface labeled "v2.0.0" (232/245/237) + `handlerCount` = 237 — bump both when `[2.1.0]` publishes (→ 262/275/267). |
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
| `docs/TOOLSETS.md` | generated | public / agent | The authoritative tool inventory (262 tools / 28 toolsets / 53 read-only). | **Do not hand-edit** (emitted by `scripts/emit-mcp-wrappers.mjs`). Header numbers are the source of truth; regenerated for v2.1.0 (incl. the cinematic wave). |
| `docs/V2-MIGRATION.md` | **updated** | public | v1.x → v2.0.0 upgrade guide (transport, six built-in replacements, invocation pattern). | Added a RELAUNCH link, a "New tools in the v2 surface (waves 1-4)" section, and fixed a stale "25 described toolsets" → 28. |
| `docs/ADDING-A-TOOL.md` | **updated** | contributor | The new-tool factory: template, checklist, naming, regeneration. | Added a **"Documentation standards"** section (5-point `<summary>` rules, param-doc rules, and a good-vs-bad `find_objects` description contrast). |
| `docs/TROUBLESHOOTING.md` | **updated** | public | v2 native-transport failure modes (port 7269, stale HTTP.sys, missing tools, recompile, play-mode, modal stalls). | Added RELAUNCH/FAQ/INSTALL to the companion-docs list. v2-accurate; tool count refreshed to 262 in the 2026-07-13 sweep. |
| `docs/BRIDGE_GOTCHAS.md` | **updated** | public / agent | Engine limitations you work *around*, not fix (input synthesis, asset shadowing, Razor quirks, whitelist, GPU stalls). | v2-aware (native-server phrasing, `restart_editor` over the native server, gotcha #9 on the Libraries file-watcher). 2026-07-12 sweep added §6e + §§10–16 (describe_type static-blindness, Services API shapes, `[AssetType]`, MovieRecorder auto-advance, editor-context JSON, LipSync-vs-SoundHandle, SDK camera-effect additions) + quick-reference rows. 2026-07-13 official-docs audit corrected §11/§13/§15 (`hasDefault` overstatements, the WithCaptureAll misdiagnosis, `Stats.LocalPlayer`), folded the `hasDefault` meta-gotcha into §10, and added §17 (transient hotload MovieRecorder corruption). |

---

## `docs/` — internal, historical & generated

| Doc | Status | Audience | Purpose | Notes |
|---|---|---|---|---|
| `docs/TOOL_BACKLOG.md` | current | internal | Roadmap mined from the 51-game corpus; built/remaining status by tier + engine-watch. | Updated through the staged `[2.1.0]` wave (2026-07-12): built-in-[2.1.0] section, theme-table ✅s, deliberately-skipped list, engine-watch (MovieRecorder ✅ covered; loopback re-verified absent; offline lipsync partially addressed). |
| `docs/asset-library-listing.md` | **updated** | public / marketing | s&box Asset Library store listing copy. | Counts refreshed to the v2.1.0 totals (262/275/267) in the 2026-07-13 sweep. The per-toolset "Full tool list" remains the v2.0.0 snapshot (noted inline; TOOLSETS.md is authoritative) — refresh it when the release publishes. |
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

## Needs-review summary

Resolved since the v2.0.0 pass (as of the 2026-07-12 `[2.1.0]` docs sweep):

1. ~~**`CLAUDE.md`**~~ — ✅ rewritten for v2 (native-primary architecture, staged working-tree
   counts, file IPC as legacy fallback).
2. ~~**`CONTRIBUTING.md`**~~ — ✅ rewritten: one-`[McpTool]`-method flow, defers to
   `docs/ADDING-A-TOOL.md`.
3. ~~**`TESTING.md`**~~ — ✅ rewritten: native-server gates + smoke path; v1 file-IPC plan retired.
4. ~~**`CHANGELOG.md` `[2.0.0]`**~~ — ✅ carries a final-totals reconciliation note above the
   wave-snapshot counts.
5. ~~**`docs/asset-library-listing.md`**~~ — ✅ counts refreshed to the v2.1.0 totals.

Still open:

6. **`NEW_TOOLS_HANDOFF.md`** — self-deprecated; consider deleting.
7. **Minor:** `docs/asset-library-listing.md`'s per-toolset "Full tool list" and
   `TESTING.md`'s "v2.0.0 surface" numbers are published-release snapshots — bump both when
   `[2.1.0]` publishes; regenerate `docs/graph/GRAPH_REPORT.md` at release per CLAUDE.md.
