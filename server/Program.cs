using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Prison.Server;
using Prison.Shared;
using Prison.Shared.Events;
using Prison.Shared.Networking;
using Prison.Shared.Pathfinding;
using Prison.Shared.Utilities;
using Prison.Shared.World;
using Serilog;

// Headless dedicated server bootstrap (PLAN §4.1): embeds the Core Simulation with zero
// rendering involved, and listens for players over the versioned message protocol
// (PLAN §7.11) — TCP transport for v1, see docs/adr/0002.

var configPath = args.FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("PRISON_SERVER_CONFIG")
    ?? Path.Combine(AppContext.BaseDirectory, "config", "server.toml");

var config = File.Exists(configPath)
    ? ServerConfig.Load(configPath)
    : new ServerConfig();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(config.LogLevel)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

using var loggerFactory = new LoggerFactory().AddSerilog(Log.Logger);
var log = loggerFactory.CreateLogger("Server");

log.LogInformation("Prison dedicated server starting: {Name}", config.Name);
log.LogInformation("Config: {Path} (tick rate {TickRate}/s, profile {Profile})",
    File.Exists(configPath) ? configPath : "<defaults — no config file found>",
    config.TickRate, config.PerformanceProfile);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

// Load the data-driven world (Pillar #4): same content files as the client.
var contentRoot = ContentPaths.Resolve();
var tiles = TileRegistry.LoadFromDirectory(Path.Combine(contentRoot, "tiles"));
var items = Prison.Shared.Items.ItemRegistry.LoadFromDirectory(Path.Combine(contentRoot, "items"));
var recipes = Prison.Shared.Items.RecipeDefinition.LoadFromDirectory(Path.Combine(contentRoot, "recipes"));
var map = MapDefinition.Load(Path.Combine(contentRoot, "maps", "test_prison.json"));
var world = map.BuildWorld(tiles);
log.LogInformation("World '{Map}' loaded: {Floors} floor(s), {W}x{H}, {Tiles} tile types, {Stairs} stair connection(s)",
    map.Id, world.FloorCount, world.Floor(0).Width, world.Floor(0).Height, tiles.Count, world.Stairs.Count);

// Identical match assembly as the client (Pillar #3), just without rendering and without a
// local player — every prisoner in this match belongs to a connected network peer.
var match = MatchFactory.Create(world, map, items, recipes, loggerFactory, includePlayer: false);
using var simulation = match.Simulation;

// Headless smoke check: the shared pathfinder must route across floors via the stairs.
var smoke = match.Pathfinding.Request(map.PlayerSpawn.Position, new TilePos(20, 5, 1), priority: 100);
match.Pathfinding.Process(budget: 1);
log.LogInformation("Pathfinding smoke test: {Status}, {Length} step(s) across floors",
    smoke.Status, smoke.Path?.Count ?? 0);

simulation.Events.Subscribe<SuspectAlertEvent>(evt =>
    log.LogInformation("[AI] Guard {Guard} calls in suspect at {Pos}", evt.Guard.Id, evt.Position));
simulation.Events.Subscribe<ArrestEvent>(evt =>
    log.LogInformation("[AI] Guard {Guard} arrested prisoner {Prisoner} at {Pos}", evt.Guard.Id, evt.Prisoner.Id, evt.Position));

// The authoritative network session: protocol handshake, intents in, observable state out.
using var transport = new TcpServerTransport(config.Port);
var session = new ServerSession(transport, match, world, map,
    loggerFactory.CreateLogger<ServerSession>());
log.LogInformation("Simulation initialized: {Systems} system(s), {Guards} guard(s); listening on TCP port {Port} (protocol v{Proto})",
    simulation.Systems.Count, map.Guards.Count, config.Port, Protocol.Version);

var clock = Stopwatch.StartNew();
var lastElapsed = 0d;
var lastReportTick = 0ul;
var lastReportTime = 0d;

while (!cts.Token.IsCancellationRequested)
{
    session.PumpNetwork();

    var now = clock.Elapsed.TotalSeconds;
    simulation.Advance(now - lastElapsed);
    lastElapsed = now;

    session.BroadcastState();

    if (now - lastReportTime >= 10)
    {
        var tps = (simulation.CurrentTick - lastReportTick) / (now - lastReportTime);
        log.LogInformation("Tick {Tick} | {Tps:F1} ticks/s | {Entities} entities | {Players} player(s)",
            simulation.CurrentTick, tps, simulation.World.Size, session.PlayerCount);
        lastReportTick = simulation.CurrentTick;
        lastReportTime = now;
    }

    // Sleep until roughly the next tick is due instead of busy-spinning.
    var nextTickAt = (simulation.CurrentTick + 1) / (double)config.TickRate;
    var sleep = nextTickAt - clock.Elapsed.TotalSeconds;
    if (sleep > 0.002)
        Thread.Sleep(TimeSpan.FromSeconds(sleep - 0.001));
}

log.LogInformation("Shutdown requested; stopped at tick {Tick}", simulation.CurrentTick);

// Persist the match telemetry locally for offline analysis (PLAN Phase 4). Local only:
// nothing is shared unless a host opts in (Pillar #7) — sharing arrives with the backend.
var sessionDir = Path.Combine("telemetry", $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
match.WriteTelemetry(sessionDir);
log.LogInformation("Telemetry written to {Dir} ({Events} journal entries, {Keyframes} replay keyframes)",
    sessionDir, match.Telemetry.Entries.Count, match.Replay.Keyframes.Count);

Log.CloseAndFlush();
