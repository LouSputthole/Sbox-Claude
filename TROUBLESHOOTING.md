# Troubleshooting

Common failure modes and their fixes, ordered roughly by how often each one hits. Also online: [sboxskins.gg/claudebridge/troubleshooting](https://sboxskins.gg/claudebridge/troubleshooting).

Quick orientation:

- **`get_bridge_status` is your first stop.** It reports the IPC directory, the heartbeat age, the bridge build version, and the result of a real round-trip — enough to tell "not connected" from "connected but requests aren't draining" from "fully working."
- **When the editor itself is misbehaving, read the log.** `get_compile_errors` and `read_log` (both v1.5.0, MCP-server-side) read `sbox-dev.log` directly, so they work even when the editor has crashed or its frame loop has stalled. You no longer have to guess.
- The bridge build version and the MCP-server version should match major.minor. A mismatch (`handlerCount` lower than expected, or "Unknown command") usually means one side is stale — see §11.

---

## 1. Bridge won't connect — "connected but every call times out" (IPC directory mismatch)

**Symptom:** `get_bridge_status` says connected (or pings in ~0ms) but every real tool call hangs and times out at 30s.

**Cause #1 — the two sides resolved different temp directories.** This is the single most common cause. The MCP server (Node) resolves its IPC dir via `os.tmpdir()` (which reads `TEMP` first); the s&box addon (C#) resolves via `Path.GetTempPath()` (which reads `TMP` first). On some Windows setups `TEMP` and `TMP` differ, so the server writes `req_*.json` into a directory the addon never reads.

**Fix:**

1. Find the directory the **addon** is actually using:
   - **Editor → Claude Bridge → Status** shows it, or
   - check the editor console for `[SboxBridge] … IPC at <dir>`, or
   - read `status.json` in the candidate temp dir — its `ipcDir` field is authoritative.
2. Point the MCP server at that same directory with `SBOX_BRIDGE_IPC_DIR`:
   ```bash
   claude mcp remove sbox
   claude mcp add sbox --env SBOX_BRIDGE_IPC_DIR="<that dir>" -- npx sbox-mcp-server
   ```
   (If you launch via `node …/dist/index.js`, set the same env var.)

The addon side resolves its directory from `Path.GetTempPath()` only and does **not** honor an env override (to stay inside the s&box sandbox), so always realign from the MCP-server side.

> `SBOX_BRIDGE_HOST` / `SBOX_BRIDGE_PORT` are **cosmetic** — there is no network socket. Changing them does nothing except what `get_bridge_status` displays. (Older docs that told you to "change the port in `MyEditorMenu.cs`" are obsolete — that file has no port.)

---

## 2. Connected, but no response — requests don't drain

**Symptom:** `get_bridge_status` reports a live heartbeat, but tool calls don't drain.

**Cause:** The bridge processes queued requests from a **static** `[EditorEvent.Frame]` handler (since v1.3.0). It runs whether or not the Claude Bridge dock is open, so a **closed dock is not the cause**. Two real causes remain: a single very slow handler blocking the frame it runs on, or the editor window being **minimized** long enough that the OS throttles frame events for the process.

**Fix:** Keep the s&box editor window visible (not minimized). If one specific call hangs, suspect that handler — read `read_log` / `get_compile_errors` to see what it's doing. The dock does **not** need to be open.

**Verify:** Ask Claude to call `is_playing` — it should respond in well under a second.

**If the heartbeat itself is stale** (`get_bridge_status` says disconnected): s&box isn't running, the project failed to load, or the bridge addon failed to compile — see §6.

---

## 3. C# compile failure — "Compile of 'local.\<project>.editor' Failed:"

**Symptom:** Tools stop working after you (or Claude) edited a script; the editor shows a broken-reference or compile error.

**First: just read the error.** Don't guess — use the v1.5.0 self-diagnosis tools:

```
get_compile_errors      → the latest compile failure(s), parsed from sbox-dev.log
read_log  filter="Failed"  → broader log context if you need it
```

Both run MCP-server-side, so they work even if the editor is in a broken state. Fix the file the error names, `trigger_hotload`, and re-check `get_compile_errors` until clean.

**Common true causes** (the `tool.frame` / broken-reference message is usually a *wrapper*, not the cause):

- A real syntax/type error in a project `.cs` file. If the game code fails to compile, the editor-side bridge fails too (`Broken Reference: package.local.X`).
- A handler constructor threw on a newer SDK — that one tool is unavailable, but the rest of the bridge keeps working (registration is fault-tolerant). Look for `Failed to register '…'` in the log.
- A stale `.csproj` with hard-coded DLL paths from another machine — see §6.

**If you can't read the log via the tools,** open it directly:
`<sbox>/logs/sbox-dev.log` (e.g. `A:\SteamLibrary\steamapps\common\sbox\logs\sbox-dev.log`) and search for `[SboxBridge]` and `Compile of 'local.`.

---

## 4. Screenshot shows the wrong angle (you can't see your change)

**Symptom:** You made a visual change, called `take_screenshot`, and the change isn't in frame — or the shot looks like a completely different part of the scene.

**Cause:** `take_screenshot` always renders from the scene's **Main Camera** — one fixed angle. If the Main Camera isn't pointed at the object you changed, you won't see it. This is the #1 reason "I can't verify the visual change."

**Fix:** Use **`screenshot_from`** instead. It uses a temporary non-main camera to frame a target object or world point, returns an inline PNG, and leaves the real camera unchanged:

```
screenshot_from   target=<object GUID or position>
```

Then read the PNG. (`frame_camera` only moves the *editor viewport*, which the screenshot does **not** use — so `frame_camera` alone won't change what `take_screenshot` captures.)

> Screenshots save to `<sbox-install>/screenshots/sbox.<timestamp>.png` regardless of any `path` argument (known quirk). List the newest file there and read it.

---

## 5. Scene edit refused — "not allowed while play mode is active"

**Symptom:** A scene-mutating tool returns something like:

```
'create_gameobject' is not allowed while play mode is active. Stop play first (stop_play) and try again.
```

**Cause:** The bridge deliberately refuses scene-mutating commands while `Game.IsPlaying` is true. Mutating the scene during play can desync the serializer and corrupt `.scene` files on save — this guard exists because it actually happened to someone.

**Fix:** Call `stop_play`, make your edits, then `start_play` again. Read-only tools, `take_screenshot` / `screenshot_from`, `is_playing`, `start_play` / `stop_play`, and runtime-property tools are safe during play.

> **Side note — `is_playing` can lie.** Its `sessionPlaying` field sometimes reads `true` in edit mode after a restart. Trust the **`gameFlag`** field instead.

**To recover a save that was corrupted before the guard existed:** restore the most recent `.scene` from `<your-project>/.history/`, or from git.

---

## 6. "I had to install it twice" / the menu never appears (wrong install folder)

**Cause:** An older `install.ps1` copied the addon into `<sbox>/addons/sbox-bridge-addon/`. s&box's global `addons/` folder is built-in only and **silently refuses to compile custom C#**, so nothing appears and nothing tells you why.

**Fix:** Run the current installer, which targets your **project's** `Libraries/claudebridge/` folder, and clean up the stale copy:

```powershell
.\install.ps1 -RemoveStaleAddons
```
```bash
./install.sh --remove-stale
```

By hand: copy `sbox-bridge-addon/claudebridge.sbproj` → `<your-project>/Libraries/claudebridge/claudebridge.sbproj`, and `sbox-bridge-addon/Editor/MyEditorMenu.cs` → `<your-project>/Libraries/claudebridge/Editor/MyEditorMenu.cs`.

**Stale `.csproj` after moving machines/drives:** the auto-generated `Editor/claudebridge.editor.csproj` references s&box DLLs with absolute paths. If you copied it from another machine, delete it — s&box regenerates a correct one on next launch (the installer does this automatically):

```powershell
Remove-Item "<your-project>\Libraries\claudebridge\Editor\claudebridge.editor.csproj"
```

---

## 7. `Error calling event 'tool.frame' on 'BridgePoller'` — spammed every frame

**What you're seeing:** the bridge's frame handler is throwing, and s&box reports it ~60×/sec.

**What it means:** almost always the *real* error happened during bridge startup (a handler constructor threw, or the project didn't compile). The `tool.frame` line is the wrapper.

**Current behavior:** `OnFrame` is wrapped in try/catch with deduplicated logging — the console no longer floods, and the real underlying exception is logged once per unique message. Use `get_compile_errors` / `read_log` (§3) to find the true cause.

---

## 8. `claude mcp add sbox` succeeds, but Claude says no `sbox` tools exist

**Cause:** the MCP server process started and immediately exited. Usually one of:

- `dist/index.js` doesn't exist (you forgot `npm run build`).
- The path passed to `claude mcp add` was wrong or relative.
- Node.js is older than 18.

**Fix:**

```bash
cd sbox-claude/sbox-mcp-server
npm install
npm run build
ls dist/index.js          # must exist
node -v                   # must be 18.x or newer

claude mcp remove sbox
claude mcp add sbox -- node "/absolute/path/to/sbox-claude/sbox-mcp-server/dist/index.js"
```

If you're using the **plugin**, run `/reload-plugins` (or restart Claude Code) and check `~/.claude/plugins/`.

---

## 9. Particles are invisible (use `spawn_vpcf`, not `spawn_particle`)

**Symptom:** You called `spawn_particle` / `create_particle_effect` / `add_trail` / `add_beam`, the call succeeded, but nothing renders — at most a single flat sprite, and nothing in play mode.

**Cause:** those are the **experimental** Batch 18 runtime `ParticleEffect` tools. They compile and build the correct component graph, but s&box's component `ParticleEffect` needs sprite assets plus a live-play view the bridge can't supply, so the result **does not render through the bridge**.

**Fix:** use **`spawn_vpcf`** — it plays a compiled `.vpcf` via `LegacyParticleSystem`, which is the supported, visible particle path.

> Caveat (v1.5.0): no flame `.vpcf` ships in a bridge-loadable form yet — `ParticleSystem.Load` returns null for the cloud-cached `impact.generic`. A project-owned `.vpcf` is the reliable input. See `CHANGELOG.md` [1.5.0] → Known Issues.

---

## 10. `take_screenshot` ignores the `path` parameter

**Known quirk.** Screenshots always save to s&box's own directory regardless of what you pass:

```
<sbox-install>/screenshots/sbox.<timestamp>.png
```

List the most-recent file there and read it. (And remember §4 — it's the Main Camera; use `screenshot_from` to aim.)

---

## 11. Plugin / version drift — wrong tool count, or "Unknown command"

**Symptoms & causes:**

- **`Unknown command: <name>`** round-tripping to the editor → your **MCP server is newer than the addon** (it's sending a command the installed addon doesn't handle). Re-run the installer to update the addon, then restart s&box.
- **`tool not found` at the MCP server** for an old tool like `get_console_output` → your **MCP server is stale** (those phantom tools were removed in v1.3.0). Update it: `npx sbox-mcp-server@latest`, or `/reload-plugins` if you use the plugin.
- **`handlerCount` well below the README's number** in `get_bridge_status` → the addon didn't fully compile, or some handlers failed to register. Check `get_compile_errors` / the log for `Failed to register`.
- **Plugin pin vs. plugin version.** The plugin (v1.5.1) pins `sbox-mcp-server@1.5.2` in its `.mcp.json`, matching the published npm release — keep the two in lockstep. If you bump the pin, publish that npm version **first**, or `npx` will 404 on install.

**Rule of thumb:** keep the bridge addon and the MCP server on the same major.minor. If you upgrade one, upgrade both.

---

## 12. `Couldn't add project` / compiler-name collision on s&box startup

**Cause:** the project has **both** a local-dev `Libraries/claudebridge/` **and** an asset-library-installed copy (e.g. `Libraries/sboxskinsgg.claudebridge/`) claiming the same compiler name.

**Fix:** keep one. Either set the local copy's `Org` to `local` in its `.sbproj`, or remove the asset-library copy. **Never** sync the repo's `.sbproj` (which has `Org: sboxskinsgg`, for publishing) into a project's `Libraries/` — a project working copy must stay `Org: local`.

---

## 13. `create_material` errors with a dictionary-key error

**Resolved (v1.7+)** — `create_material` reads `path` and writes a valid KV1 `.vmat` (verified working). If you somehow hit this on an old addon, update it; as a fallback, write the `.vmat` directly via `write_file` (KV1 syntax: curly blocks, no JSON-style colons/commas).

---

## 14. `set_property` rejects a `Color` value

**Cause:** colors must be a comma-separated **string**, not a JSON object.

**Wrong:** `{ "value": { "r": 1, "g": 0, "b": 0, "a": 1 } }`
**Right:** `{ "value": "1, 0, 0, 1" }`

---

## 15. Procedural mesh renders chrome / sky-reflective

**Known limitation.** `MeshComponent.SetFaceMaterial(face, material)` and `MeshComponent.Color` tint don't visibly apply on a `PolygonMesh`. **Workaround:** place a `Ground` plane underneath as a visual fallback while the mesh still provides collision.

---

## Still stuck?

1. Run `get_bridge_status` and copy the full result (IPC dir, heartbeat age, bridge version, round-trip result).
2. Run `get_compile_errors` (or grab the `[SboxBridge]` block from `sbox-dev.log`).
3. Copy the exact Claude Code error text for the failing tool.
4. Open an issue at https://github.com/LouSputthole/Sbox-Claude/issues with all three. The startup banner alone (`[SboxBridge] … N handlers, IPC at …`) confirms where the addon loaded from and how many tools registered.
