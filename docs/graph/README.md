# Bridge Map — graphify knowledge graph

This folder holds a **source knowledge graph of the s&box Claude Bridge**: a map of how its
code connects. It is regenerated with graphify's AST pass over the C# and TypeScript sources.
The current source-only graph maps types, methods, handlers, calls, and file relationships;
consult the project docs and [TOOLSETS.md](../TOOLSETS.md) for the semantic tool guide.

## What's here

| File | What it is |
|------|------------|
| `graph.json` | The machine-readable graph used by query tools. See `GRAPH_REPORT.md` for the current regenerated node, edge, and community counts. |
| `graph.html` | A self-contained interactive viewer. Open it in a browser to **browse** the graph — pan/zoom, click a node to see what it connects to, communities are colour-coded. |
| `GRAPH_REPORT.md` | A human-readable summary: god nodes (most-connected abstractions), community hubs (navigation), and the extraction audit (EXTRACTED vs INFERRED edges). |

## How to read the map

- **`IBridgeHandler` is the source-level spine.** Editor-side command handlers implement it, so
  it remains the bridge-owned hub for tracing how a command reaches the editor. Use the current
  god-node ranking in `GRAPH_REPORT.md` instead of relying on hard-coded edge counts.
- **Generated wrappers map to their C# handlers and supporting types.** A tool like
  `screenshot_from` can be traced through its wrapper, handler, and shared capture service. The
  source-only graph answers implementation questions; the docs explain intended behavior.
- **Communities are the cross-document edges.** graphify's community detection groups related
  nodes (e.g. the networking handlers, the visuals tools, the changelog fixes) and surfaces
  connections across files you wouldn't think to look for. The hubs are listed at the top of
  `GRAPH_REPORT.md`.
- **`MyEditorMenu.cs` is the routing spine, not the whole implementation.** Handler families are
  split across dedicated files, including asset geometry, camera capture, and placement plans.
  Use communities and file relationships to move from the central registry into each subsystem.

## How to USE it

**Browse it:** open `graph.html` in any browser. No server needed — it's a single self-contained
file. Click around to see what connects to what.

**Query it:** run the graphify query command against `graph.json` to ask questions in natural
language:

```bash
graphify query "what implements screenshot_from"      --graph docs/graph/graph.json
graphify path  "create_player_controller" "IBridgeHandler" --graph docs/graph/graph.json
graphify explain "MyEditorMenu.cs"                    --graph docs/graph/graph.json
```

`query` does a broad BFS traversal (good for "what is X connected to?"); add `--dfs` to trace a
specific chain. `path` finds the shortest path between two concepts; `explain` gives a
plain-language description of one node and its neighbors. (Inside Claude Code you can also just
ask in plain language — the `/graphify` skill treats a question as a query against this graph.)

## ⚠️ This map CAN GO STALE

The graph is a **snapshot**, not a live view. It was generated from the repo at a point in time
(see the date at the top of `GRAPH_REPORT.md`). **Every time tools, handlers, or docs change, it
drifts out of date** — new tools won't appear, removed ones will linger, edge counts will be wrong.

**Check freshness before trusting it:** compare the date in `GRAPH_REPORT.md` against recent
changes. If the graph predates a tool/handler/doc change, regenerate it:

- **Quick, deterministic, no-LLM (code/AST only):** run `scripts/regen-graph.ps1`. This refreshes
  the code structure (handlers, tools, call edges) from the AST. Fast and reproducible — but it
  does **not** re-read the docs.
- **Full doc-inclusive refresh:** re-run the **`/graphify`** skill on the repo. That does the full
  AST + semantic (LLM) pass, so the doc/skill/changelog edges and community labels are rebuilt too.
  This is the authoritative regen and is what maintainers should run as part of a release (see
  `CLAUDE.md` and the `sbox-build-feature` skill).
