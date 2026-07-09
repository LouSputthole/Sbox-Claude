# Troubleshooting (v2 — native MCP server)

The known failure modes of the v2.0.0 native transport, each as **Symptom → Diagnosis → Fix**.

Two companion docs:

- **[BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md)** — engine limitations you work *around*, not fix (input synthesis, asset shadowing, Razor transpiler quirks, whitelist errors, GPU stalls).
- The root **[TROUBLESHOOTING.md](../TROUBLESHOOTING.md)** — the legacy file-IPC transport (still compiled-in as a fallback through v2.0.x; retires v2.1.0).

---

## 1. Port 7269 not answering

**Symptom:** `http://127.0.0.1:7269/mcp` refuses connections or times out — `claude mcp add` registered fine, but every request fails and the `sbox` server shows as unreachable.

**Diagnosis:** The native MCP server is hosted **by the editor process**. If s&box isn't running, there is no server. If the editor *is* running, the server may be disabled in preferences.

**Fix:** Start s&box and open your project. Then check **Editor → Preferences → MCP Server** — it's on by default at port 7269; enable it if it's been switched off. If you changed the port there, update the URL you registered with `claude mcp`. Still dead with the editor running and the server enabled? See entry 2.

---

## 2. Editor log: `[MCP] Couldn't start MCP server on port 7269`

**Symptom:** The editor is running and the MCP server is enabled, but the port doesn't answer — and the editor log shows `[MCP] Couldn't start MCP server on port 7269` (the bind "conflicts with an existing registration"). The editor gives up silently; nothing else looks wrong.

**Diagnosis:** A **stale HTTP.sys registration** from a dying editor instance still holds the port. This happens when a previous editor process crashed or is still shutting down while the new one starts — the new instance can't bind, logs the line once, and carries on without the server.

**Fix:** Make sure the stale editor process has actually exited (check Task Manager for lingering s&box processes), then **restart the editor**. The bind succeeds once the stale holder is gone.

---

## 3. Bridge tools missing from `search_tools`

**Symptom:** `search_tools` finds the native built-ins (`scene_tree`, `asset_search`, …) but no bridge tools; `list_toolsets` shows no `bridge_*` toolsets.

**Diagnosis:** The bridge's 218 tools are `[McpTool]` methods discovered from the **claudebridge addon's compiled editor assembly**. If the library isn't installed in the *open* project, or its C# isn't compiling, the engine has nothing to discover. (Also check location: the addon must live in `<project>/Libraries/` — the global `addons/` folder silently refuses to compile custom C#.)

**Fix:**
1. Confirm the library is installed in this project: **Editor → Library Manager** → `sboxskinsgg.claudebridge`.
2. Check for compile failures with the built-in **`compile_status`** tool — or, if the editor-side tools are unusable, the lifeline's **`get_compile_errors`**.
3. Fix any errors (or reinstall/update the library) — toolsets hot-register within seconds of a clean compile, no restart needed.

---

## 4. Editing addon `.cs` files externally doesn't recompile

**Symptom:** You copy/sync/git-checkout files under `Libraries/claudebridge/Editor/` from outside the editor, and nothing changes — old tool behavior keeps serving, and no compile output appears in the log.

**Diagnosis:** The **Libraries file-watcher is unreliable** for external edits — sometimes it recompiles, often it silently ignores the change until a restart (see [BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md) #9). Worse, successful compiles log *nothing* (only failures log `Compile of '<addon>' Failed:`), so a quiet log proves nothing either way.

**Fix:** The dependable loop is: sync files → **`restart_editor`** → wait for the editor to come back (~90–150 s) → verify which assembly is live via `get_bridge_status`'s `handlerCount` (or a version marker) rather than trusting silence. Note this applies specifically to the `Libraries/` editor-assembly path — project `Code/` edits via `write_file` / `create_script` still hotload normally.

---

## 5. Editor crashed or hung — every native tool is dead

**Symptom:** All tools error out or the endpoint disappears entirely; the editor window is gone or frozen solid.

**Diagnosis:** The native server lives inside the editor process and **dies with it**. Nothing served over `http://127.0.0.1:7269/mcp` can tell you why the editor went down.

**Fix:** Use the **lifeline** server — it runs outside the editor and keeps working when nothing else does. `read_log` tails `sbox-dev.log`; `get_compile_errors` surfaces the C# compile failure that most often explains a bad session. Diagnose, fix, relaunch the editor. If you never registered it:

```bash
claude mcp add sbox-lifeline -- npx -y sbox-mcp-server@2 --lifeline
```

---

## 6. Scene-mutating tool refused during play mode

**Symptom:** A tool like `create_gameobject` or `set_property` throws an error saying it mutates the scene and is **refused during play mode**.

**Diagnosis:** Play mode is active. Scene-mutating bridge tools deliberately refuse while the game is running — edit-mode mutations against a live runtime scene get lost or conflict.

**Fix:** Stop play first — **`play_stop`** (native built-in) or **`stop_play`** (bridge) — then retry the call. To tweak values on live runtime objects *without* leaving play mode, use `set_runtime_property` (changes don't persist to the saved scene).

---

## 7. Tools time out, but the editor is open

**Symptom:** Calls hang and eventually time out even though the editor window is clearly up and the port answered before.

**Diagnosis:** Tools execute on the editor's **main thread**. A **modal dialog** (save prompt, popup, error/crash dialog) blocks that thread, so queued work is never picked up — the native server's `PickupTimeout` detection reports the editor as blocked rather than hanging forever.

**Fix:** Bring the editor window to the foreground and **dismiss the dialog**; pending work resumes immediately. If no dialog is visible and calls still stall (screenshots especially), suspect a GPU/render stall instead — see [BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md) #8 (`restart_editor`; your saved scene survives).

---

## Quick reference

| Symptom | Likely cause | Fix |
|---|---|---|
| Port 7269 not answering | editor not running / MCP server disabled | start the editor; **Editor → Preferences → MCP Server** |
| `[MCP] Couldn't start MCP server on port 7269` in the log | stale HTTP.sys registration from a dying editor instance | wait for the stale process to exit, restart the editor |
| No `bridge_*` tools in `search_tools` | claudebridge library not installed / not compiling | Library Manager check; `compile_status` or lifeline `get_compile_errors` |
| External addon `.cs` edits never take effect | Libraries file-watcher unreliable ([BRIDGE_GOTCHAS.md](BRIDGE_GOTCHAS.md) #9) | sync → `restart_editor` → verify via `handlerCount` |
| Everything dead, editor crashed/hung | native server dies with the editor | lifeline `read_log` / `get_compile_errors` |
| "refused during play mode" | play mode active | `play_stop` / `stop_play`, then retry |
| Timeouts with the editor open | modal dialog blocking the main thread (`PickupTimeout`) | dismiss the dialog; if none, see GPU stall (gotcha #8) |
