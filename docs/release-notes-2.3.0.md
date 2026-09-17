# s&box Claude Bridge v2.3.0 — Know When Your Code Is Live, and an Editor That Can't Play Dead

**286 tools. 278 editor handlers. Thirteen GitHub issues worked, and three bugs the fixes themselves were hiding.**

v2.3.0 is a hardening release. Issues #10–#23 (twelve now closed) were about the unglamorous
layer: the file-IPC transport losing requests without a word, a frozen editor looking exactly
like a crashed one, agents running stale code after a hotload and never knowing. All of that was written, reviewed,
and green in CI — and then it was run against a real editor for the first time, where three of
the fixes turned out to be wrong in ways no offline gate could see. Those are fixed too, and
this is the first release where every smoke row that could be exercised live was.

---

## Know when your code is actually live

The problem (#15): edit an existing `.cs`, call `trigger_hotload`, call `invoke_button` — and
silently run the *old* assembly, because the recompile is asynchronous and a successful compile
logs nothing. 2.3.0 gives agents a fingerprint: `trigger_hotload` returns `assemblyBefore`,
`get_bridge_status` returns `gameAssembly`, and you poll until the `mvid` changes.

The first cut of that fingerprint read the assembly through the TypeLibrary. Run live, it sat
perfectly still through a method-body edit — while the new code was already executing. s&box's
**fast hotload** loads the new assembly and detours into it but never moves the TypeLibrary's
types, so the most common edit there is (change a method body) was the one case the tool
reported as "nothing happened," with a note telling the agent to restart the editor. A
five-minute restart, for an edit that had landed in under a second.

The fingerprint now follows the highest-`Version` loaded build of the project assembly — every
compile bumps it — and reports `version`, `fastHotloaded`, and `loadedBuilds` alongside the
`mvid`. Verified live through all three shapes: body-only edit (fast hotload, flips in under a
second), structural edit (full swap), and file delete.

The same run caught a second flaw: the project assembly was matched by a `package.local.*`
*prefix*, so for a moment mid-swap the fingerprint belonged to `package.local.menu` — a false
"your recompile landed." It now matches the project ident exactly and remembers the resolved
name across the swap window.

## A frozen editor no longer looks like a crashed one

An "External Changes Detected" dialog blocks the editor's main thread. Every bridge call times
out, the heartbeat goes stale, and from the outside it is indistinguishable from a dead process
(#14). The heartbeat now runs on the addon's poll-timer thread and publishes both clocks:
`heartbeat` (main thread), `processHeartbeat` (poll thread), `mainThreadStalledMs`, and
`blockedBy`. `get_bridge_status` and request-timeout errors say *process alive, main thread
blocked* instead of "not connected."

Honest scope: at steady state the two clocks track within single-digit milliseconds in a live
editor, and the stall diagnostics are covered end to end by a fake-editor test — but this
release was not exercised against a real modal dialog.

## File IPC that doesn't lose things quietly

The legacy transport is still the compatibility fallback and the lifeline's only road in, so it
got the attention it never had:

- **Claimed, not deleted** (#19, #20) — a request is renamed to `req_<id>.json.processing` when
  picked up and removed only after its response is written. A timeout can now say "picked up,
  still executing" vs "never picked up," and a crash mid-handler leaves the request on disk for
  the next session to sweep and log by command.
- **Replays are no-ops** — processed ids are remembered for five minutes. Verified live: the
  same `create_gameobject` request sent twice created exactly one object, and the second was
  logged as ignored.
- **A protocol handshake** (#22) — `protocolVersion` rides on every request and in
  `status.json`; a request from a newer protocol is refused with a sentence a person can act on,
  not a parse error.
- **No silent catches** (#18) — every swallowed exception on the transport path now logs, and an
  unparseable response fails in ~250 ms with its first 200 characters instead of spinning out
  the 30-second timeout.

Upgrade order doesn't matter: the published 2.2.0 server was run against the 2.3.0 addon and
passed the full self-test.

## Plan before you place

Eleven tools that shipped in 2.2.0's source flagged "live smoke pending" are now verified and
released: `dryRun:true` placement plans for `scatter_props`, `place_along_path`, and
`grid_duplicate`, with `commit_placement_plan` for exact, rollback-protected creation and
slot→GUID receipts; `inspect_model_geometry` for model-local bounds and pivot-to-ground
offsets; provenance-rich `get_bounds`; play-aware `find_objects_near`; session camera bookmarks;
`capture_camera_set` with previous-capture RGBA comparison; and orthographic `capture_topdown`.
A dry run against a 1,544-object scene left it at 1,544 objects; the commit produced four of
four receipts.

## Also in this release

- **Assets land where the runtime can load them** (#13) — `create_sound_event`,
  `create_material`, and `create_prefab` resolve relative paths under `Assets/` and return both
  `path` and `assetPath`. A `.sound` written at the project root used to compile, preview, and
  then fail to load at runtime.
- **`instantiate_prefab` no longer clones a stale prefab.** The asset system can still hold the
  previous contents of a path that was just rewritten; a prefab written in the last 10 seconds
  is now read straight from disk.
- **Big scenes fit better** (#16) — `get_scene_hierarchy namesOnly:true` returns a compact tree,
  and `find_objects` reports `total` / `showing` / `truncated` so a capped list can't pass for a
  complete one. Measured, not promised: `namesOnly` was about 30% smaller on a flat 1,544-object
  scene (271 kB vs 391 kB) — pair it with `maxDepth` and `rootId`.
- **`set_property` refuses two Terrain landmines** (#12) — `Terrain.Enabled` and
  `Terrain.ClipMapLodExtentTexels` both kill terrain rendering until a scene reload.
- **`read_log` works off Windows** (#10) — `sbox-dev.log` is auto-detected across Linux and
  macOS Steam libraries, Flatpak and Snap included, with a Proton section in TROUBLESHOOTING.md.
  #10 itself stays open: the native MCP server on port 7269 is the editor's, and under Proton it
  may not come up at all — the file-IPC fallback is the workaround, and Linux reports are wanted.
- **The live gate cleans up after itself.** `verify-native-mcp.mjs` assumed one specific
  project and wrote a stray sound event into any other; it is now self-contained, deletes its
  verify prefab, and runs 41 checks.
- 32 Node tests (was 12), including an in-process fake editor that drives the whole file-IPC
  round trip and a sweep of every registered tool's schema.

## The s&box Codex Bridge

The Codex distribution moves in lockstep — same 286 tools, same native editor MCP server, same
fixes, generated from the Claude plugin so the two can't drift. CI installs it from the live
GitHub marketplace with the current Codex CLI on Windows and Linux for every release.

```
codex plugin marketplace add LouSputthole/Sbox-Claude --ref codex-v2.3.0
codex plugin add sbox-codex-bridge@sboxskins
```

## Upgrading

- **Claude Code:** `claude plugin marketplace update sbox-claude`, update the plugin, restart.
- **Editor addon:** update `sboxskinsgg.claudebridge` via the s&box Library Manager.
- **Lifeline (optional):** the plugin pins `sbox-mcp-server@2.3.0` automatically.

Tool names are unchanged. Nothing was removed. `SBOX_BRIDGE_HOST` / `SBOX_BRIDGE_PORT` are gone,
but they never did anything — there has never been a socket (#23).
