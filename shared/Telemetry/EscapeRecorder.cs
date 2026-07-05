using System.Text.Json;
using Arch.Core;
using Prison.Shared.AI;
using Prison.Shared.ECS;
using Prison.Shared.ECS.Components;
using Prison.Shared.World;

namespace Prison.Shared.Telemetry;

/// <summary>One sampled point of a prisoner's journey.</summary>
public readonly record struct PathSample(ulong Tick, float X, float Y, int Floor);

/// <summary>
/// The Escape Recorder (PLAN §7.9): samples every prisoner's path through the prison,
/// accumulates distance walked and a presence heat map. Together with the
/// <see cref="TelemetryRecorder"/>'s event journal this is the raw record an escape
/// analysis (Phase 10's Escape Analyzer) consumes.
/// </summary>
public sealed class EscapeRecorder : ISimulationSystem
{
    /// <summary>Path sampling period: 4 samples/second at 20 ticks/s is plenty for analysis.</summary>
    public const uint SampleIntervalTicks = 5;

    private static readonly QueryDescription Prisoners =
        new QueryDescription().WithAll<PrisonerTag, Position>();

    private readonly Dictionary<Entity, List<PathSample>> _paths = [];
    private readonly Dictionary<Entity, float> _distances = [];

    public string Name => "EscapeRecorder";

    /// <summary>Where prisoners have physically been, sample by sample.</summary>
    public HeatMap Presence { get; } = new();

    public IReadOnlyDictionary<Entity, List<PathSample>> Paths => _paths;

    public float DistanceWalked(Entity prisoner) => _distances.GetValueOrDefault(prisoner);

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        if (time.Tick % SampleIntervalTicks != 0)
            return;

        var tick = time.Tick;
        ecsWorld.Query(in Prisoners, (Entity prisoner, ref Position position) =>
        {
            if (!_paths.TryGetValue(prisoner, out var path))
                _paths[prisoner] = path = [];

            if (path.Count > 0)
            {
                var last = path[^1];
                if (last.Floor == position.Floor)
                {
                    var dx = position.X - last.X;
                    var dy = position.Y - last.Y;
                    _distances[prisoner] = _distances.GetValueOrDefault(prisoner) + MathF.Sqrt(dx * dx + dy * dy);
                }
            }

            path.Add(new PathSample(tick, position.X, position.Y, position.Floor));
            Presence.Increment(new TilePos((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), position.Floor));
        });
    }

    /// <summary>Serializes the movement record for offline analysis.</summary>
    public string ToJson() => JsonSerializer.Serialize(new
    {
        prisoners = _paths.Select(kv => new
        {
            entity = kv.Key.Id,
            distance_walked = _distances.GetValueOrDefault(kv.Key),
            path = kv.Value.Select(s => new { s.Tick, s.X, s.Y, s.Floor }),
        }),
        presence_heat_map = Presence.ToSerializable(),
    });
}
