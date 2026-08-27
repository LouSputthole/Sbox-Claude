# Testing Guide (v2, native MCP)

The test plan for the s&box Claude Bridge on the **native editor MCP server**
(`http://127.0.0.1:7269/mcp`). Automated gates first, then a short manual smoke path —
not an exhaustive pass over every tool. The surface under test: **273 native tools /
28 `bridge_*` toolsets / 57 read-only / 7 lifeline / 286 total / 278 handlers**
(`get_bridge_status.handlerCount` is the live assembly fingerprint).

> **Use a test project, not a production one.** The live gate and the self-test create and
> delete GameObjects, write a temp prefab (`prefabs/__bridge_verify.prefab`) and temp
> scripts in `Code/`, and trigger recompiles. Everything cleans up after itself, but run it
> where a stray object wouldn't matter.
>
> Looking for the v1 file-IPC test plan (console fingerprint checks, the per-phase tool
> tables)? That exhaustive matrix retired with the v2.0.0 relaunch; the legacy transport itself
> remains a compatibility fallback in current source and is covered by root `TROUBLESHOOTING.md`.

---

## 1. Automated gates (run these first)

| # | Gate | Command | Needs editor? | What it proves |
|---|------|---------|---------------|----------------|
| 1 | Node tests | `cd sbox-mcp-server && npm test` | no | 12 tests over the transport client: heartbeat-staleness classification, timeout diagnostics (which side stalled), `SBOX_BRIDGE_IPC_DIR` override, `isConnected` false-positive regression |
| 2 | Quality gate | `node scripts/audit-mcp-quality.mjs` | no | no tool-name collisions with native built-ins (collisions are SILENT tool loss — hard fail) + description quality (5-point summary, param docs) |
| 3 | Parity gate | `node scripts/audit-parity.mjs` | no | TS tools ↔ C# handlers parity, every concrete `IBridgeHandler` has a registration factory, and the 4-way version lock holds while the compatibility fallback ships |
| 4 | Codegen freshness | `node scripts/extract-manifest.mjs && node scripts/emit-mcp-wrappers.mjs && git diff --exit-code -- scripts/tools-manifest.json sbox-bridge-addon/Editor/Mcp docs/TOOLSETS.md` | no | nobody edited a zod schema without regenerating, or hand-edited a generated file |
| 5 | **Live gate** | `node scripts/verify-native-mcp.mjs [--port 7269]` | **yes** | the whole native surface, end to end (below) |

Gates 1–4 are exactly what CI runs (`.github/workflows/ci.yml`). Gate 5 needs a human with
an open editor — it is the release gate.

### What the live gate checks

`verify-native-mcp.mjs` speaks streamable-HTTP JSON-RPC to the native server and prints
`PASS`/`FAIL` per check, then `N/N checks passed` (exit 1 on any failure). Check families:

- **Handshake + inventory** — `initialize`, `list_toolsets` reports the expected `bridge_*`
  toolsets.
- **Discovery** — `search_tools "create gameobject"` finds `create_gameobject`; `search_tools
  "multiplayer test"` finds the start/status/stop harness.
- **Read-only spot-runs** — one tool per family (`get_bridge_status`, `get_project_info`,
  `is_playing`, `multiplayer_test_status`, `get_scene_hierarchy`, `list_prefabs`, `describe_type`, `validate_project`,
  `describe_project`, `describe_scene`, `find_broken_references`).
- **Nullable binding** — `find_objects` with its `int?` limit both passed and omitted.
- **Mutating GUID round-trip** — `create_gameobject` returns a GUID → `delete_gameobject`.
- **Inline image + rotation binding** — `take_screenshot` returns an inline PNG content block
  (not a file path); out-of-order `{yaw,roll,pitch}` objects round-trip to an exact yaw through
  `set_transform` and `save_camera_bookmark`, while `capture_view` accepts the same native-stringified contract.
- **Throw semantics** — a bad GUID produces a real, readable thrown tool error, not an
  `{ error }` payload inside a success.
- **Wave regression chains** — batch tools (`dryRun: true` → apply → verify), full prefab
  serialize → `instantiate_prefab` recreates components, `checkpoint_scene` →
  delete → `restore_checkpoint` resurrects the object, the wave-3/4 scaffolds generate →
  one hotload → compile clean → `tune_vehicle` e2e on the compiled controller.

One check prints `SKIP`: the auto-undo convention is parked on an engine limitation
(`FullUndoSnapshot` / `UndoSystem.Snapshot` verified inert) — scene checkpoints are the
supported answer.

### Verifying the lifeline

The live gate covers the native surface only. Verify the lifeline by launching it directly
(`npx -y sbox-mcp-server@2 --lifeline`, or `node dist/index.js --lifeline` from a local
build):

| Check | Expected |
|---|---|
| Tool registration | exactly the **7** lifeline tools: `read_log`, `get_compile_errors`, `search_docs`, `get_doc_page`, `list_doc_categories`, `run_self_test`, `get_bridge_status` — nothing else |
| Editor **closed** | `read_log` / `get_compile_errors` still answer (they read `sbox-dev.log` directly); `get_bridge_status` reports **disconnected** once the heartbeat is >5 s stale (no false positive); `run_self_test` reports BROKEN — bridge not responding |
| Editor **open** | `get_bridge_status` reports connected (the lifeline talks legacy IPC to the addon) |

The point of the lifeline is the middle row — it's how Claude diagnoses a dead editor that
took the native server down with it.

---

## 2. Manual smoke path (~10 minutes, editor open)

Run after any addon change reaches the live project (sync with absolute paths →
`restart_editor` — see [CONTRIBUTING.md](CONTRIBUTING.md) for the loop).

| # | Step | Expected | Status |
|---|------|----------|--------|
| 1 | Launch the editor with the bridge project | native server up on port 7269 (Editor → Preferences → MCP Server) | [ ] |
| 2 | `get_bridge_status` | `connected: true`, `versionsAligned: true`, `handlerCount` = **278** (a lower number = partial compile / failed registrations — check `get_compile_errors`) | [ ] |
| 3 | `run_self_test` | `HEALTHY — 8/8`: connectivity → create temp object → add component → assign model → non-empty bounds → `capture_view` PNG → recompile temp `.vmat` → remove component; then cleans up after itself. Refuses in play mode | [ ] |
| 4 | Native round-trip: `list_toolsets` → `search_tools "flicker light"` → `call_tool add_flicker_light` (or any found tool) | toolsets listed, tool found by natural language, call executes with a real result | [ ] |
| 5 | `take_screenshot` | returns an **inline PNG** image block in the tool result — look at it; if you got a file path back, you're on the legacy surface | [ ] |
| 6 | `inspect_model_geometry` on a known prop, then `scatter_props` with `dryRun:true` | geometry includes model-local provenance and grounding offset; preview returns exact placements + `planId`; scene hierarchy is unchanged | [ ] |
| 7 | `commit_placement_plan` for that preview, inspect receipts, then delete its returned group/objects | exact slot count and GUID receipts; grounded bottoms visually sit on the surface; cleanup leaves no test props | [ ] |
| 8 | `set_transform` with explicit `space:"world"` and `space:"local"`; repeat the same value | before/after receipts use the requested space; repeat reports `noOp:true`; a malformed multi-field request changes nothing | [ ] |
| 9 | `get_bounds` and `find_objects_near` around the test prop | aggregate/render/physics/solidPhysics provenance is explicit; nearby results are nearest-first pivot distances | [ ] |
| 10 | Save two camera bookmarks, run `capture_camera_set` twice with `comparePrevious:true`, call `capture_topdown`, then delete the bookmarks | ordered inline PNGs + labeled manifests; second set has comparable RGBA metrics; top-down scale metadata is present; deletion removes baselines | [ ] |
| 11 | Pass out-of-order `rotation:{yaw:90,roll:0,pitch:0}` objects through `set_transform`, `save_camera_bookmark`, and `capture_view`; also smoke `spawn_citizen`/`equip_model` with a comma string; clean up all artifacts | returned receipts preserve yaw=90 regardless of JSON property order; captures and character/equipment calls avoid string-conversion errors | [ ] |

---

## 3. Play mode & the playtest harness (manual)

Still-valid checks carried over from the v1 plan, retargeted to the v2 surface:

| # | Check | Steps | Expected | Status |
|---|-------|-------|----------|--------|
| P1 | Play round-trip | `start_play` → `is_playing` → `stop_play` | enters play; **trust `gameFlag`, not `sessionPlaying`** (can read stale); returns to editor | [ ] |
| P2 | Mutation guard | during play, call `create_gameobject` | refused with a clear "play mode" error — the edit is not silently lost | [ ] |
| P3 | In-game capture | during play, `capture_view` | inline PNG of the running game's POV | [ ] |
| P4 | Scripted gameplay loop | `playtest` with steps: `move` → assert `Displacement` > 0 → `jump` → assert `IsAirborne` next frame → land → assert `IsOnGround` | verdict PASS; assertions evaluate **in-frame** (transient states are catchable) | [ ] |
| P5 | Harness status/abort | `playtest_status` mid-run; `playtest_abort` on a stuck run | live progress, then the full per-step transcript; abort stops the job (`"aborted": false` when nothing runs) | [ ] |
| P6 | Aimed verification | `screenshot_from` at a known object | a temporary non-main camera frames the target and returns an inline PNG; the real camera remains unchanged | [ ] |
| P7 | Local multiplayer harness | `start_multiplayer_test clients:1` → poll `multiplayer_test_status` until `connectionCount` reaches returned `expectedConnections` → call an unrelated read-only editor tool → `stop_multiplayer_test disconnect:true` | one real `sbox.exe -joinlocal` client appears and joins above the returned pre-spawn `baselineConnections`; status and the unrelated editor call return while the client is alive; a newly created lobby is private/hidden; stop returns without stalling the editor, with `stopped:true` and empty `remainingPids`. Allow about 80 seconds and roughly 4.5 GB RAM | [ ] |
| P8 | Multiplayer guardrails | while disconnected call `start_multiplayer_test clients:2 maxPlayers:2`; after P7 has started, call `start_multiplayer_test clients:1` again | first call rejects host-plus-client capacity without creating a lobby/process; second rejects an overlapping tracked run without launching another client | [ ] |

**Honest limit:** the harness proves controls *fire* and state transitions happen; it is
not a substitute for a human playtest of **feel** (no analog input synthesis — see
[docs/BRIDGE_GOTCHAS.md](docs/BRIDGE_GOTCHAS.md) #1).

---

## 4. Security spot-checks

The handler layer is unchanged from v1, so its containment guarantees still need spot
verification after changes near file/path code:

| # | Test | Expected |
|---|------|----------|
| S1 | `read_file` with `../../etc/passwd` | thrown error: path must be within project |
| S2 | `write_file` / `delete_script` with a rooted path outside the project | thrown error, nothing written/deleted |
| S3 | `create_script` with a `name` containing spaces/punctuation | compilable class (sanitized identifier), not broken C# |
| S4 | any tool with an unknown-but-valid GUID | readable thrown error ("not found"), never a false success |

---

## Test execution notes

- Mark `[ ]` → `[x]` as steps pass; the live gate is self-reporting.
- The recompile loop for addon changes is `restart_editor`, not the file-watcher
  ([docs/BRIDGE_GOTCHAS.md](docs/BRIDGE_GOTCHAS.md) #9). Successful compiles log nothing —
  fingerprint with `handlerCount`, don't trust silence.
- Screenshots on the native surface are **inline**; only the legacy path writes
  `<sbox>/screenshots/*.png` files.
- Networking scaffolds verify as code generation + compile. The local multiplayer harness can
  create a real host/client session for host-side replication assertions, but it cannot inspect
  or drive the separate client window.
