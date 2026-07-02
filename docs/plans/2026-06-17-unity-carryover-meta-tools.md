# Wave plan — engine/workflow meta-tools ported from the Unity bridge (2026-06-17)

**Premise:** the s&box `TOOL_BACKLOG.md` already covers the game-system-scaffold lane (corpus-mined
from 51 games). The Unity bridge build-out pulled ahead in a *different* lane — **engine / workflow
meta-tools** that drive and inspect a running scene rather than generate gameplay code. This wave
ports the high-value, s&box-feasible ones.

**Hard gate (this project's rule):** s&box must be running with the `claudebridge` addon. For every
tool: `describe_type` the API live BEFORE writing C#, then generate → hotload/restart →
`get_compile_errors` → screenshot-verify. No blind ships.

## Candidates, ranked

### 1. `debug_draw_*` + `debug_clear`  — BUILD FIRST  (risk: low-med — API confirmed)
Unity: `debug_draw_box/line/ray/sphere/label`, `debug_clear`. s&box has no debug-viz at all.
- **Why first:** renders in the editor → `screenshot_from`-verifiable (the bridge's proven sweet
  spot, unlike runtime particles which can't be seen through the bridge).
- **Use cases:** visualize `raycast` hits, `physics_overlap` volumes, `trigger_zone` bounds, NPC
  `SightRange`/`FovDegrees` cones, `place_patrol_route` paths.
- **Impl path (API CONFIRMED in corpus):** a `ClaudeDebugDraw` component (`NetworkMode.Never`)
  holding a `List<DebugPrim>`. **Edit-mode** render via `protected override void DrawGizmos()` +
  `Gizmo.Draw.*`. **Open question RESOLVED:** `DrawGizmos()` runs even when the object is NOT
  selected — proven by the `Gizmo.Draw.Color = ...WithAlpha( Gizmo.IsSelected ? 1f : 0.2f )` pattern
  (terry_games `FloorIsLava.cs:114`), which only makes sense if it draws in both states. No
  force-select needed. **Play-mode** render via the duration-based `DebugOverlay` (below). One
  handler appends a primitive; `debug_clear` empties the list / destroys the holder.
- **Confirmed gizmo API** (terry_games `Code/Decorations/SlamingDoor.cs`, `Code/Obstacles/Obstacle.cs`,
  `Code/Logic/Gamemodes/FloorIsLava/*`): `Gizmo.Draw.Color`, `Gizmo.Draw.LineThickness`,
  `Gizmo.Draw.IgnoreDepth`, `Gizmo.Draw.Line(a,b)`, `Gizmo.Draw.LineBBox(bbox)`, `Gizmo.Draw.Model(path)`,
  `Gizmo.IsSelected`, `Gizmo.Colors.*`. Verify exact `Sphere`/`SolidBox`/`Text` member names via
  `describe_type Gizmo.GizmoDraw` when live (the rest are confirmed).
- **Confirmed runtime API** (battledraft `Code/Utils/*`, darkrpog ToolGun modes): static
  `DebugOverlay.Box(center,size,color)` / `.Line(new Line(a,b),color)` / `.Sphere(new Sphere(pos,r),color,duration,overlay:)`
  / `.Text(pos,str,size:,duration:,overlay:)` / `.Capsule(...)`; or instance form
  `DebugOverlaySystem.Current.Sphere/Line/Text/Box(... duration:, overlay:)`. Use `duration:` so a
  one-shot bridge call persists a few seconds in play mode.

### 2. `set_time_scale`  — CHEAPEST WIN  (risk: low — API confirmed)
Unity: `playtest_set_time_scale`. s&box has play/stop but no time control.
- **Impl (CONFIRMED):** set `Scene.TimeScale` (float). `0f` = pause, `1f` = normal, `0.1f` = slow-mo.
  Clamp >= 0. Read-back in a `*_status`/return field. Works on the active scene.
- **Corpus proof:** sdoomresurrection `Code/ui/Pause.cs` (`Scene.TimeScale = 0f` / `= 1f`),
  ss1 `Code/Manager.cs` (animated ramps), sandbox/scenestaging FreeCam.
- **Value:** slow-mo to watch a fast interaction; fast-forward idle/economy ticks during playtest.

### 3. `get_profiler_stats`  (risk: low — API confirmed)
Unity: `get_profiler_stats`. s&box has none.
- **Impl (CONFIRMED):** read `Sandbox.Diagnostics.PerformanceStats`. Headline = `.LastSecond`
  (rolling-avg struct). Full surface (darkrpog `Code/Diagnostics/RoleplayPerfDiagnostics.cs` is a
  complete reference impl): `FrameTime` (s, ×1000 → ms), `GpuFrametime`, `GpuFrameNumber`,
  `BytesAllocated`, `ApproximateProcessMemoryUsage`, `Exceptions`, and
  `Timings.{Update,Physics,Ui,Render,Network,GcPause}` each with `.AverageMs(frames)`.
- A clean read-only dump; needs live engine state (not MCP-server-side). Editor-handler tool.

### 4. `run_tests` + `get_test_results`  (risk: HIGH — likely no clean path, do LAST)
Unity: drives the project test runner. s&box bridge only has `run_self_test` (its own health).
- **Gap:** can't run the *project's* `UnitTests/` suite from the bridge.
- **Corpus finding (2026-06-18):** s&box tests use **MSTest**
  (`global using Microsoft.VisualStudio.TestTools.UnitTesting;`, sgba `UnitTests/UnitTests.cs`).
  **No editor-side test-runner API** (`TestRunner`/`RunTests`/`ITestRunner`) appears anywhere in the
  engine or 51-game corpus — so there is probably no in-bridge handler path.
- **Only feasible path:** MCP-server-side `dotnet test` against the generated `*.unittest.csproj`
  (no editor API needed — like `read_log`/`get_compile_errors`). **Risk:** the test project references
  s&box DLLs; `dotnet test` outside the editor build context may not resolve them. **Spike this with a
  raw `dotnet test` in a terminal BEFORE writing the tool** — if it can't resolve s&box assemblies,
  drop #4 entirely.

### 5. `sim_advance` / frame-stepping  — DEFER  (risk: high)
Unity steps the sim N frames deterministically. s&box has no obvious clean tick-advance API from the
editor. Not worth the dig unless a clear `Scene` tick hook turns up. Skip for now.

## Progress (2026-06-18) — branch `claude/unity-carryover-meta-tools`
- **`set_time_scale` (#2): DRAFTED, offline-verified.** `debugviz.ts` + index.ts wiring +
  `DebugVizHandlers.cs::SetTimeScaleHandler` + `Register(...)` in MyEditorMenu.cs. `npm run build`
  clean, parity audit PASS.
- **`get_profiler_stats` (#3): DRAFTED, offline-verified.** Added to the same files. Parity PASS at
  **194 tools / 185 handlers / 0 orphans @ 1.13.0**.
- **RESOLVED:** both live-verified and SHIPPED in v1.14.0 (set_time_scale 0.1↔1.0 + real profiler stats via raw IPC).
- **RESOLVED:** `debug_draw_*` (#1) SHIPPED in v1.15.0 (both render paths live-verified); `run_tests` (#4)
  CLOSED as infeasible in v1.16.0 (s&box test projects need the editor's net10 build chain; machine has .NET 8 only).
- Uncommitted (commit-when-asked). Pre-existing unrelated edits on the branch:
  CONTRIBUTING.md / README.md / package.json — leave them.

## Build order
1. `set_time_scale` (low risk, proves the wave) → 2. `debug_draw_*` (highest value) →
3. `get_profiler_stats` (API confirmed) → 4. `run_tests` (only if the runner API exists).

APIs for 1–3 are now grounded in shipping game code (see per-tool corpus refs) — the live
verify-loop is a confirm, not a discovery. **Land each C# handler and its TS tool together:**
`scripts/audit-parity.mjs` (CI) enforces TS↔C# parity, so a TS-only stage would fail the gate.
That's why none of this can be pre-built while the editor is down — the C# half needs a live compile.

Model new handlers on the existing scaffold generators in `sbox-bridge-addon/Editor/` (separate
handler file, register in `MyEditorMenu.cs`, TS tool in `sbox-mcp-server/src/tools/`). New TS module
suggestion: `src/tools/debugviz.ts` (1 + 2) and fold profiler/tests into `diagnostics.ts`.
