using System.Text.Json;
using Arch.Core;
using Prison.Shared.ECS;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;

namespace Prison.Shared.Telemetry;

/// <summary>An event captured for replay: the tick it happened, its type, and its data.</summary>
public readonly record struct ReplayEvent(ulong Tick, string Type, string Data);

/// <summary>One position keyframe for one entity.</summary>
public readonly record struct ReplayKeyframe(ulong Tick, int EntityId, float X, float Y, int Floor);

/// <summary>
/// The Replay Recorder (PLAN §7.9): captures the complete event stream (via the bus's
/// catch-all channel) plus periodic position keyframes of every placed entity — enough to
/// scrub back through a match. Registered as the *first* system each tick so events published
/// later in that tick are stamped with the right tick number. The persisted format is
/// versioned from day one (§7.12 discipline applies to replays too).
/// </summary>
public sealed class ReplayRecorder : ISimulationSystem
{
    public const int FormatVersion = 1;

    /// <summary>Keyframe period: 2/second at 20 ticks/s keeps replays small but scrubbable.</summary>
    public const uint KeyframeIntervalTicks = 10;

    private static readonly QueryDescription Placed = new QueryDescription().WithAll<Position>();

    private readonly List<ReplayEvent> _events = [];
    private readonly List<ReplayKeyframe> _keyframes = [];
    private ulong _currentTick;

    public ReplayRecorder(EventBus events)
    {
        events.SubscribeAll(evt =>
            _events.Add(new ReplayEvent(_currentTick, evt.GetType().Name, evt.ToString() ?? "")));
    }

    public string Name => "ReplayRecorder";

    public IReadOnlyList<ReplayEvent> Events => _events;

    public IReadOnlyList<ReplayKeyframe> Keyframes => _keyframes;

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        _currentTick = time.Tick;

        if (time.Tick % KeyframeIntervalTicks != 0)
            return;

        var tick = time.Tick;
        ecsWorld.Query(in Placed, (Entity entity, ref Position position) =>
            _keyframes.Add(new ReplayKeyframe(tick, entity.Id, position.X, position.Y, position.Floor)));
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        version = FormatVersion,
        events = _events.Select(e => new { e.Tick, e.Type, e.Data }),
        keyframes = _keyframes.Select(k => new { k.Tick, k.EntityId, k.X, k.Y, k.Floor }),
    });
}
