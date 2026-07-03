# Attribution — the `sbox-api` skill

This skill is adapted from **claude-sbox** by **David Ryan**, used under the MIT License (full text in `LICENSE`).

- **Upstream:** https://github.com/gavogavogavo/claude-sbox
- **License:** MIT © David Ryan (retained verbatim in `LICENSE`)

## What we changed when bundling it into the sbox-claude plugin
- Renamed the skill `sbox` → `sbox-api` and cross-linked it with the Claude Bridge + the `sbox-build-feature` workflow skill (write code correctly here → build/run/verify it with the bridge there).
- **Repointed every API-lookup instruction from the static schema files to the bridge's LIVE reflection** — `mcp__sbox__describe_type` / `search_types` / `get_method_signature` — which reflects the *actually-installed* SDK and is therefore more accurate than any baked-in list.
- **Dropped** the static schema (`references/api-schema-core.md`, `references/api-schema-extended.md`, `raw/api-schema.json`), the schema-generation `scripts/`, and the upstream `docs/` build logs — redundant with (and less current than) the bridge's live reflection.
- **Kept** the curated material that teaches patterns + the mental model: the Unity→s&box translation table and the Ten Rules in `SKILL.md`, plus the `core-concepts`, `components-builtin`, `ui-razor`, `networking`, `input-and-physics`, and `patterns-and-examples` references.

Big thanks to David Ryan — this is exactly the "write correct s&box C# instead of hallucinating Unity" knowledge that pairs perfectly with the bridge's hands + eyes.
