# FAQ

Plain answers about the v2.0.0 "Native" relaunch. For the full story see the
[relaunch overview](RELAUNCH.md); for the tool tour see [ECOSYSTEM.md](ECOSYSTEM.md).

---

### What is this relaunch?

v2.0.0 moves the s&box Claude Bridge onto **s&box's own native MCP server** — the one the
editor now ships and runs by default at `http://127.0.0.1:7269/mcp`. Instead of a Node
process shuttling JSON files through a temp directory (the v1.x way), the bridge's tools are
now `[McpTool]` methods the engine discovers itself. You get streamable HTTP instead of
50 ms file polling, **inline screenshots** instead of temp-file paths, **live tool discovery**
via `search_tools`, permission-free reads, and real error messages. Same craft, better plumbing.

---

### Do my old tools and workflows still work?

**Yes.** Tool names are **unchanged** from v1.x. Anything you (or an agent) called before —
`create_gameobject`, `take_screenshot`, `create_health_system`, `playtest` — is called by the
same name; it's just invoked through the native server's `call_tool` now instead of over file
IPC.

The one wrinkle: **six tools were dropped from the bridge surface** because the native server
ships built-ins with the *same names and semantics* —
`spawn_model`, `list_scenes`, `save_scene`, `undo`, `redo`, `remove_component`. Your workflows
keep the same names; Facepunch's implementations serve them. (All six also still work over the
legacy file-IPC path until v2.1.0.) Full details in [V2-MIGRATION.md](V2-MIGRATION.md).

---

### What is MCP / the "native server"?

**MCP** (Model Context Protocol) is the open standard Claude Code uses to talk to tools. A
**server** exposes a set of tools; the client (Claude Code) discovers and calls them.

The **native server** is the MCP server **Facepunch built into the s&box editor**. It's on by
default (**Editor → Preferences → MCP Server**), loopback-only (`127.0.0.1`), and hosts the
transport itself — which is why v2 no longer needs the sandbox-blocked sockets workaround the
old file-IPC transport existed for. The bridge addon plugs its tools into that server; you
connect Claude Code to it with one `claude mcp add` command.

---

### Do I need to be technical?

No. You install the addon (one click from the s&box Library Manager), run one connect command
(or install the plugin, which does it for you), and then **talk to Claude Code in plain
language** — "make a horror level," "add a car I can drive," "give me an inventory system."
The agent handles the tools, the C#, the scenes, and screenshots its own work to check it. See
the [Quickstart in the README](../README.md#quickstart--your-first-5-minutes) and
[INSTALL.md](../INSTALL.md). Node.js isn't required on the main path — only for the optional
lifeline.

---

### Can an AI agent modify my project? What stops it breaking things?

**Yes, it can modify your project — that's the point** — and v2 has a real set of protections
so it does so safely:

- **Read-only hints.** 53 tools are marked `[McpTool.ReadOnly]` — inspection, search, lints,
  read screenshots. The client can run them without prompting *because they promise not to
  change anything.* Everything that *can* change your project is a mutating tool and prompts.
- **Play-mode guards.** Scene-mutating tools **refuse while the game is running**, with a clear
  error, so an edit can't get lost against a live runtime scene.
- **Dry-runs.** The batch tools take `dryRun: true` and report exactly what *would* change,
  across every target, without touching anything — you (or the agent) confirm the plan first.
  `batch_delete` is destructive and not undoable, so dry-run is the discipline there.
- **Scene checkpoints.** `checkpoint_scene` snapshots the whole scene; `restore_checkpoint`
  rolls it back. Checkpoint before risky work, undo it if it goes wrong. (And your scene *file*
  isn't changed until you `save_scene`.)
- **Real errors.** Failures throw readable messages instead of hiding inside a "successful"
  response — so a broken step is obvious, not silent.

The honest caveat: there is **no engine auto-undo** for bridge mutations (the engine's snapshot
APIs are inert to addons), which is exactly why checkpoints and saving early matter. See the
[Agent Guide](AGENT-GUIDE.md) for how a well-behaved agent uses all of this.

---

### How do I know what tools exist?

Three ways, from most to least live:

1. **Ask the agent** — it finds tools with `search_tools "<what you want>"` and browses groups
   with `list_toolsets` / `describe_toolset`. You never need the list yourself.
2. **[TOOLSETS.md](TOOLSETS.md)** — the generated, authoritative inventory: every tool, its
   toolset, and whether it's read-only. This file is regenerated from the addon, so it never
   drifts.
3. **[ECOSYSTEM.md](ECOSYSTEM.md)** — the plain-English companion: what each of the 28 toolsets
   is for, example use cases, and how they fit together.

---

### How do I add a tool?

A new tool in v2 is **one static `[McpTool]` method + XML docs** in the addon — no TS module,
no schema file, no npm publish, no parity audit. The engine discovers it on hotload and agents
find it via `search_tools`. The full recipe (template, checklist, naming rules, and the
documentation standards a good tool description must meet) is in
**[ADDING-A-TOOL.md](ADDING-A-TOOL.md)**.

---

### What's coming next?

Planned for **v2.1.0** and beyond (tracked in [TOOL_BACKLOG.md](TOOL_BACKLOG.md)) — none of
this is in v2.0.0 yet:

- **Retiring the legacy path** — the v1.x file-IPC transport and the full stdio TS tool layer
  (everything except the lifeline) are compiled-in as a fallback through v2.0.x and retire in
  v2.1.0.
- **Remaining Tier-2 scaffolds** from the mined 51-game backlog.
- **A loopback multiplayer test harness** — spawn N clients, drive each via `playtest`, assert
  sync. Blocked on the engine shipping its loopback socket.
- **Typed DTO returns** for hot read tools, so agents plan around fields instead of parsing text.
- **`MovieRecorder`** — record-gameplay-to-clip (the MovieMaker *playback* family already
  shipped in v1.20.0; recording is the piece still missing).

---

### What happens to the old npx server?

It doesn't disappear — it becomes the **lifeline**. Run it as a slim second server
(`npx -y sbox-mcp-server@2 --lifeline`) and it exposes the **7 editor-down tools** (`read_log`,
`get_compile_errors`, `search_docs`, `get_doc_page`, `list_doc_categories`, `run_self_test`,
`get_bridge_status`).

Why keep it? Because the native server is hosted **inside the editor and dies with it** — when
s&box crashes or hangs, port 7269 goes silent and nothing there can tell you why. The lifeline
runs *outside* the editor, so it still answers "why did the editor crash." **Fallback now,
lifeline forever.** The plugin wires both servers for you; installing it manually is one extra
`claude mcp add` (see [INSTALL.md](../INSTALL.md) step 4).

---

### The port isn't answering / tools are missing — where do I look?

[TROUBLESHOOTING.md](TROUBLESHOOTING.md) covers the v2 failure modes (port 7269 not answering,
stale HTTP.sys registration, bridge tools missing from `search_tools`, external addon edits not
recompiling, play-mode refusals, modal-dialog stalls). For engine limitations you work *around*
rather than fix (input synthesis, asset shadowing, Razor quirks, GPU stalls), see
[BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md).
