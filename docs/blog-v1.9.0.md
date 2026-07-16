# s&box Claude Bridge v1.9.0 — We Gave the AI a Brain

**The best AI tool for building s&box games just got a knowledge base.** v1.9.0 ships `sbox-cookbook`: a deep, code-grounded library of how real games are actually built — so the AI reaches for proven, shipped patterns instead of guessing.

If you build s&box games and you talk to an AI to do it, this is the release that changes the math. Not because the model got smarter — because we stopped making it improvise.

---

## The Brain

Here's the honest problem with using *any* AI to write s&box code: the engine moves fast, the API drifts between SDK builds, and a general-purpose model fills the gaps with plausible-sounding nonsense. It'll happily hand you a multiplayer economy that desyncs the moment a second player joins, a save system a client can forge, and a loot table that's "random" in a way players can exploit. It looks right. It compiles. It's wrong in exactly the ways that bite you three weeks later.

v1.9.0 fixes that at the root. We built a **massive brain** into the plugin — a skill called `sbox-cookbook` — and we trained it on **real, shipped, public-source s&box games**. The best games in the ecosystem. Not toy samples, not docs paraphrased, not the model's training data: the actual source of games people built and shipped, plus the modern s&box engine source itself.

Then we distilled what those games do into **code-grounded recipes**. It covers the systems that are genuinely hard to get right:

- **Inventories** — backpacks, hotbars, stacking, the networked-collection patterns that don't roll back on proxies
- **Economies & currency** — host-authoritative balances, the request → apply → confirm shape, money as `[Sync(SyncFlags.FromHost)]` so a client can't author its own wallet
- **Save & persistence** — signed, versioned save files in `FileSystem.Data` that sanitize and clamp on load, because saves outlive your balance changes
- **Shops & trading** — vendors, prices, trades that re-validate server-side
- **Gacha & loot** — weighted tables, recharge timers, provably-fair rolls
- **Progression & prestige** — data-driven balance tables, upgrade and prestige trees
- **Multiplayer networking & authority** — the single biggest bug class in s&box, handled the way shipped games handle it: ownership gates, `[Rpc.Host]` re-validation, the real netcode patterns
- **Level design** — modular sets, blockouts, lighting, triggers, spawns

And on top of the systems, full **genre playbooks** — tycoon/idle, shopkeeper, deathmatch/arena, platformer & obstacle course, survival/horror, card-battler, roleplay, gacha-crawler, and more. Each one tells the AI which systems to compose, in what build order, and how the real games actually wired them together — with source it can point to.

Every recipe is grounded in how shipped games do it. That's the whole pitch. When you ask the plugin to "build me a tycoon" or "add a trading shop" or "make the loot drops feel fair," it isn't pattern-matching against a half-remembered tutorial. It's reaching for the same architecture that's already running in production s&box games. **Proven patterns instead of hallucinations.**

---

## See it, verify it

A brain that only writes code is half a tool. The other half is being able to *look* at what got built and confirm it's correct — and v1.9.0 gives the AI eyes for that too.

Six new inspection and validation tools (**166 total now, up from 160**) mean the AI can SEE and VERIFY, not just write:

- **`inspect_networked_object`** — dump exactly what an object replicates over the network, plus every component's `[Sync]` fields with their flags and live values. No more guessing what's actually synced.
- **`networking_lint`** — a static scan for the multiplayer footguns that cause silent desync and exploits: money/health/score declared as plain `[Sync]`, unguarded mutators, `[Rpc.Host]` methods that never re-check the caller. The AI can now catch its own authority bugs before you ever hit play.
- **`scene_validate`** — flags scene-setup mistakes: no camera, stray root rigidbodies, trigger-vs-trace mismatches.
- **`save_inspect`** — list, read, and diff the project's save files, so persistence bugs are visible instead of mysterious.
- **`services_query`** — read live `Sandbox.Services` stats and leaderboards.
- **`simulate_input`** — drive named input actions during play, so the AI can actually exercise what it built.

Pair these with the screenshot-driven workflow the plugin already ships, and you get a loop that's closer to how a careful human works: write it, inspect what it produced, lint it for the classic mistakes, watch it run.

---

## Built on the current API — verified by reflection

Recipes are only as good as the API they assume. So before shipping, we **reflected the live SDK** and confirmed every API the cookbook and the generated C# rely on against the *current* engine — not stale training data. And the bridge keeps doing this at build time: the live editor reflection (`describe_type`, `search_types`, `get_method_signature`) is always the authoritative signature check for *your* installed SDK. If a recipe ever disagrees with what's actually installed, reflection wins.

**License:** v1.9.0 is relicensed to **AGPL-3.0-or-later** — fully open source, copyleft. (One note: the code is open, but the "s&box Claude Bridge" name and branding aren't a license to pass a fork off as the original. See `NOTICE`.)

---

## Get it

The brain lives in the **Claude Code plugin** — and that's the way to run this. The bare MCP server gives you the tools; the plugin gives you the tools *plus* the brain: the `sbox-cookbook` knowledge base, a specialist agent tuned to build games with the bridge, the screenshot-driven build workflow, and first-run onboarding. The recipes and the tuned agent are the difference between an AI that improvises and one that builds like it's read the source of every good game in the ecosystem. Install the plugin:

```
/plugin marketplace add LouSputthole/Sbox-Claude
/plugin install sbox-claude
```

Then restart Claude Code. Open your s&box project, connect the bridge, and ask it to build something real.

The model didn't get smarter this release. It got **experienced.** Go build something.
