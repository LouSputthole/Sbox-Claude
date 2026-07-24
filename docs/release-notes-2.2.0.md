# s&box Claude Bridge v2.2.0 — Arguments That Just Work, Multiplayer That Tells the Truth, and Codex

**286 tools. 278 editor handlers. Two bug classes dead, one whole new audience.**

v2.1.0 shipped with two invisible cracks: structured arguments that the native MCP gate quietly
turned into strings nobody parsed, and a multiplayer test harness that existed in the codebase
but was never registered. Both were found the honest way — by an agent building a real game
against the live bridge and hitting them within the hour. v2.2.0 fixes both, verified in that
same game, and ships the bridge to a second agent platform.

---

## Rotation and vector arguments just work now

The native MCP ToolRegistry keeps the historical string parameter contract — which means a
structured `{"pitch":12,"yaw":45,"roll":7}` or `{"x":1,"y":2,"z":3}` arrives at the addon as a
*string of JSON*. In 2.1.0, every rotation parse threw `requires an element of type 'Object'…
target… 'String'`, and agents learned to route around it through `set_game_object`.

2.2.0 unwraps stringified JSON objects/arrays before parsing — on the shared path and the
strict paths, for **both rotation and vectors** (position, scale, center, spacing, lookAt).
Comma strings (`"12,45,7"`), arrays, and objects all parse, everywhere: creation, transform,
camera bookmarks and captures, placement plans, drive_player.

Worth recording: the vector half of this fix came from the pre-merge review, not the original
patch. The rotation fix was live-verified; the identical gap in three vector parsers was
sitting right next to it, with a changelog entry already claiming it was fixed. A mechanical
grep for unwrap call-sites caught it. Reviews earn their keep.

## The multiplayer harness is real now

`start_multiplayer_test`, `multiplayer_test_status`, and `stop_multiplayer_test` shipped in
2.1.0's source — unregistered. Nobody could call them. 2.2.0 registers all three and hardens
the whole flow:

- **Private, hidden lobbies** — your test session doesn't show up in anyone's server browser.
- **Capacity validation and overlapping-run prevention** — no double-starting a harness run.
- **False-positive join guards and failed-start rollback** — a lobby that half-started cleans
  itself up instead of lying about it.
- **Truthful tracking** — clients are real `sbox.exe -joinlocal` processes (~4.5 GB RAM each,
  capped at 2); when one dies, status says so (`alive: false`), immediately.

Live proof, same day: a harness client booted, joined a real game-in-development's lobby,
spawned as player 2, played ten minutes of rounds, and when the editor stalled long enough to
starve its connection, the status endpoint reported the death honestly and the game's
disconnect path cleaned up without a trace.

## The s&box Codex Bridge

The first public **Codex distribution** ships from the same repository:
`plugins/sbox-codex-bridge` — the same 286 tools over the same native editor MCP server, plus
the lifeline diagnostics, the API brain, the 51-game cookbook, and the workflow skills,
packaged for OpenAI Codex. It is generated from the Claude plugin by
`scripts/gen-codex-plugin.mjs`, so the two can't drift.

```
codex plugin marketplace add LouSputthole/Sbox-Claude --ref codex-v2.2.0
codex plugin add sbox-codex-bridge@sboxskins
```

## Also in this release

- Prefab `[Property]` values are documented as **authoritative over code defaults** — a saved
  prefab overwrites your field initializers on deserialize. Tune the prefab, not the constant.
  (`sbox-build-feature` gotchas has the pattern.)
- The release record is honest again: legacy file-IPC retirement, announced for v2.1.0, did not
  happen and is now explicitly deferred pending a compatibility decision.

## Upgrading

- **Claude Code:** `claude plugin marketplace update sbox-claude`, update the plugin, restart.
- **Editor addon:** update `sboxskinsgg.claudebridge` via the s&box Library Manager.
- **Lifeline (optional):** the plugin pins `sbox-mcp-server@2.2.0` automatically.

Tool names are unchanged. Nothing was removed.
