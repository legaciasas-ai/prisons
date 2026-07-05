# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this project is

"Prison" — a 2D top-down prison simulation game (Prison Architect × The Escapists) built around
an evolutionary loop: a procedural generator builds a prison, players try to escape it, telemetry
records everything, and an offline Evolution AI mutates the prison's design to counter how it was
beaten. **`PLAN.md` is the authoritative design document** — architecture, design pillars,
glossary, data schemas, and the phase-by-phase roadmap (§14) all live there. `CONVERSATION.md` is
the historical founding discussion; don't treat it as current.

Current progress (see git history, `[PHASE]` commits): roadmap phases 0–7 done, phase 8
(networking) mostly done — remaining: friends/invites (needs the Phase 12 backend). Next up:
phase 9 (AI LOD/scheduler/budget).

## Repository layout

- `shared/` — **the Core Simulation library** (engine-agnostic C#, no Godot types): ECS (Arch
  library), World/tiles/floors, hierarchical A* pathfinding, shared Visibility (FOV) module,
  Staff AI (perception → memory → utility-AI decisions → actions), suspicion/investigation,
  escapist mechanics (dig/disguise/lockpick/craft/throw), event bus, telemetry (escape recorder,
  heat maps, replay), procedural generation (blueprints, style kits, decoration, families),
  save/load (`Serialization/MatchSave.cs`, versioned binary + migration chain), and networking
  (`Networking/`: versioned protocol, authoritative `ServerSession`, replicated `ClientSession`,
  loopback + TCP transports). `MatchFactory` assembles a match identically everywhere.
- `client/` — Godot 4.7 (C#) presentation layer. `Main.cs` renders an `IMatchClient`
  (`MatchClient.cs`): `LocalMatchClient` embeds the simulation (single-player),
  `RemoteMatchClient` mirrors a server. Rendering/input only — no gameplay logic here, ever.
- `server/` — headless dedicated server (plain .NET console app, no Godot). Fixed-tick loop,
  TCP listener (port 30500), TOML config in `server/config/server.toml`.
- `content/` — **data only, zero code** (JSON): tiles, items, recipes, rooms (functional
  blueprints), styles (style kits), decoration_rules, families, maps, jobs, schedules, uniforms.
- `tools/prison-gen/` — standalone generator CLI (generate/inspect prisons without the game):
  `dotnet run --project tools/prison-gen -- --seed 42 --security high --out prison.json`.
- `tests/Prison.Shared.Tests/` — xUnit suite for the shared library (80+ tests).
- `docs/adr/` — architecture decision records (0001: Arch ECS; 0002: TCP transport v1 behind
  transport interfaces, ENet later). Any deviation from PLAN.md gets an ADR.
- `backend/`, `infra/`, `mods/`, `assets/` — backend services (mostly future, Phase 12),
  Docker Compose + CI configs, reserved mod overlay, art assets.

## Commands

`dotnet` is **not on PATH** on this machine — use `~/.dotnet/dotnet` (SDK 8.0):

```sh
export PATH="$HOME/.dotnet:$PATH"
dotnet build Prison.sln          # builds everything, including the Godot client (SDK from NuGet)
dotnet test Prison.sln           # full test suite
dotnet run --project server      # dedicated server: fixed-tick sim + TCP listener on 30500
```

The Godot editor/runtime is **not installed** here: the client *compiles* headlessly but cannot
be run/played in this environment. To play online, a user runs the client with env
`PRISON_SERVER=host[:port]` (and optional `PRISON_PLAYER_NAME`); unset = local single-player.

## Architecture rules (condensed from PLAN.md §2 — read the pillars before big changes)

- **Simulation is authoritative and identical everywhere** (client single-player, dedicated
  server, player-hosted). Clients send *intents*; all validation runs inside the simulation.
- **`shared/` never references Godot types.** Rendering and simulation are fully decoupled;
  the sim runs at a fixed tick (20/s default), framerate-independent, headless-capable.
- **No perception cheating**: players, guards, dogs, cameras all use the one shared Visibility
  module. No code path may give an NPC knowledge it couldn't physically acquire.
- **Two AIs, strictly separated**: Staff AI (real-time, in-match, never learns mid-match) vs
  Evolution AI (offline, between generations). Never blur them.
- **Everything is data**: tiles/items/rooms/styles are property bags defined in `content/`;
  engine code never tests for a specific tile by name (no `if (tile == WALL)`).
- **Escapability is never a validation criterion** for generated prisons (Pillar #9 — one-way
  difficulty ratchet); believability and enjoyability *are* mandatory gates.
- **No Kubernetes** (Pillar #10): Docker Compose in `infra/`, driven by GitHub Actions.
- **Never serialize language objects**: explicit versioned binary formats with migration
  chains (`MatchSave`), same discipline for the network protocol (`Protocol.Version` handshake).
- Dependency injection over internal construction; prefer established libraries (Arch ECS,
  Serilog, Tomlyn) over hand-rolling — deviations get an ADR.

## Conventions

- Commit messages in **French**; phase milestones as `[PHASE] phase N finie — résumé`.
- Tests use the `[Collection("content")]` fixture (`TestContent`) which loads the real
  `content/` data — prefer exercising real content over synthetic fixtures.
- A generation regression must fail CI like a compile error would (PLAN §15): keep
  `tools/prison-gen` validation wired into the test suite/CI.
