# PLAN.md — Project "Prison": Design & Build Plan

This document is the complete design and implementation plan for the game. It is derived from the founding design conversation (see `CONVERSATION.md` for the original discussion) and reorganized into a single actionable reference. Anyone should be able to pick up this document and understand what the game is, why it is built the way it is, and how to build it from nothing to a live, running product.

This document does not assume you've read `CONVERSATION.md`. Everything needed is restated here.

---

## Table of Contents

1. [Vision Statement](#1-vision-statement)
2. [Design Pillars](#2-design-pillars-non-negotiable-principles)
3. [Glossary](#3-glossary)
4. [High-Level Architecture](#4-high-level-architecture)
5. [Technology Stack](#5-technology-stack)
6. [Repository & Project Structure](#6-repository--project-structure)
7. [Core Simulation Systems](#7-core-simulation-systems)
8. [Prison Generation Pipeline](#8-prison-generation-pipeline)
9. [The Two AIs](#9-the-two-ais-evolution-ai-vs-staff-ai)
10. [Prison Hosting Model & Lifecycle](#10-prison-hosting-model--lifecycle)
11. [Backend Services & Data Model](#11-backend-services--data-model)
12. [Performance, Scalability & Settings](#12-performance-scalability--settings)
13. [Modding Support](#13-modding-support)
14. [Development Roadmap](#14-development-roadmap)
15. [Team, Tooling & Process](#15-team-tooling--process)
16. [Risks & Open Questions](#16-risks--open-questions)
17. [Appendix: Example Data Schemas](#17-appendix-example-data-schemas)

---

## 1. Vision Statement

A 2D top-down prison simulation game combining **Prison Architect** (layout, staffing, rules, prison-building simulation) with **The Escapists** (player-driven escape gameplay: crafting, stealth, disguises, digging, social engineering), wrapped in an **evolutionary loop**: a procedural generator builds a prison, players try to escape it, and every successful escape becomes training data that makes the *next* version of that prison harder in a specific, targeted way.

The core loop:

```
Algorithm generates Prison
        │
        ▼
Players infiltrate it
        │
        ▼
Players escape (or fail)
        │
        ▼
Game records everything
        │
        ▼
Escape analyzer finds weaknesses
        │
        ▼
Algorithm modifies prison design
        │
        ▼
New generation of the prison is deployed
        │
        ▼
Repeat forever
```

The prison is not random — it is **evolving**, and it remembers who it lost to and how.

This is not "a Godot game." It is a simulation platform (closer in ambition to Minecraft, Factorio, RimWorld, or Prison Architect than to a simple action game): huge entity counts, multiplayer, dedicated and player-hosted servers, procedural generation, real-time AI, long-running persistent worlds, and future modding support.

---

## 2. Design Pillars (Non-Negotiable Principles)

These principles should override any local decision that conflicts with them. If a feature request seems to violate one of these, the feature needs to be redesigned, not the pillar.

1. **Two AIs, strictly separated.**
   - The **Evolution AI** operates *between* prison generations (offline). It reads historical escape data and redesigns the prison.
   - The **Staff AI** operates *during* a live match (real-time). It never knows anything it could not physically perceive, and it never "learns" mid-match in a way that gives it an unfair advantage.
   - Violating this (e.g., guards that get smarter mid-match because "the AI is learning") breaks player trust immediately. Never do it.

2. **No AI or entity can perceive through walls, at unlimited range, or via anything other than a shared physical simulation.** Players, guards, dogs, and cameras all use the *exact same* visibility/hearing system. There is no "cheating" code path that gives NPCs magic knowledge.

3. **The simulation is authoritative and identical everywhere.** Whether a prison runs on official infrastructure or a player's laptop, the same simulation library with the same rules produces the same behavior. Only the *precision/budget* of the simulation may scale (see §12), never the *rules*.

4. **Everything is data, nothing is hardcoded.** Tiles, walls, items, jobs, rooms, prison styles — all described in external data files (JSON/YAML), never as `if (tile == WALL)` in engine code. This is what makes scaling, reskinning, and eventual modding possible without rewriting the engine.

5. **Rendering and simulation are fully decoupled.** Godot is the renderer and UI. It does not run AI, generation, or prison rules. The simulation runs at a fixed tick rate, independent of framerate, and can run headless (dedicated server) with zero rendering code involved.

6. **A new official prison is only born because players defeated the old one.** No prison churn for its own sake. Regeneration is earned, not scheduled arbitrarily (see §10).

7. **Privacy is opt-in outside official infrastructure.** Official (centralized) prisons always collect and share escape data (this funds evolution). Player-hosted prisons only share data if the host explicitly opts in.

8. **Generate, then improve — never expect one algorithm to produce a finished, beautiful prison in one pass.** Procedural generation is a pipeline of specialized passes (layout → validation → style → decoration → story → scoring), not a single monolithic generator.

9. **Difficulty is a one-way ratchet toward an aspirational "perfect prison" — escapability is never a design requirement.** The Evolution AI's long-term goal is not "balance" — it is a slow, targeted march toward a maximally secure, hypothetically perfect prison. A generated prison is **never required to remain escapable**, and "is an escape route possible?" is deliberately **not** part of the generation-validation pipeline (§8.4). What *is* required, unconditionally, on every generation no matter how secure it becomes: it must remain **enjoyable to play** and it must remain **believable** — a reasonable person should feel this facility could plausibly exist in the real world. Escapes becoming rarer, and eventually vanishingly rare, for a long-lived family is the intended endgame, not a failure state.

10. **No Kubernetes.** Backend and dedicated-server infrastructure is deployed with Docker Compose, driven by CI/CD (GitHub Actions). Orchestration complexity beyond that is not justified at this project's current scale — do not introduce Kubernetes (or an equivalent) without an explicit, documented reason revisiting this pillar.

---

## 3. Glossary

| Term | Meaning |
|---|---|
| **Prison** | One instance of a playable facility — either a persistent official world or a player-hosted server. |
| **Generation** | A version of a specific prison (e.g., "Blackstone, Generation 27"). Each generation is produced by evolving the previous one. |
| **Prison Family** | The persistent identity of an official prison across generations (e.g., "Blackstone"). A family has an Architectural DNA and a Warden Doctrine that persist and slowly evolve; the family itself never "resets." |
| **Architectural DNA** | The heritable visual/structural parameters of a family: age, architecture style, capacity, region, floor count, preferred blueprints, roof type, decoration density, etc. |
| **Warden Doctrine** | The heritable *gameplay/security* parameters of a family: staffing philosophy, patrol density, surveillance investment, guard doctrine, contraband tolerance, etc. Mutates in response to escapes. |
| **Functional Blueprint** | A reusable, pre-designed room/module template (e.g., "Medium Cell Block") defining gameplay layout: doors, furniture slots, capacity, connection points. Style-agnostic. |
| **Architectural Style Kit** | A reskin layer applied over a blueprint: wall/floor materials, window shapes, colors, roof edges — visuals only, no gameplay impact. |
| **Decoration Rule Set** | Procedural rules that scatter atmosphere details (pipes, stains, signs, plants, clutter) over a built room. Visuals only. |
| **Official / Centralized Prison** | Hosted on the project's own infrastructure. Always public, always shares escape data, participates in global evolution and rankings. |
| **Community Prison** | Hosted by a player. Visibility can be public/private/friends-only. Escape data sharing is optional. |
| **Evolution AI** | The offline system that turns escape data into weakness scores and mutates a prison family's DNA/Doctrine to produce the next generation. |
| **Staff AI** | The real-time system controlling guards, medics, dogs, cameras, etc. during a live match. Physically grounded, no cheating. |
| **LOD (Level of Detail — simulation)** | How much computational precision an NPC or region receives, based on relevance (is a player nearby? is anyone watching?). |
| **Simulation Bubble** | A region of the world currently being fully simulated because a player (or an important event) is present there. Areas outside bubbles run cheap statistical simulation. |
| **Heat Map** | Aggregated statistics over many playthroughs showing which corridors, rooms, or fences are exploited most, feeding the Evolution AI. |
| **ECS** | Entity Component System — the core data architecture: entities are IDs, components are pure data, systems are the only code that operates on components. |

---

## 4. High-Level Architecture

```
                              Godot Client
                        (Rendering + UI + Input)
                                   │
                     ════════════════════════════
                          Shared Simulation
                     (identical on client & server)
                     ════════════════════════════
                                   │
         ┌─────────────────────────┼─────────────────────────┐
         │                         │                         │
   Dedicated Server         Player-Hosted Server        AI Generator /
   (headless, official)     (headless or in-client)     Evolution Service
         │                         │                         │
         └────────────┬────────────┘                         │
                      │                                      │
                 Backend Services  ◄─────────────────────────┘
     (PostgreSQL, Redis, Object Storage, Message Bus, Observability)
```

Key structural decision: the **Shared Simulation** is a standalone library used identically by the Godot client (in single-player/local-preview mode), the dedicated official server (headless), and any player-hosted server (headless or embedded in a running client). The client adds rendering, input, and UI on top; it never contains gameplay logic that the server doesn't also have.

### 4.1 Module Breakdown

```
Core Simulation (shared library)
├── Entity System (ECS)
├── Tile / World System
├── Pathfinding
├── Visibility (FOV, hearing, smell)
├── Staff AI (perception, memory, reasoning, actions)
├── Suspicion & Investigation
├── Prison Rules (schedules, jobs, contraband, needs)
├── Economy (if applicable: jobs, wages, commissary)
├── Procedural Generation (blueprints, styles, decoration, validation)
├── Networking (message protocol, replication)
├── Serialization (save format)
├── Event Bus
└── Telemetry (escape recorder, heat maps, replay recorder)
```

The client project adds:
```
Client
├── Renderer (tilemaps, sprites, lighting, fog-of-war draw)
├── Input handling
├── UI / HUD / menus
├── Audio
├── Local single-player bootstrap (embeds Core Simulation directly)
```

The server project adds:
```
Server
├── Headless bootstrap (embeds Core Simulation, no rendering)
├── Network listener (ENet, authoritative)
├── Admin/API endpoints (for backend integration, prison lifecycle control)
├── Server performance profile & auto-tuning
```

Backend services (separate deployables, not part of the Godot project):
```
Backend
├── Accounts / Auth Service
├── Prison Registry & Lifecycle Service
├── Evolution Service (Escape Analyzer + Rule Engine + Generator)
├── Matchmaking / Server Browser
├── Replay & Asset Storage Gateway
├── Admin Dashboard
```

---

## 5. Technology Stack

| Layer | Technology | Rationale |
|---|---|---|
| Engine | **Godot 4 (C#)** | Open source (a hard requirement), mature 2D pipeline, supports headless server builds out of the box. |
| Language | **C# (.NET 8+)** | One language across client, server, and tools. Strong ECS library ecosystem, good performance, good tooling, easier hiring than niche alternatives. |
| Rendering | Godot `RenderingServer` / TileMap / custom drawing | Fast 2D rendering, GPU instancing for large tile counts. |
| Simulation core | Custom ECS (or a proven lightweight C# ECS library — see §7.1) | Tailored to an AI-heavy, entity-dense simulation; avoids per-entity class explosion. |
| Pathfinding | Hierarchical A* over per-floor navigation graphs, connected via stairs/elevators | Handles multi-floor buildings correctly and cheaply. |
| Visibility | Shared raycasting/shadow-casting FOV module | One implementation used by players, guards, cameras, dogs, towers — no separate "AI vision." |
| Networking | Godot's ENet transport (low-level) + a custom, versioned message protocol | Authoritative simulation; deterministic message types instead of relying on Godot's high-level RPC magic. |
| Serialization / Save Format | Custom versioned binary format (chunks → entities → components) | Never serialize language objects directly; must survive years of updates. |
| Database | **PostgreSQL** | Relational data: accounts, prison metadata, friendships, escape records, evolution history. Mature, scales well, strong tooling. |
| Cache / ephemeral state | **Redis** | Sessions, matchmaking, presence, rate limiting, temporary prison state. |
| Object storage | **MinIO** (self-hosted, S3-compatible) or Amazon S3 | Replays, save files, generated prison archives, screenshots, mods. Never store large blobs in Postgres. |
| Message bus | **NATS** | Lightweight pub/sub between backend services (escape events → evolution service → analytics/achievements/replay recorder). Chosen over RabbitMQ for simplicity. |
| Logging | **Serilog** (or Microsoft.Extensions.Logging abstractions) | Structured logs with levels (INFO/WARN/ERROR/DEBUG/NETWORK/AI). |
| Configuration | **TOML or YAML** | Human-readable server/gameplay configuration files. |
| Metrics/Observability | **Prometheus + Grafana** | CPU, AI timings, player counts, server health — critical because this game's whole premise depends on measuring itself. |
| CI/CD | **GitHub Actions** | Automated builds, tests, and dedicated-server deployment. |
| Deployment / Orchestration | **Docker Compose** (explicitly **not** Kubernetes) | Every backend service and the dedicated game server ship as containers, defined in `infra/` Compose stacks. GitHub Actions builds images and drives deploys (e.g., pushing images and re-running `docker compose up -d` on target hosts). See "Why not Kubernetes?" below. |
| Data definitions | **JSON or YAML** during development, compiled to an optimized binary cache at build time | Human-editable content, fast runtime loading. |

### Why not Java or C++?

- **Java** is viable (Minecraft proves it) but has a weaker game-dev ecosystem and tooling; only worth it if the team wants to hand-roll rendering/tooling from scratch. Not recommended here.
- **C++** offers maximum control but roughly doubles development time; unnecessary unless building a fully custom engine with a larger team.
- **Rust** has excellent server/networking/ECS characteristics but a younger game ecosystem; a reasonable future consideration for a rewrite of specific backend services, not the initial choice.
- **C# + Godot** gives the best balance of developer productivity, ecosystem maturity, and performance for a project of this ambition, while satisfying the open-source engine requirement.

### Why not Kubernetes? (Design Pillar #10)

Kubernetes solves problems this project does not have yet: multi-node auto-scaling, complex rolling deployments across a large cluster, and self-healing at a scale far beyond a handful of backend services and a set of dedicated game servers. Adopting it now would mean paying its operational complexity tax (a cluster to run, YAML manifests, RBAC, ingress controllers, etc.) for no corresponding benefit. **Docker Compose** — one `docker-compose.yml` (or a small set of them) per environment, deployed via CI/CD — is sufficient for: standing up the full backend stack (§11) locally and in production, running one or more dedicated game servers per host, and giving player-hosts a simple, copyable way to self-host a Community prison server (§10.1) without needing to understand container orchestration at all. Revisit this decision only if/when the number of independently-scaling services or hosts genuinely outgrows what Compose can reasonably manage — and if so, record that as an ADR (§15) rather than silently introducing Kubernetes.

---

## 6. Repository & Project Structure

```
prison/
├── client/                  # Godot project: rendering, UI, input, audio, local bootstrap
├── server/                  # Headless server bootstrap, network listener, admin API
├── shared/                  # The Core Simulation library — used by both client & server
│   ├── ECS/
│   ├── World/               # Tiles, chunks, floors, layers
│   ├── Pathfinding/
│   ├── Visibility/
│   ├── AI/
│   │   ├── Perception/
│   │   ├── Memory/
│   │   ├── Reasoning/       # Utility AI scoring, priorities
│   │   └── Actions/         # Patrol, investigate, chase, arrest, etc.
│   ├── Suspicion/
│   ├── Investigation/
│   ├── Generation/          # Blueprint assembly, style kits, decoration, validation
│   ├── Networking/          # Protocol definitions, serialization of messages
│   ├── Serialization/       # Save format
│   ├── Events/              # Event bus
│   ├── Telemetry/           # Escape recorder, heat maps, replay recorder
│   └── Utilities/
├── assets/                  # Textures, sprites, sounds, tiles, animations, UI, fonts, music, localization
├── content/                 # Data-driven game content (NOT code)
│   ├── tiles/
│   ├── items/
│   ├── recipes/
│   ├── jobs/
│   ├── schedules/
│   ├── uniforms/
│   ├── rooms/                # functional blueprints
│   ├── styles/                # architectural style kits
│   ├── decoration_rules/
│   └── families/              # prison family definitions (DNA + doctrine)
├── mods/                     # Reserved structure for future community mods (same shape as content/)
├── tools/                    # Offline tooling: prison generator CLI/preview, data validators, migration tools
├── backend/                  # Non-Godot backend services (Accounts, Prison Registry, Evolution Service, Matchmaking, Admin Dashboard)
│   ├── evolution-service/
│   ├── prison-registry/
│   ├── accounts/
│   ├── matchmaking/
│   └── admin-dashboard/
├── infra/                    # Docker Compose stacks (no Kubernetes — Pillar #10), CI pipeline definitions, monitoring configs
├── docs/                     # Design docs, architecture decision records (ADRs), onboarding guide
├── CONVERSATION.md            # Original design discussion (historical reference)
├── PLAN.md                    # This document
└── CLAUDE.md                  # AI-agent-facing repo guidance
```

Rules for this structure:
- `shared/` never references Godot-specific types directly where avoidable — keep it engine-agnostic where practical so the dedicated server can, in principle, run without loading full Godot rendering subsystems (Godot's headless mode already helps here, but discipline in `shared/` pays off for testability).
- `content/` and `mods/` contain **zero code** — only data files (JSON/YAML) and referenced assets.
- `tools/` should let a designer generate and preview a prison **without launching the full game**, to keep iteration on generation fast.

---

## 7. Core Simulation Systems

### 7.1 Entity Component System (ECS)

Every game object — guard, prisoner, chair, door, camera, generator, fence, dog, bullet, food — is an **entity** (just an ID) with attached **components** (pure data). **Systems** are the only code allowed to read/write components, and each system has one responsibility.

Example composition:
- A **janitor**: Position, Inventory, Cleaning, Pathfinding.
- A **prisoner**: Position, Inventory, EscapeAI, Needs, Relationships.
- A **camera**: Position, VisionCone, Power, Rotation.
- A **dog**: Position, Vision, Smell, AI.

Systems run independently and communicate through components and the event bus, never through direct references to each other's internals (e.g., the AI system never directly holds a `Pathfinder` instance it constructed — it receives one via dependency injection, see §15).

**Implementation decision:** start with a proven, lightweight C# ECS library (e.g., a source-generator-based one like Arch or DefaultEcs) to avoid reinventing archetype storage and query iteration from scratch. Only fall back to a fully custom ECS if integration friction with Godot/networking proves too costly — document that decision in an ADR (`docs/adr/`) if taken.

Systems list (initial set, expand over time): Movement, Vision, Hearing, Smell, Needs, Schedule, Inventory, Pathfinding Request/Resolve, AI Decision, Suspicion, Investigation, Combat, Crafting, Door/Lock, Power/Electricity, Damage/Repair, Communication/Radio.

### 7.2 World Representation & Tile System

The prison is not "just tiles" — it is a stack of **layers**, each independently updatable:

```
Floor · Wall · Furniture · Objects · Power · Water · Navigation · Visibility · Heat · Sound · Ownership
```

Tiles are never hardcoded by type. A tile is a bag of properties, defined in data:

```
Concrete Floor: movement_cost, visibility, sound, can_dig, can_burn, can_place_furniture, can_flood
Metal Floor: movement_cost, reflects_sound, cannot_dig, conducts_electricity
```

The engine code only ever asks a tile "what are your properties," never "are you concrete." This is what lets a designer add a "Glass Wall" purely by writing a data file, no code change required.

The world is organized into **floors** (for multi-story buildings, per the "multiple stairs" requirement) and **chunks** within each floor, for streaming and simulation-bubble purposes (see §7.10).

### 7.3 Multi-Floor Navigation & Pathfinding

Because the game explicitly supports multiple floors connected by stairs and elevators:

- Each floor has its own navigation graph.
- Floors are connected via designated stair/elevator nodes.
- Pathfinding runs **hierarchical A\***: first plan at the floor-graph level (which floors/stairwells to traverse), then resolve the fine path within each floor.
- Pathfinding requests are **shared and queued** — hundreds of guards must not each trigger an independent expensive pathfind in the same tick. A pathfinding worker pool serves requests from a priority queue (see §7.10 for how priority is assigned).

### 7.4 Visibility, Fog of War & Lighting

Two distinct systems, kept deliberately separate:

**World Visibility (perception):**
```
Entity → raycast/shadowcast from position, facing, FOV cone, max distance → set of currently-visible tiles
```
No raycast, no line of sight ⇒ no detection, ever. This module is used identically by the player's camera/fog-of-war rendering, and by every guard, dog, camera, and tower AI. There is exactly one implementation.

**Memory Visibility (fog of war rendering):**
A tile has three player-facing states:
1. **Unseen** — never observed, fully hidden (black).
2. **Currently visible** — actively observed this frame (full detail).
3. **Remembered** — previously observed, no longer visible (grayed out, last-known state frozen). Familiar from RimWorld, Project Zomboid, and most RTS games.

**Lighting** is a separate layer from visibility, not a replacement for it. A dark room reduces effective visibility distance (e.g., 3 tiles instead of the normal range); a flashlight or lit area extends it (e.g., 10 tiles). Visibility asks "is there a clear line of sight," lighting asks "how far does that line of sight reach before it's too dark to resolve detail."

### 7.5 Perception, Memory & Communication (Staff AI senses)

Every Staff AI-controlled entity (guards, medics, dogs, CCTV operators) runs the same sensing pipeline; only parameters differ per role/individual:

```
Perception → Memory → Reasoning → Decision → Action
```

**Vision:** position, head direction, field of view (~120° default, tunable), max distance, peripheral vision — resolved via the shared Visibility module (§7.4). No line of sight ⇒ no detection.

**Hearing:** every action emits a sound with a radius. Example baseline values (tune during playtesting):

| Action | Radius |
|---|---|
| Walking | 2m |
| Running | 8m |
| Fighting | 25m |
| Explosion | 60m |
| Metal cutting | 20m |
| Digging | 15m |

If an entity is within a sound's radius, an *investigation event* is generated for it — not an instant, certain detection.

**Smell (dogs only):** an additional sensor keyed to contraband, blood, fresh tunnels, food, etc.

**Radio/Communication:** guards do not have a shared hive mind. A sighting becomes an event that must be *reported* (e.g., "Guard A sees player" → "Escape in Block C" broadcast), received by nearby guards or a control room, and applied with a **realistic delay** (a few seconds, tunable by infrastructure state). If the radio tower/infrastructure is destroyed or degraded, communication delay increases — this is a legitimate, physically-grounded escape strategy, not an exploit.

**Memory (belief, not omniscience):** a guard never knows "the player is in the laundry right now." It knows "I last saw the prisoner at 10:42 in the laundry," with a **confidence value that decays over time** (e.g., 100% → 75% → 50% → 20% → forgotten). Reasoning and investigation act on this belief state, not on ground truth.

### 7.6 Staff AI Decision-Making

Two viable models, and this project should default to the second:

**Behavior Trees** (classic, scalable, simple to reason about):
```
Patrol → hear sound? → investigate → nothing? → resume patrol → see player? → chase → lost? → search area → give up
```

**Utility AI** (recommended default — smoother, more dynamic, avoids rigid tree branching): every candidate action gets a numeric score each decision tick, and the highest-scoring action is chosen.

```
Continue patrol = 12
Investigate      = 55
Chase             = 91
Arrest            = 100
Rest              = 30
```

Priority ordering for reference (used to seed the utility scoring): escort prisoner, patrol, search, investigate a sound, help another guard, fight, call backup, return to patrol.

**Internal state / personality parameters** modulate scoring per individual guard: bravery, aggression, alertness, discipline, experience, fatigue, stress, trust. Example: a rookie has higher stress (+80%), reacts slower, and calls backup sooner; a veteran has lower stress (+10%), investigates longer, and better predicts escape routes. This produces variety without any guard ever "learning" mid-match.

### 7.7 Suspicion & Investigation Systems

Each prisoner has a **threat score**, computed purely from *observed* signals:

- Time spent outside their scheduled location
- Seen near contraband
- Seen with tools
- Running
- Entering restricted areas
- Talking with already-suspicious inmates
- Previous escape attempts
- Recent search results
- Guard/staff reports

Example progression: Normal inmate (threat 10) → caught stealing (threat 45) → seen at a tunnel (threat 82) → placed under constant surveillance. Everything here is derived from in-fiction observations — never from hidden, ground-truth game state.

**Investigation AI**, when a suspect goes out of sight, does not simply forget:
```
Last known location → generate a search radius → split available guards → search rooms → expand radius if nothing found → resume patrol
```

### 7.8 Escapist Player Mechanics

The player-facing gameplay is unaffected by all of the above — only the prison around it changes generation to generation. Player-facing systems to implement: crafting items, building relationships with NPCs, stealing tools, distracting guards, fighting, tunneling, disguising, cutting fences, hiding contraband, and managing personal schedules. These are systems in their own right (Inventory, Crafting, Relationship/Reputation, Combat, Digging, Disguise, Lockpicking, Diversion) that hook into the shared event bus (digging emits a `DiggingEvent`, disguising emits a `DisguiseEvent`, etc.) so the Staff AI, telemetry, and heat maps all react to the same signals without being hard-wired to each other.

### 7.9 Event Bus & Telemetry

An event bus decouples producers from consumers. Example: a `DiggingEvent` from the player's digging action can simultaneously feed the Sound System (hearing propagation), Guard AI (investigation trigger), Heat Map (aggregate statistics), Statistics, Achievements, and the Replay Recorder — without any of those systems referencing each other.

A dedicated **Telemetry module** inside the shared simulation, present from day one (observability is as important as gameplay in a game whose whole premise is adaptation):

```
Telemetry
├── Escape Recorder     (full path, time, items, doors, NPC interactions, damage, distance, rooms visited, disguises, digging, combat, lockpicks, diversions, noise, guard detections)
├── AI Statistics
├── Performance Metrics
├── Heat Maps           (most-used corridor, most-escaped fence, most-stolen room, most broken wall, most blind cameras, etc.)
├── Path Usage
├── Item Usage
├── Vision Statistics
└── Replay Recorder
```

Every important system emits structured events (e.g., "guard lost sight," "door forced open," "tunnel discovered"). For player-hosted prisons these events stay local unless the host opts in to sharing (§10.3); for official servers they always feed the evolution pipeline (§9).

### 7.10 AI Scaling: LOD, Scheduler, Budget, Simulation Bubbles

This is the single most important non-gameplay system in the project, since prisons may contain thousands of prisoners and hundreds of staff.

**Do not run full AI on every entity, every frame.** Instead:

**Simulation LOD (Level of Detail):**

| LOD | Condition | Behavior |
|---|---|---|
| 0 | Player is actively watching | Full 60Hz perception, decisions, animations, pathfinding |
| 1 | Same building, not directly observed | ~10Hz, reduced perception, simplified pathfinding |
| 2 | Other side of the prison | ~2Hz, patrol only, simplified navigation |
| 3 | Far away | No animation/pathfinding; discrete event resolution only (e.g., "guard reached cafeteria," instantly) |
| 4 | Region/wing unloaded/unobserved | Statistical simulation only (e.g., "no incidents, continue schedule") |

**Simulation bubbles:** areas with no observing player run the cheap event-level simulation; the moment a player enters, the detailed simulation for that area spins up. This is the same "world bubble" concept used in large open-world/city-builder games.

**AI scheduler**, not "every entity thinks every tick":
- **Perception** updates at different rates by importance (chasing guards update far more often than calmly patrolling ones).
- **Decision-making** runs only on relevant triggers (new sighting, radio message, waypoint reached, sound heard) rather than every tick.
- **Pathfinding** is served from a shared, queued worker pool so hundreds of guards don't all request paths simultaneously.
- **Communication** is event-based with realistic delay (§7.5), not instantaneous.
- **Background staff** (janitors, cooks, librarians, office workers) run simplified routines until a player interacts with or approaches them.

**Priority-based CPU allocation**, so the system degrades gracefully under load instead of uniformly starving everyone:

| Situation | Priority |
|---|---|
| Player nearby | 100 |
| Chasing | 90 |
| Investigating | 70 |
| Talking | 30 |
| Sleeping | 10 |

**Dynamic AI budget:** the server measures its own CPU usage; if it's overloaded, it reduces update rates or entity counts at the low-priority end first (e.g., degrade from "300 guards at 10Hz" to "300 guards at 5Hz + reduced far-simulation," rather than dropping frames uniformly). See §12 for how this becomes a player/host-facing "performance profile."

### 7.11 Networking & Server Authority

The simulation must be **server-authoritative** whether "the server" is official infrastructure or a player's own machine — the distinction between hosting models should be nearly invisible to the simulation code:

```
                Authoritative Host
                     │
      ┌──────────────┴──────────────┐
      │                              │
 Prison State                 AI Simulation
      │                              │
      └──────────────┬───────────────┘
                     │
           Clients receive updates
```

Use Godot's ENet transport as the low-level channel, but define an explicit, versioned message protocol rather than relying on Godot's high-level RPC magic — this keeps behavior deterministic and makes cross-version compatibility and debugging tractable. Message categories to define explicitly: Move, Inventory, Door, Fight, Chat, Craft, Animation, Sound, Vision, AI-state-sync.

The simulation runs on a **fixed tick** (e.g., 20 ticks/sec), completely decoupled from render framerate:

```
Input → Simulation → AI → Physics → Network → Render
```

### 7.12 Save/Load & Serialization

Never serialize language objects directly (no `BinaryFormatter`-style object graphs). Use an explicit, versioned structure:

```
World → Chunks → Entities → Components → Binary (versioned)
```

This must be fast, forward-compatible (old saves loadable after updates), and independent of any particular class layout in code, so refactors don't silently break save compatibility. Write a migration mechanism from the start (a save file declares its format version; a loader chain upgrades old versions to current).

---

## 8. Prison Generation Pipeline

Generating *a valid* prison is easy. Generating *a prison that feels hand-designed, matches its family's identity, and specifically counters how it was previously beaten* is the hard, central problem of this project. It is solved with a **pipeline of specialized passes**, not one monolithic generator — think of it as a virtual studio: a Planner, then an Architect, then a Security Engineer, then an Interior Designer, then a Landscaper, then a Quality Inspector, each with one narrow job.

### 8.1 Prison Families & Architectural DNA

Every **official** prison belongs to a **Family** (e.g., "Blackstone") which persists across generations — a family is never discarded and replaced with an unrelated one; it *evolves*, like a Pokémon evolving into a new form rather than being deleted and replaced by an unrelated creature.

A family carries two heritable, mostly-stable identities:

**Architectural DNA** (visual/structural identity — changes slowly):
```
Age, Architecture style, Capacity, Region/climate, Floor count,
Preferred cell block shape, Preferred tower shape, Preferred window size,
Roof type, Decoration density
```

**Warden Doctrine** (gameplay/security identity — mutates in direct response to escapes):
```
Number of guards, Number of cameras, Prison size, Security level, Door density,
Patrol frequency, Yard size, Workshop placement bias, Fence layers, Search frequency,
Lighting policy, Prison schedule, Cell spacing, Watchtower count, Guard AI aggressiveness,
Contraband tolerance
```

Every successful escape mutates the Doctrine in a targeted way. Examples:

| Player exploited | Doctrine mutation |
|---|---|
| Tunnels | Increase concrete flooring, increase random searches, decrease tool access, add ground microphones/dogs |
| Disguises | Random ID checks, two-person verification, restricted uniform access |
| Riots | Faster riot response, prisoner segregation, emergency lockdown procedures |
| Laundry as a blind spot | Insert additional checkpoints/surveillance around laundry |

The AI changes **procedures and layout**, never the underlying physics of perception (Pillar #2). A prison never becomes "psychic" — it becomes better staffed, better arranged, and better policed, transparently, from data.

Optionally, seed initial families with distinct **starting personalities** to give early variety before evolution has had time to differentiate them organically:

- **Military** — many guards, towers, checkpoints, patrols; weak at paperwork/disguise detection.
- **Bureaucratic** — many doors, keycards, scanners; few guards.
- **Cheap** — few guards, poor maintenance, easy tunnels, broken cameras.
- **Maximum Security** — few prisoners, high surveillance, no contraband, rigid routines.

These personalities are just initial Doctrine presets; they still evolve independently afterward, and over hundreds of generations may converge or diverge unpredictably — that unpredictability is intended.

### 8.2 Content Libraries

Three independent, reusable libraries feed the generator. Keeping them independent is what makes variety scale multiplicatively instead of requiring one handcrafted asset per style per room:

```
                Prison Generator
                       │
        ┌──────────────┼──────────────┐
        │              │              │
 Functional      Architectural     Decoration
 Blueprints         Style Kits       Rule Sets
```

**Functional Blueprints** define *how a room works*, independent of appearance: doors, furniture positions, equipment, navigation/connection points, capacity, gameplay tags (e.g., "Medium Cell Block," "Small Kitchen," "Guard Station"). Each blueprint exposes explicit connection points ("a corridor may exit here," "utility lines enter here") and metadata ("supports 80 inmates," "high surveillance," "good for rehabilitation") so the assembler can place, rotate, and mirror it correctly.

**Architectural Style Kits** define *how it is built*, purely visually: wall material, floor material, window shape, roof edges, columns, colors, trims (e.g., Modern, Victorian, Soviet, Industrial, Scandinavian, Brutalist Concrete). The same kitchen blueprint rendered in two different style kits is gameplay-identical but visually unrecognizable as the same room.

**Decoration Rule Sets** define *how it feels lived-in*: procedural placement rules for pipes, warning signs, dirt/damage level, maintenance clutter, plants, electrical cabinets, wall cracks. Purely cosmetic, applied as a final pass after layout and style are locked in.

Multiplicative variety: ~60 functional blueprints × ~8 style kits × ~10 decoration rule sets does not require ~4800 handcrafted assets — it requires roughly 60 room *designs*, 8 construction "languages," and 10 environmental "personalities," combined by the pipeline. A family keeps a consistent style kit and mostly-consistent decoration rules across generations so players recognize "this is definitely another Blackstone prison" even after major layout changes.

### 8.3 Generation Pipeline Steps

```
Random Seed + Family DNA/Doctrine
      │
      ▼
1. Design Intent          (resolve family DNA into concrete parameters: security level, age, capacity, floors, philosophy)
      │
      ▼
2. Functional Zones       (district-level planning: Administration, Cell Blocks, Kitchen, Laundry, Workshop, Medical,
      │                     Visitation, Yard, Security, Maintenance — think city planning, not room placement yet)
      ▼
3. Circulation            (how do guards move? how do prisoners move? where are checkpoints and emergency exits?
      │                     resolved before any building is actually drawn)
      ▼
4. Architecture Assembly  (within each district, assemble Functional Blueprints from the library, following
      │                     architectural grammar rules — e.g. a cell block always expands as
      │                     Cells → Hallway → Cells, or Cells → Guard Desk → Cells)
      ▼
5. Gameplay Validation    (automated tests — see §8.4 — discard and regenerate if failed)
      │
      ▼
6. Security Optimization  (apply Warden Doctrine: guard counts, camera placement, patrol routes, checkpoints)
      │
      ▼
7. Beautification Pass    (apply the family's Architectural Style Kit: windows, benches, trees, flower beds,
      │                     cracks, stains, pipes, vents, signs, lights — purely visual, zero gameplay impact)
      ▼
8. Storytelling Pass      (apply the family's Decoration Rule Set: broken vending machine, old riot damage,
      │                     graffiti, abandoned offices, flooded maintenance tunnels — believability details)
      ▼
9. Exterior Generation    (procedurally generate vegetation, parking, roads, fences, towers, terrain — see §8.5;
      │                     nobody memorizes exteriors the way they memorize interiors, so pure procedural
      │                     generation is acceptable here, unlike interiors)
      ▼
10. Quality Scoring       (believability/realism, enjoyability, symmetry, visual variety, walking distances,
      │                     guard coverage, room proportions, density — reject and regenerate if below
      │                     threshold; escapability is deliberately not scored, see §8.4)
      ▼
11. Simulation Testing    (run AI-only simulations: do guards patrol smoothly? do prisoners get stuck? does
      │                     lunchtime create traffic jams? are there blind spots? reject if problems found)
      ▼
Published Prison Candidate
```

Introduce deliberate small imperfections after symmetric layout generation (one damaged room, a shifted corridor, an extra storage closet) — perfect symmetry reads as artificial; controlled imperfection reads as believable.

### 8.4 Validation & Scoring

**Escapability is deliberately not a validation criterion.** Per Design Pillar #9, the long-term goal is a prison that becomes ever harder — potentially, for an old and much-evolved family, practically impossible to escape. Testing "can this prison still be escaped?" and rejecting candidates that fail it would directly fight the game's own core loop, so that test is intentionally excluded from this pipeline. (An earlier draft of this plan included such a test; it has been deliberately removed.)

What *is* required, unconditionally, on every candidate regardless of how secure it has become:

**A. Structural/functional validation (binary pass/fail — the prison must simply work as a facility):**
- Can every prisoner reach food, their assigned cell, work assignments, and medical care per the schedule? (Yes required)
- Can guards and staff reach every area they need to patrol, maintain, or respond to? (Yes required)
- Is every cell reachable by staff (for searches, escort, emergencies)? (Yes required)
- Can the prison deadlock (e.g., a door/keycard loop with no valid path for required logistics)? (No required)
- Is performance (pathfinding cost, entity density) acceptable at the target simulation budget? (Yes required)

**B. Believability & enjoyability validation (required on every candidate, not optional polish):**
- **Realism/plausibility** — would a reasonable person believe this facility could exist in the real world? Reject layouts that are structurally valid but architecturally absurd (physically implausible room adjacencies, nonsensical circulation, security theater with no coherent logic).
- **Enjoyability** — is the space interesting to move through and interact with, regardless of how hard it is to escape? Reject candidates that are merely tedious mazes, monotonous corridors, or featureless boxes — high security must be expressed through interesting, legible design (checkpoints, sightlines, varied room shapes), not through boredom.

Only candidates passing **all** of (A) and (B) proceed to Quality Scoring — symmetry, visual variety, walking distances, guard coverage, room proportions, empty space, density (§8.3 step 10). Failing or low-scoring candidates are discarded and regenerated — this loop happens **offline, before any player ever sees the result** (see §9.3 for how this fits the "generate several candidates, pick the best" strategy).

### 8.5 Exterior Generation

Exteriors (vegetation, parking lots, roads, fences, towers, ponds, forests, rocks, cliffs) are generated fully procedurally with no blueprint library — players spend the vast majority of playtime indoors and don't memorize exterior details the way they memorize interior layouts, so the cost/benefit of hand-authored exterior blueprints is poor. Interiors always use the Blueprint + Style + Decoration pipeline above; exteriors do not need to.

---

## 9. The Two AIs: Evolution AI vs Staff AI

Restating Pillar #1 with implementation detail. These are **two entirely separate codebases/services**, not two modes of one system.

### 9.1 Escape Analyzer

Every match's Telemetry (§7.9) — path taken, time, items used, doors opened, NPC interactions, damage, distance walked, rooms visited, disguises, digging, combat, lockpicks, diversions, noise, guard detections, and the resulting heat map — is recorded. The Escape Analyzer converts a raw escape into structured **weakness signals**, for example:

```
Weakness: tunnel started in Cell 14           → tunnel_score: +9 danger
Weakness: laundry not monitored               → laundry_score: +6
Weakness: guard disguise worked                → disguise_score: +8
Weakness: no patrol near east fence            → east_fence_score: +10
```

Crucially, the Evolution AI never consumes raw player input/replays directly — it only consumes these structured, translated signals. This keeps the system debuggable, balanceable, and safe (no risk of a generator overfitting to noise in raw input streams).

At scale, this also aggregates across **many** escapes, not just the most recent one — e.g., "72% of escapes on this family used the laundry route" is a far stronger, more reliable signal than any single escape, and should weigh more heavily in mutation decisions.

### 9.2 Evolution / Rule Engine

The Evolution AI turns weakness signals into concrete Doctrine/DNA mutations (§8.1). Start **rule-based** (a genetic-algorithm-style mutation engine over the Doctrine/DNA parameter set), not machine-learning-based:

```
Escape Logs → Escape Analyzer → Weakness Scores → Evolution/Rule Engine
                                                          │
                                                          ▼
                                              Updated Prison DNA/Doctrine
                                                          │
                                                          ▼
                                               Procedural Generator (§8)
                                                          │
                                                          ▼
                                                 New Prison Candidate(s)
```

A rule-based/evolutionary-algorithm approach produces the feeling of an adaptive, learning prison while remaining understandable, debuggable, and controllable by the team — this matters enormously for explaining to players (and to yourselves) *why* a prison changed a specific way. Machine-learned models can be introduced later for narrow, well-scoped subtasks (e.g., optimizing patrol routes, predicting likely escape paths) once the rule-based foundation is solid and well-instrumented — the core evolutionary game loop does not require neural networks to deliver its central fantasy.

**The mutation engine is a one-way ratchet, not a balancer (Pillar #9).** Its objective function is *not* "keep this prison beatable" or "keep win rate near X%" — there is no target escape rate to balance toward. Each mutation should be a *targeted, permanent* countermeasure to a real, observed weakness (per the Escape Analyzer's signals, §9.1), and mutations should not be walked back later just because a prison has become hard to escape. The only forces that can veto or reshape a mutation are the Believability & Enjoyability validation gates (§8.4) and, optionally, an administrator (§9.3) — never "it's gotten too hard." Over hundreds of generations, a family is expected to trend toward a very low, possibly near-zero escape rate; that is success, not a bug to fix. This is also why aggregating across many escapes (not just the latest one) matters so much for a given mutation — it keeps the ratchet targeted at real, recurring weaknesses instead of overreacting to a single lucky or exploit-driven run.

### 9.3 Candidate Generation & Human Approval Gate

When an official prison is compromised (see §10.2), do not generate exactly one replacement. Generate **several candidates** from the mutated DNA/Doctrine, run each through the full validation and simulation-testing pipeline (§8.3–8.4), score them, and deploy only the best:

```
Escape Confirmed
      │
      ▼
Generate N Candidates (e.g., 10)
      │
      ▼
Automatic Validation (per candidate)
      │
      ▼
Quality Scoring (per candidate)
      │
      ▼
Administrator Review (optional — most of the time, unnecessary)
      │
      ▼
Schedule Deployment (respecting the minimum 1-day delay, §10.2)
      │
      ▼
Official Prison Goes Live; old generation archived
```

Most of the time the system auto-deploys the highest-scoring candidate with no human involvement. The **optional** administrator review step exists as a safety valve — procedural/adaptive systems occasionally produce technically-valid-but-unenjoyable results, and admins should be able to inspect, reject, or force-regenerate before anything goes live, especially early in development or if the evolution algorithm ever misbehaves.

---

## 10. Prison Hosting Model & Lifecycle

### 10.1 Official vs Community Prisons

| Property | Official (Centralized) | Community (Player-Hosted) |
|---|---|---|
| Hosting | Project's own infrastructure | A player's machine (or their own dedicated box) |
| Visibility | Always public | Public, private, or friends-only (via in-game friendship system) |
| Escape data sharing | Always on, mandatory | Optional, host-controlled toggle |
| Feeds global evolution | Yes | Only if sharing is enabled |
| Appears in rankings | Yes | No |
| Can receive special/curated events | Yes | No |
| Managed by administrators | Yes | Only the host (self-managed) |
| Impact if data sharing disabled | N/A (never disabled) | Prison still evolves — but *only* from its own local history, with zero effect on the global/official ecosystem |

`HostType` (Official / Community) is stored as **metadata on the prison record**, not as a separate code path — the simulation, generation, and hosting code is identical either way; only policy (data routing, visibility rules, admin permissions) differs based on this flag. This is important for maintainability: there should never be an "official-only" bug that a player-hosted server can't hit, or vice versa.

Administrators must be able to view and (for official prisons) set/change this metadata through an admin tool (§11.5 / the Admin Dashboard service).

### 10.2 Prison Lifecycle State Machine

Official prisons are **not** continuously or arbitrarily regenerated. A new generation only comes into existence because players collectively defeated the previous one — this gives each official prison a persistent identity that players get to know, master, and eventually beat, rather than a disposable, forgettable level.

```
Generating → Testing → Ready → Active → Compromised → Retiring → Archived
```

- **Generating** — a candidate is being built by the pipeline (§8.3).
- **Testing** — automated validation/simulation testing in progress (§8.4).
- **Ready** — passed all checks, queued for deployment.
- **Active** — live, open to players, escape data being collected.
- **Compromised** — triggered the instant a *valid* escape occurs. The prison does **not** disappear immediately: players already inside can finish their session, and it remains fully playable for a **minimum of 24 hours** after being marked Compromised.
- **Retiring** — the 24-hour window has elapsed; a replacement generation is being produced (candidate generation, §9.3) while the old generation is still technically live/frozen for reference.
- **Archived** — the old generation is retired from active play but preserved (see the optional "Museum mode" idea in §14, Phase 15) so its place in the family's evolutionary history remains visible.

Why the minimum 1-day delay specifically (not instantaneous replacement):
- It makes prison replacement feel significant and event-worthy, rather than trivial.
- It gives administrators a window to verify the escape was legitimate (not caused by a bug or exploit) before it drives real evolution.
- It allows the community to be told in advance ("Prison 17 — Status: Compromised — Replacement arrives in 18h 32m").
- It lets players directly compare "the prison before" and "the prison after," reinforcing the evolutionary narrative.

Every official prison generation stores a `ParentPrisonId`, forming an explicit lineage:

```
Prison 42 → Prison 57 → Prison 63 → Prison 88   (all one Family, e.g. "Blackstone")
```

This lineage is itself a player-facing artifact worth surfacing (rankings, family history pages, and eventually the Museum mode).

### 10.3 Data Sharing & Privacy Rules

- Official prisons: escape data sharing is **always on** and non-optional — this is the fuel for the global evolution pipeline, and players choosing to play on official infrastructure are implicitly opting into this.
- Community (player-hosted) prisons: the host controls a `ShareEscapeData` toggle at creation and possibly changeable later.
  - If **on**: escape events feed both the local prison's own evolution *and* (in a suitably anonymized, aggregated form) the shared global pool, exactly like official data.
  - If **off**: the prison still evolves — using only its own local escape history — but contributes nothing to the wider ecosystem and imposes no privacy cost on that host's players.
- The global evolution service must never download or replay raw player games. It only ever receives already-summarized structured events (the Escape Analyzer's output, §9.1) such as "tunnel escape from cell block B succeeded after 14 minutes" or "guard lost line of sight in laundry corridor" — never raw input streams, video, or anything individually identifying beyond what's necessary for anti-cheat/legitimacy verification.

This produces three distinct scales of adaptation, worth stating explicitly since they're easy to conflate:
1. **During a match** — guards react only to what they can physically perceive (Staff AI, §9 / §7.5–7.7). No learning happens here.
2. **Between generations of one specific prison** — that prison's own Doctrine/DNA evolves from its own history (Evolution AI, §9.2), regardless of hosting type.
3. **Across the whole community (opt-in)** — official prisons and consenting community prisons contribute to a global pool, so brand-new prison families can start already informed by lessons learned from a much larger sample, without ever giving any individual guard impossible, physically-ungrounded knowledge.

### 10.4 Administration Tools

Minimum required admin capabilities, exposed via the Admin Dashboard backend service (§11):
- View/set a prison's `HostType` (Official/Community) and `Visibility`.
- View full lifecycle state and history/lineage of any official prison family.
- Review pending candidate generations (optional human approval gate, §9.3) — inspect, approve, reject, or force-regenerate.
- Investigate a flagged escape for legitimacy (e.g., suspected exploit or bug) before it's allowed to drive evolution.
- Force a status transition (e.g., manually retire a broken prison early) in emergencies.

---

## 11. Backend Services & Data Model

### 11.1 PostgreSQL Schema

Core tables (columns are illustrative starting points, not exhaustive — expand as needed, but keep this as the initial target):

**Players**
| Column | Notes |
|---|---|
| PlayerId | PK |
| Username | |
| CreatedAt | |
| ... | account/profile fields |

**Friends**
| Column | Notes |
|---|---|
| PlayerIdA / PlayerIdB | friendship edge |
| Status | pending/accepted/blocked |

**Prisons**
| Column | Notes |
|---|---|
| PrisonId | PK |
| FamilyId | groups generations of the same identity (§8.1) |
| Generation | evolution generation number within the family |
| HostType | `Official` \| `Community` |
| Status | `Generating` \| `Testing` \| `Ready` \| `Active` \| `Compromised` \| `Retiring` \| `Archived` |
| OwnerId | null for official prisons; player id for community prisons |
| Visibility | `Public` \| `Private` \| `FriendsOnly` (community only) |
| CreatedAt | |
| RetireAt | scheduled replacement time, set once Compromised |
| ShareEscapeData | boolean; always true for official |
| ParentPrisonId | previous generation, forming the lineage tree (§10.2) |

**PrisonVersions** — denser record of each generation's DNA/Doctrine snapshot, blueprint/style/decoration choices, and quality scores (for reproducibility, debugging, and the eventual Museum mode).

**Escapes**
| Column | Notes |
|---|---|
| EscapeId | PK |
| PrisonId | which generation |
| PlayerId(s) | who escaped |
| StartedAt / CompletedAt | |
| Legitimate | bool, set after admin/automated review |

**EscapeEvents** — the granular, structured telemetry stream per escape (path, items, doors, detections, etc. — §7.9/§9.1), keyed to `EscapeId`.

**Statistics** — aggregated heat-map-style rollups (per prison, per family, global).

**Achievements**, **Items**, **Reports** (player reports/moderation), **Servers** (registry of active dedicated/community servers, for the server browser/matchmaking).

### 11.2 Redis Usage

- Session tokens / online presence.
- Matchmaking queues.
- Rate limiting (auth, API abuse).
- Ephemeral/temporary prison state that doesn't need durable storage.

### 11.3 Object Storage

Use MinIO (self-hosted, S3-compatible) or Amazon S3/Backblaze B2 — never store large blobs directly in PostgreSQL:
- Replays.
- Save files.
- Screenshots.
- Generated prison archives (for the Museum mode / historical record).
- Mods.

### 11.4 Message Queue / Event Bus Topics (Backend, not to be confused with the in-simulation Event Bus of §7.9)

Use NATS to connect backend services without them blocking on each other. Minimum topic set:

```
escape.finished          → published when a match's escape (or failed attempt) concludes
prison.compromised       → published when an official prison transitions to Compromised
prison.candidate.ready   → published when a generated candidate finishes validation/scoring
prison.deployed          → published when a new generation goes live
```

Subscribers: Evolution Service (consumes `escape.finished`, `prison.compromised`), Analytics, Achievements service, Replay Recorder archival, Admin Dashboard (for live status).

### 11.5 Evolution Service

A standalone backend service (not embedded in any game server process), so game servers never freeze or lag while a new prison is being generated:

```
Official Server → Escape Data → Message Queue (NATS) → Evolution Service → New Prison DNA/Doctrine → Database
```

Internally, this service runs the Escape Analyzer (§9.1), the Evolution/Rule Engine (§9.2), and orchestrates candidate generation + validation + scoring + optional admin approval (§9.3, §8.3–8.4) before publishing a `prison.deployed` event.

### 11.6 Observability

- **Prometheus + Grafana** for CPU, AI timing, player counts, per-server health, and pipeline throughput (how long generation/validation/evolution take).
- **Structured logging** (Serilog) with explicit levels including a dedicated `AI` and `NETWORK` level, since debugging AI and network issues in this kind of game is otherwise extremely painful.
- Given the game's entire premise is "the system measures and adapts to itself," under-investing in observability directly undermines the core design — treat it as a first-class deliverable, not an afterthought.

---

## 12. Performance, Scalability & Settings

Players understand *performance*, not *simulation quality* — never expose raw simulation internals as a player-facing choice (e.g., never ask a host to pick "Small Server Simulation" vs "Big Server Simulation" directly). Instead expose **intent-based profiles**, and keep **graphics settings completely independent** of simulation settings, since a host's CPU and GPU are different bottlenecks.

### 12.1 Server Performance Profile (visible only when hosting)

| Profile | Intended use |
|---|---|
| Lightweight | Small home servers, 1–20 players |
| Balanced | Most PCs, 20–60 players |
| High Capacity | Powerful PCs, 60–150 players |
| Dedicated | Official servers or powerful dedicated machines |

The chosen profile only adjusts internal precision knobs — **gameplay rules never change**:

| Setting | Lightweight | Dedicated |
|---|---|---|
| AI updates/sec | Lower | Higher |
| Far-away NPC simulation | More simplified | More detailed |
| Pathfinding workers | 1 | 8+ |
| Max simultaneous investigations | 20 | 200 |
| Max active prisoners | 300 | 3000 |
| Tick rate | Adaptive | Fixed |
| Background simulation | Simplified | Full |

Do **not** implement this as literally separate "small" and "big" simulations — implement **one** simulation with a configurable computational budget (see §7.10's LOD/scheduler/priority system), and let the profile presets simply set that budget's parameters. This has real long-term payoffs: escape strategies remain transferable across every hosting tier, bugs are reproducible against one model instead of several, a prison hosted on a home PC today can migrate to official infrastructure tomorrow with no behavior change, and future hardware improvements are just a matter of raising the default budget rather than redesigning the AI.

```
                 Prison Simulation
                        │
        ┌───────────────┼───────────────┐
        │               │               │
   AI Budget      Rendering Budget   Network Budget
        │               │               │
  Auto or Manual   Graphics Settings   Max Players
```

### 12.2 Graphics Settings (fully independent of simulation)

Graphics settings control rendering only, and must never be coupled to AI/simulation precision (a player might have a powerful GPU and a weak CPU, or the reverse — these are different bottlenecks that must be tunable independently):

- **Low:** fewer shadows, shorter draw distance, low-resolution textures, fewer particles.
- **Ultra:** advanced lighting effects, volumetric fog, high-resolution textures, dynamic reflections.

### 12.3 Auto-Tuning

Provide an **Auto** mode for hosts so most people never have to manually tune anything:

```
On server start: benchmark CPU → benchmark memory → benchmark pathfinding → estimate AI capacity → choose initial profile
Continuously:     measure frame time → measure simulation time → adjust AI budget
```

This lets the server continuously self-adapt rather than forcing hosts to manually tweak dozens of technical knobs.

---

## 13. Modding Support

Not required at launch, but the entire content pipeline must be designed for it from day one, because retrofitting data-driven content later is far more expensive than building it that way from the start (Pillar #4). Concretely:

- Everything gameplay-relevant (furniture, jobs, uniforms, foods, tools, walls, cameras, functional blueprints, style kits, decoration rules) is defined as data under `content/`, never hardcoded in engine code.
- `mods/` mirrors the same directory shape as `content/` so a mod is just an overlay/addition of the same kind of data:

```
mods/
  ExampleMod/
    walls/
    items/
    recipes/
    textures/
    scripts/   (if/when a scripting hook is exposed — treat this as a later, carefully-scoped addition, not a day-one requirement)
```

- Document the data schemas thoroughly (see §17 for starter examples) so future modding documentation can be generated largely from the same schema definitions used internally.

---

## 14. Development Roadmap

This roadmap sequences the system design above into buildable phases. Each phase should produce something concretely testable before moving on — do not attempt to build networking, generation, and AI simultaneously from scratch; each layer should be validated in a simple, controlled context first.

### Phase 0 — Foundations & Tooling
- Establish the repository structure from §6.
- Set up the Godot 4 (C#) project skeleton for `client/` and a minimal headless bootstrap for `server/`.
- Decide and integrate an ECS approach for `shared/` (evaluate a proven lightweight C# ECS library first; fall back to custom only if integration friction is too high — record the decision as an ADR).
- Set up structured logging (Serilog) and a config format (TOML/YAML) from the start.
- Set up CI (GitHub Actions): build client, build server, run unit tests, on every push.
- Author the first `infra/` Docker Compose stack (even if it only contains a placeholder server container) and a CI job that builds and pushes its image — establish the Compose-based deployment habit immediately rather than bolting it on later (Pillar #10, no Kubernetes).
- **Deliverable:** an empty but running Godot project with a headless server stub, shared ECS library wired into both, CI green, and a working (even if trivial) Docker Compose deploy path.

### Phase 1 — Core Simulation Skeleton (single-player, hand-built test map, no AI, no generation, no networking)
- Implement the layered World/Tile system (§7.2) with a small hand-authored test prison.
- Implement multi-floor navigation graphs + hierarchical A* pathfinding, including stairs (§7.3).
- Implement the shared Visibility module (raycasting FOV) and the three-state fog of war (unseen/visible/remembered) (§7.4), plus lighting as a separate layer.
- Implement basic player movement/collision as ECS entities/components.
- **Deliverable:** a player character can walk a hand-built multi-floor test prison, pathfind up/down stairs, and see fog of war correctly update with no AI present yet.

### Phase 2 — Staff AI (single-player, one guard type)
- Implement Perception (vision cone via the shared Visibility module, hearing radius events) (§7.5).
- Implement Memory (belief state with confidence decay) (§7.5).
- Implement Utility AI decision-making with the initial priority set (patrol/investigate/chase/arrest) (§7.6).
- Implement basic radio/communication delay simulation (§7.5).
- Implement the Suspicion system (§7.7) and Investigation AI (§7.7) for a single prisoner/guard pair.
- Build the AI scheduler skeleton (§7.10) even with only a handful of NPCs, so the architecture is right before scale is needed.
- **Deliverable:** a guard patrols, hears/sees the player only via physical raycasts/sound radii, investigates, and can chase/catch the player, with no cheating and no mid-match learning.

### Phase 3 — Escapist Gameplay Mechanics
- Implement Inventory, Crafting, and basic interactable objects (doors, tools, contraband) (§7.8).
- Implement digging, disguises, lockpicking, and diversions, each emitting events on the event bus.
- Wire these events into the Staff AI (investigation triggers) and a first version of Telemetry (§7.9).
- **Deliverable:** a player can dig a tunnel or don a disguise and successfully or unsuccessfully evade a guard, purely through emergent interaction between the mechanic and the Staff AI senses.

### Phase 4 — Telemetry & Event Bus Maturity
- Formalize the Event Bus (§7.9) as a first-class system, not an ad hoc side channel.
- Implement the Escape Recorder, basic heat maps, and a Replay Recorder.
- Persist match telemetry locally for offline analysis/testing of the (not-yet-built) evolution pipeline.
- **Deliverable:** every test playthrough produces a structured, inspectable telemetry record and a heat map overlay.

### Phase 5 — Procedural Generation Pipeline v1 (offline tool, not yet integrated into live game)
- Define the Design Intent schema (family DNA/doctrine parameters) (§8.1, §17).
- Build an initial Functional Blueprint library (~10–15 blueprints covering the core room types) and its data format (§8.2, §17).
- Implement District/Zone planning and Circulation generation (§8.3 steps 1–3).
- Implement Blueprint assembly with basic architectural grammar rules (§8.3 step 4).
- Implement automated structural Validation tests (reachability of food/cells/work/medical, no deadlocks, patrol coverage — deliberately **no** escapability check, §8.4).
- Implement a first, simple Quality Scoring function, including basic believability/enjoyability heuristics (§8.4).
- Build a standalone CLI/preview tool under `tools/` so designers can generate and inspect prisons **without launching the full game client** — iteration speed here matters enormously.
- **Deliverable:** the offline tool can generate a structurally valid, connected, reachable prison from a Design Intent, and reject/regenerate failures automatically.

### Phase 6 — Architectural Style Kits & Decoration
- Define the Style Kit data format and build an initial set (~5–8 kits) (§8.2, §17).
- Define the Decoration Rule Set format and build an initial set (§8.2).
- Implement the Beautification and Storytelling passes (§8.3 steps 7–8).
- Implement the procedural Exterior Generator (§8.5).
- Implement the Family system: DNA/Doctrine inheritance, generation lineage, blueprint/style preference weighting (§8.1).
- **Deliverable:** the same functional layout, rendered through two different families, is visually distinct, and a family's second generation is visually recognizable as "the same prison" despite layout differences.

### Phase 7 — Save/Load & Serialization
- Implement the versioned binary save format (chunks → entities → components) (§7.12).
- Implement a save-format migration mechanism.
- **Deliverable:** a running match can be saved and reloaded exactly, and an intentionally-old-format save can still load and be upgraded.

### Phase 8 — Networking & Multiplayer Foundation
- Split the shared simulation cleanly from rendering if any coupling crept in during Phases 1–7 (audit against Pillar #5).
- Implement the versioned message protocol over Godot's ENet transport (§7.11).
- Stand up a headless dedicated server build using the same `shared/` library.
- Implement basic player-hosted server mode and the friends-based invite system.
- **Deliverable:** two or more clients can connect to a dedicated (or player-hosted) authoritative server, see a consistent world state, and play together with server-validated actions.

### Phase 9 — AI Scaling & Performance
- Implement the full LOD system (0–4) for NPC simulation (§7.10).
- Implement the AI scheduler with priority-based CPU allocation and simulation bubbles (§7.10).
- Implement the dynamic AI budget manager reacting to measured CPU/frame time.
- Expose the Server Performance Profile presets (Lightweight/Balanced/High Capacity/Dedicated) as a thin layer over the budget system (§12.1).
- Confirm Graphics settings remain fully decoupled from simulation settings (§12.2).
- Implement the Auto-tuning benchmark-on-start + continuous adjustment loop (§12.3).
- **Deliverable:** a stress test with hundreds of simulated NPCs across a large multi-floor prison stays within target frame/tick budgets on a "Balanced" profile, and gracefully degrades rather than stalling under load.

### Phase 10 — Evolution AI & Escape Analyzer
- Implement the Escape Analyzer, converting raw Telemetry into structured weakness signals (§9.1).
- Implement the rule-based Evolution/Rule Engine mutating DNA/Doctrine from weakness signals (§9.2).
- Implement multi-candidate generation, automated validation/scoring reuse from Phase 5–6, and best-candidate selection (§9.3).
- Build the (initially minimal) admin approval gate UI/API (§9.3, §10.4).
- **Deliverable:** feeding a batch of recorded test escapes into the Evolution service produces a measurably *harder*, specifically-countering next generation of the same prison family (never a softer one), which still passes the Believability & Enjoyability gates (§8.4), without any human intervention required.

### Phase 11 — Prison Lifecycle & Hosting Model
- Implement the `Prisons` metadata model (`HostType`, `Visibility`, `Status`, `ParentPrisonId`, etc.) (§10.1, §11.1).
- Implement the full lifecycle state machine (Generating → Testing → Ready → Active → Compromised → Retiring → Archived) with the mandatory 24-hour minimum Compromised→Retiring delay (§10.2).
- Implement Community prison hosting: creation, visibility settings (public/private/friends), and the `ShareEscapeData` opt-in toggle (§10.1, §10.3).
- Implement the admin tools to view/set `HostType` and manage lifecycle transitions (§10.4).
- **Deliverable:** an official prison, once escaped, visibly enters Compromised status with a visible countdown, remains playable, and after the delay is replaced by an admin-reviewable next generation, all driven by real backend state, not a script.

### Phase 12 — Backend Services & Infrastructure
- Stand up PostgreSQL with the full schema from §11.1.
- Stand up Redis for sessions/matchmaking/presence.
- Stand up MinIO (or S3) for replays/saves/screenshots/archives/mods.
- Stand up NATS with the topic set from §11.4, wiring the Evolution Service, Analytics, Achievements, and Replay archival as subscribers.
- Implement Accounts/Auth and the Friends system.
- Implement Matchmaking / server browser.
- Stand up Prometheus + Grafana and route structured logs into a central place.
- **Deliverable:** the full backend stack runs (locally via `infra/` compose configs, and in a real deployment environment), and an end-to-end escape on a dedicated server produces database rows, a queued evolution job, and visible metrics/logs.

### Phase 13 — Multiplayer Content Loop at Scale
- Deploy real dedicated official servers hosting actual prison families (seed with a handful of distinct starting Doctrines/personalities per §8.1).
- Run real or closed-beta playtesting, verifying the entire loop end-to-end online: escape → compromised → 24h delay → candidate generation → validation/scoring → (optional admin review) → new generation deployed → players notice and respond.
- **Deliverable:** at least one official prison family has visibly evolved through two or more real, player-driven generations.

### Phase 14 — Modding Support
- Formalize and document the `content/`/`mods/` data schemas (§13, §17).
- Build a mod loader that overlays `mods/*` on top of `content/*` safely (conflict detection, load order).
- Publish modding documentation.
- **Deliverable:** a third party can add a new wall type, item, or functional blueprint purely via a mod folder, with no engine code changes.

### Phase 15 — Polish, Live Ops, Historical/Museum Mode
- Implement a "Museum" mode letting players revisit Archived prison generations offline, to experience the family's evolutionary history.
- Implement rankings, achievements, and live events on official infrastructure.
- Ongoing: add more families, more style kits, more gameplay mechanics, and continue tuning the Evolution engine's rules based on real aggregate data (§9.1's "72% of escapes used X" style insights).
- **Deliverable:** the game is a live, evolving product with a visible, explorable history, not just a static release.

---

## 15. Team, Tooling & Process

- **Dependency Injection over ad hoc construction.** Systems should *receive* their dependencies (e.g., the AI system receives a `Pathfinder` instance) rather than constructing them internally. This keeps systems independently testable and swappable — important given how many systems (Vision, Hearing, AI, Pathfinding) need to be validated in isolation.
- **Never hand-roll what an established library already solves well** (ECS storage/iteration, structured logging, metrics export) — see the Technology Stack (§5) for the specific choices; deviate only with a documented rationale (an ADR under `docs/adr/`).
- **CI from day one** (GitHub Actions): build both `client/` and `server/`, run unit tests for `shared/`, and (once available) run the offline generation tool's validation suite as a CI gate — a prison-generation regression should fail a build the same way a compile error would.
- **Design docs and ADRs** belong in `docs/`. Any deviation from this plan (e.g., swapping PostgreSQL for something else, or abandoning ECS for a different architecture) should be recorded there with the reasoning, not just implemented silently.
- **Content authors need tools before Phase 6 is "done."** The `tools/` CLI/preview tool introduced in Phase 5 should remain a living, prioritized deliverable — a fast local generate-and-inspect loop is what makes the beautification/decoration passes actually tunable by a human, rather than only inferable from code review.

---

## 16. Risks & Open Questions

These are called out explicitly so they are tracked and revisited, not silently forgotten:

- **ECS library choice** — needs a hands-on evaluation early (Phase 0) against Godot's C# interop and the networking/serialization requirements; a wrong choice here is expensive to unwind later.
- **Networking protocol vs. Godot's high-level multiplayer API** — building a fully custom protocol on top of raw ENet is more work upfront but pays off in determinism and debuggability; revisit if development velocity in Phase 8 is much slower than expected.
- **Generation pipeline tuning is inherently iterative and hard to fully specify in advance** — the validation/scoring thresholds in §8.4 will need real playtesting data to tune; treat the numeric thresholds mentioned throughout this document (hearing radii, FOV degrees, LOD tick rates, candidate counts) as **initial defaults to be tuned, not final specification**.
- **Balancing rule-based Evolution AI vs. future ML-based components** — start rule-based (§9.2) deliberately; revisit only specific, narrow subtasks (patrol route optimization, escape-path prediction) for ML once the rule-based system and its telemetry are mature and trustworthy.
- **Legitimacy verification for escapes** (distinguishing a real strategic escape from an exploit/bug) is only partially solved by the admin approval gate (§9.3) — this may need dedicated anti-cheat/anomaly-detection tooling as the player base grows.
- **Community/self-hosted server support burden** — player-hosted servers running the same authoritative simulation code (§7.11, §10.1) need a smooth, well-documented setup path, or this pillar of the design (friend-hosted prisons) will go unused in practice.
- **Modding scope** — §13 deliberately scopes modding to data-only for the initial plan; if/when script-level mod hooks are introduced, sandboxing and security need dedicated design work before that door is opened.
- **Long-lived families may become effectively unescapable, which is intended (Pillar #9) but needs a design answer for player experience** — decide, before Phase 13's real playtesting, what happens for players facing a very old, heavily-evolved family: e.g., letting new players default to younger/easier families, exposing a family's approximate "age"/difficulty tier before joining, or leaning into "beating an ancient, near-perfect prison" as a rare, high-prestige achievement. The plan intentionally does not pre-decide this — it should be settled with real data from Phase 13, not guessed at now.
- **Believability/enjoyability scoring (§8.4) is inherently more subjective than the structural checks it sits alongside** — unlike reachability/deadlock checks, "does this feel real" and "is this fun" cannot be fully automated with confidence at first. Expect this to start as a mix of heuristics plus human-in-the-loop review (§9.3's admin gate) and to only gradually become more automated as the team learns which heuristics actually correlate with human judgment.
- **Docker Compose scaling ceiling (Pillar #10)** — Compose is the right choice today, but if official infrastructure eventually needs to run many dedicated servers across many hosts with automated failover, revisit this deliberately (as an ADR) rather than drifting into ad hoc orchestration scripts that become a worse, unofficial Kubernetes.

---

## 17. Appendix: Example Data Schemas

These are starting-point illustrations, not final specifications — refine them as Phase 0/1/5 tooling is built.

### 17.1 Tile Definition

```json
{
  "id": "concrete_floor",
  "display_name": "Concrete Floor",
  "movement_cost": 1.0,
  "visibility_transparency": 1.0,
  "sound_transmission": 0.6,
  "can_dig": true,
  "can_burn": false,
  "can_place_furniture": true,
  "can_flood": false,
  "tags": ["floor", "indoor"]
}
```

### 17.2 Functional Blueprint (abridged)

```json
{
  "id": "cell_block_medium",
  "type": "cell_block",
  "capacity": 40,
  "footprint": { "width": 12, "height": 8 },
  "connection_points": [
    { "id": "corridor_north", "side": "north", "position": 4 },
    { "id": "corridor_south", "side": "south", "position": 4 }
  ],
  "furniture_slots": [
    { "type": "bed", "count": 40 },
    { "type": "toilet", "count": 40 }
  ],
  "gameplay_tags": ["high_surveillance_compatible", "supports_40_inmates"]
}
```

### 17.3 Architectural Style Kit (abridged)

```json
{
  "id": "brutalist_concrete",
  "wall_material": "concrete_panel",
  "floor_material": "concrete_floor",
  "window_shape": "narrow_slit",
  "roof_type": "flat",
  "color_palette": ["#8a8a86", "#5c5c58", "#3a3a38"],
  "trim_style": "minimal"
}
```

### 17.4 Decoration Rule Set (abridged)

```json
{
  "id": "aging_facility",
  "rules": [
    { "target": "wall", "condition": "indoor", "effect": "cracked_paint", "chance": 0.2 },
    { "target": "wall", "condition": "outdoor", "effect": "drain_pipe", "chance": 1.0 },
    { "target": "floor", "condition": "high_traffic", "effect": "worn_texture", "chance": 0.5 }
  ]
}
```

### 17.5 Prison Family (abridged)

```json
{
  "id": "blackstone",
  "display_name": "Blackstone",
  "architectural_dna": {
    "age": "1970s",
    "style_kit": "brutalist_concrete",
    "decoration_rule_set": "aging_facility",
    "capacity": "large",
    "region": "cold_climate",
    "floors": 3,
    "preferred_blueprints": ["cell_block_medium", "guard_station_square_tower"]
  },
  "warden_doctrine": {
    "guard_count": 60,
    "camera_count": 40,
    "security_level": "maximum",
    "door_density": "high",
    "patrol_frequency_hz": 0.2,
    "search_frequency_hours": 6,
    "contraband_tolerance": "low"
  },
  "current_generation": 27,
  "parent_prison_id": "blackstone-gen26"
}
```

### 17.6 Prison Record (abridged, matches §11.1)

```json
{
  "prison_id": "blackstone-gen27",
  "family_id": "blackstone",
  "generation": 27,
  "host_type": "Official",
  "status": "Active",
  "owner_id": null,
  "visibility": "Public",
  "created_at": "2026-06-01T00:00:00Z",
  "retire_at": null,
  "share_escape_data": true,
  "parent_prison_id": "blackstone-gen26"
}
```

### 17.7 Escape Weakness Signal (Escape Analyzer output, abridged, matches §9.1)

```json
{
  "escape_id": "esc_00123",
  "prison_id": "blackstone-gen27",
  "signals": [
    { "type": "tunnel_start", "location": "cell_14", "score": 9 },
    { "type": "laundry_unmonitored", "location": "laundry_block_b", "score": 6 },
    { "type": "disguise_success", "location": "gate_east", "score": 8 }
  ]
}
```

---

*End of plan. This document should be kept up to date as an ADR-linked living reference — if an implementation decision deviates from what's written here, update this file (or link the ADR that supersedes the relevant section) rather than letting the plan drift out of sync with reality.*
