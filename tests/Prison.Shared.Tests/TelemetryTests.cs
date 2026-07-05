using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.Interaction;
using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

/// <summary>
/// Phase 4 (PLAN §7.9): every playthrough produces a structured, inspectable telemetry
/// record — event journal, escape/movement record with heat map, and a versioned replay.
/// </summary>
[Collection("content")]
public class TelemetryTests(TestContent content)
{
    private MatchHandle NewMatch() =>
        MatchFactory.Create(content.BuildWorld(), content.Map, content.Items, content.Recipes,
            includeMapGuards: false);

    private static void RunSeconds(Simulation sim, float seconds)
    {
        var ticks = (int)(seconds * sim.TicksPerSecond);
        for (var i = 0; i < ticks; i++)
            sim.Tick();
    }

    [Fact]
    public void EventBus_CatchAllChannel_SeesEveryPublishedEvent()
    {
        var bus = new EventBus();
        var typed = new List<int>();
        var all = new List<object>();
        bus.Subscribe<int>(typed.Add);
        bus.SubscribeAll(all.Add);

        bus.Publish(42);
        bus.Publish("hello");

        Assert.Equal([42], typed);
        Assert.Equal(2, all.Count); // typed *and* untyped types both reach the catch-all
    }

    [Fact]
    public void EscapeRecorder_SamplesPath_Distance_AndPresenceHeatMap()
    {
        var match = NewMatch();
        var sim = match.Simulation;

        // Start in the open corridor (the spawn cell's walls would block the walk).
        sim.World.Get<Position>(match.Player) = new Position(6.5f, 7.5f, 0);
        ref var input = ref sim.World.Get<PlayerInput>(match.Player);
        input.MoveX = 1f;
        RunSeconds(sim, 2f); // walk east ~7 tiles
        input.MoveX = 0f;

        var path = match.Escape.Paths[match.Player];
        Assert.True(path.Count >= 6, $"expected regular path samples, got {path.Count}");
        Assert.True(path[^1].X > path[0].X + 3f, "path should show eastward movement");
        Assert.InRange(match.Escape.DistanceWalked(match.Player), 4f, 10f);

        Assert.True(match.Escape.Presence.Max > 0);
        Assert.True(match.Escape.Presence.Counts.Count >= 4, "several distinct tiles visited");
    }

    [Fact]
    public void ReplayRecorder_CapturesEventsWithTicks_AndKeyframes()
    {
        var match = NewMatch();
        var sim = match.Simulation;

        // Generate some activity: pick up the spoon in the spawn cell.
        sim.World.Get<Interactor>(match.Player).Request =
            new InteractionRequest(InteractionKind.PickUp, new TilePos(6, 4, 0));
        RunSeconds(sim, 1f);

        Assert.Contains(match.Replay.Events, e => e.Type == nameof(ItemPickedUpEvent));
        Assert.NotEmpty(match.Replay.Keyframes);
        // Keyframes must cover the player across time, not just once.
        Assert.True(match.Replay.Keyframes.Count(k => k.EntityId == match.Player.Id) >= 2);

        var json = match.Replay.ToJson();
        Assert.Contains("\"version\":1", json);
        Assert.Contains(nameof(ItemPickedUpEvent), json);
    }

    [Fact]
    public void TelemetrySink_PersistsAllThreeRecords()
    {
        var match = NewMatch();
        RunSeconds(match.Simulation, 1f);

        var dir = Path.Combine(Path.GetTempPath(), $"prison-telemetry-test-{Guid.NewGuid():N}");
        try
        {
            match.WriteTelemetry(dir);
            Assert.True(File.Exists(Path.Combine(dir, "telemetry.json")));
            Assert.True(File.Exists(Path.Combine(dir, "escape.json")));
            Assert.True(File.Exists(Path.Combine(dir, "replay.json")));
            Assert.Contains("presence_heat_map", File.ReadAllText(Path.Combine(dir, "escape.json")));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
