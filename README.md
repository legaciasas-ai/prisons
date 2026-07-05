# Prison

A 2D top-down prison simulation game — Prison Architect × The Escapists, wrapped in an
evolutionary loop: a procedural generator builds a prison, players try to escape it, and
every successful escape becomes training data that makes the *next* generation of that
prison harder in a specific, targeted way.

**The full design and build plan lives in [PLAN.md](PLAN.md).** Read that first; this
README only covers layout and day-to-day commands.

## Repository layout (PLAN §6)

| Path | Contents |
|---|---|
| `client/` | Godot 4 (C#) project: rendering, UI, input, local single-player bootstrap |
| `server/` | Headless dedicated server: embeds the Core Simulation, no rendering |
| `shared/` | The Core Simulation library — identical on client and server (Pillar #3/#5) |
| `content/` | Data-driven game content (tiles, items, blueprints, families…) — zero code |
| `assets/` | Textures, sprites, sounds, fonts |
| `tools/` | Offline tooling (prison generator CLI/preview, validators) |
| `backend/` | Non-Godot backend services (accounts, registry, evolution, matchmaking) |
| `infra/` | Docker Compose stacks (no Kubernetes — Pillar #10), CI/monitoring configs |
| `tests/` | Unit tests (CI gate for `shared/`) |
| `docs/` | ADRs and design docs |
| `mods/` | Reserved for future community mods (mirrors `content/`) |

## Building & running

Requires the .NET 8 SDK. The Godot 4.7 (.NET) editor is only needed to run/edit the client.

```sh
dotnet build Prison.sln              # builds shared, server, tests, and the Godot client assembly
dotnet test Prison.sln               # runs the shared-simulation unit tests
dotnet run --project server          # runs the headless dedicated server (Ctrl+C to stop)
godot4 --editor --path client        # opens the client in the Godot editor
```

Dedicated server in a container:

```sh
docker compose -f infra/docker-compose.yml up -d --build
```

Server configuration lives in `server/config/server.toml` (path overridable via the first
CLI argument or `PRISON_SERVER_CONFIG`).
