---
name: sbox-design-feature
description: Use before implementing a non-trivial s&box feature that needs design choices, state transitions, networking authority, animation or camera behavior, UI interaction, or visual acceptance criteria. Produces a compact implementation and verification design that hands off to sbox-build-feature.
---

# Design an s&box Feature

Create the smallest design that removes expensive ambiguity before code changes.

1. Read the nearest project `AGENTS.md` and any project-specific `CLAUDE.md` that records s&box facts. Current user instructions win.
2. State the player-visible outcome and the explicit non-goals.
3. Identify the owning components, state transitions, data flow, and multiplayer authority boundary.
4. List unfamiliar s&box types or signatures that must be verified through live reflection before implementation.
5. Split the work into small implementation slices. Keep parallel agents on disjoint source files; keep scene mutation, play mode, and screenshots under one coordinator.
6. Define proof: clean compile, structural inspection, runtime assertions where applicable, and framed screenshots for every visual outcome.

Return a concise design with assumptions and acceptance checks. Then follow `$sbox-build-feature` to implement and verify it.
