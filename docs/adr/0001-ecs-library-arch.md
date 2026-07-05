# ADR 0001 — Use the Arch ECS library for the Core Simulation

- **Status:** Accepted
- **Date:** 2026-07-05
- **Relates to:** PLAN.md §7.1 (ECS), §16 (ECS library choice risk)

## Context

PLAN §7.1 directs us to start with a proven, lightweight C# ECS library (naming Arch and
DefaultEcs as candidates) rather than hand-rolling archetype storage and query iteration,
and to record the decision as an ADR.

## Decision

Use **Arch** (`Arch` on NuGet, v2.1.0) as the ECS foundation of `shared/`.

- Archetype-based storage with struct components — fits the "components are pure data" rule
  and the entity-dense simulation this game needs (§7.10).
- Actively maintained, zero mandatory dependencies, works on .NET 8 and inside Godot's
  .NET runtime without any engine coupling.
- Plain-C# API (`World`, `QueryDescription`, delegate/struct queries) keeps `shared/`
  engine-agnostic (PLAN §6 rule).

Arch's `World`/`Entity`/`QueryDescription` types are used directly by systems (no wrapper
layer around every ECS call): a full abstraction layer over the ECS would cost performance
and clarity for hypothetical portability we don't need. The `Prison.Shared.Simulation`
façade owns world lifetime, the fixed-tick loop, and system registration, so a future
library swap — while expensive — stays localized to systems' query code.

## Consequences

- Save/serialization (Phase 7, §7.12) must serialize *components*, never Arch internals —
  the versioned binary format keeps us independent of Arch's storage layout.
- Networking replication (Phase 8) iterates components via queries; no Arch types on the wire.
- Revisit only if Godot interop or serialization friction proves too costly (§16), via a new ADR.
