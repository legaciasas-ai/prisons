# ADR 0002 — TCP (length-prefixed frames) as the v1 network transport, behind a transport abstraction

**Status:** accepted (Phase 8)

## Context

PLAN §5/§7.11 names "Godot's ENet transport (low-level) + a custom, versioned message
protocol" as the networking layer. Two facts complicate the ENet half today:

1. The dedicated server (`server/`) is a plain .NET console app — deliberately free of any
   Godot runtime (Pillar #5) — so *Godot's* ENet binding is simply not available there.
2. The part of §7.11 that actually carries the design weight is the **explicit, versioned
   message protocol** and **server authority**, not the specific datagram library.

## Decision

- The protocol, the authoritative `ServerSession`, and the replicated-view `ClientSession`
  live in `shared/Networking`, written against two small interfaces: `IServerTransport` /
  `IClientTransport` (discrete, reliable, ordered frames; events dispatched on the caller's
  thread via `Poll()`).
- v1 ships two implementations:
  - `LoopbackTransport` — in-memory, deterministic; used by tests and, later, by a
    listen-server (host playing in the process that runs the authoritative simulation).
  - `TcpServerTransport` / `TcpClientTransport` — length-prefixed frames
    (`[int32 LE length][payload]`, 8 MB cap) over TCP. Works identically in the plain .NET
    server, in tools/tests, and in Godot C#.
- ENet/UDP (or any unreliable-channel optimization) can be introduced later as a third
  implementation of the same interfaces, without touching protocol or session code.

## Consequences

- Everything above the transport is engine-agnostic and unit-testable (the Phase 8 test
  suite runs full client↔server exchanges in memory, deterministically).
- TCP head-of-line blocking is accepted for v1: at a 20 Hz tick with small state frames on
  a co-op scale player count, it is not the bottleneck. Revisit (new ADR) if real latency
  measurements in Phase 9/13 say otherwise — the swap is contained by design.
- The client and server must agree on `Protocol.Version`; the handshake rejects mismatches
  up front instead of desyncing later.
