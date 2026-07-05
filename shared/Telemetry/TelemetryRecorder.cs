using System.Text.Json;
using Prison.Shared.Events;
using Prison.Shared.World;

namespace Prison.Shared.Telemetry;

/// <summary>One recorded gameplay moment, structured for offline analysis.</summary>
public readonly record struct TelemetryEntry(ulong Tick, string Type, string Detail);

/// <summary>
/// First version of the Telemetry module (PLAN §7.9, Phase 3): subscribes to the in-simulation
/// event bus and keeps a structured, inspectable record of the match — the raw material the
/// Escape Recorder, heat maps and the Evolution AI's analyzer (Phase 4+) will be built on.
/// Pure consumer: it never influences the simulation.
/// </summary>
public sealed class TelemetryRecorder
{
    private readonly List<TelemetryEntry> _entries = [];
    private readonly Dictionary<SoundKind, int> _soundCounts = [];
    private readonly Dictionary<TilePos, int> _soundHeatMap = [];
    private int _observations;

    public TelemetryRecorder(EventBus events)
    {
        events.Subscribe<SoundEmittedEvent>(evt =>
        {
            _soundCounts[evt.Kind] = _soundCounts.GetValueOrDefault(evt.Kind) + 1;
            _soundHeatMap[evt.Position] = _soundHeatMap.GetValueOrDefault(evt.Position) + 1;
            // Footsteps are counted/heat-mapped but not journaled — they would drown the log.
            if (evt.Kind is not (SoundKind.FootstepWalk or SoundKind.FootstepRun))
                Add(evt.Tick, nameof(SoundEmittedEvent), $"{evt.Kind} at {evt.Position} r={evt.RadiusTiles}");
        });
        events.Subscribe<PrisonerObservedEvent>(_ => _observations++);
        events.Subscribe<SuspectAlertEvent>(evt => Add(evt.Tick, nameof(SuspectAlertEvent), $"at {evt.Position}"));
        events.Subscribe<RadioBroadcastEvent>(evt => Add(evt.Tick, nameof(RadioBroadcastEvent), $"last known {evt.LastKnownPosition}"));
        events.Subscribe<ArrestEvent>(evt => Add(evt.Tick, nameof(ArrestEvent), $"at {evt.Position}"));
        events.Subscribe<ItemPickedUpEvent>(evt => Add(evt.Tick, nameof(ItemPickedUpEvent), $"{evt.ItemId} at {evt.Position}"));
        events.Subscribe<ItemCraftedEvent>(evt => Add(evt.Tick, nameof(ItemCraftedEvent), $"{evt.RecipeId} -> {evt.OutputItemId}"));
        events.Subscribe<TileDugEvent>(evt => Add(evt.Tick, nameof(TileDugEvent), $"at {evt.Position}"));
        events.Subscribe<FenceCutEvent>(evt => Add(evt.Tick, nameof(FenceCutEvent), $"at {evt.Position}"));
        events.Subscribe<DoorUnlockedEvent>(evt => Add(evt.Tick, nameof(DoorUnlockedEvent), $"at {evt.Position}"));
        events.Subscribe<DoorToggledEvent>(evt => Add(evt.Tick, nameof(DoorToggledEvent), $"at {evt.Position} open={evt.Open}"));
        events.Subscribe<DisguiseChangedEvent>(evt => Add(evt.Tick, nameof(DisguiseChangedEvent), $"role={evt.Role ?? "none"}"));
        events.Subscribe<DisguiseCompromisedEvent>(evt => Add(evt.Tick, nameof(DisguiseCompromisedEvent), $"at {evt.Position}"));
        events.Subscribe<DiversionEvent>(evt => Add(evt.Tick, nameof(DiversionEvent), $"{evt.ItemId} to {evt.Target}"));
    }

    public IReadOnlyList<TelemetryEntry> Entries => _entries;

    public IReadOnlyDictionary<SoundKind, int> SoundCounts => _soundCounts;

    /// <summary>How often each tile was the source of a sound — a proto heat map (PLAN §7.9).</summary>
    public IReadOnlyDictionary<TilePos, int> SoundHeatMap => _soundHeatMap;

    /// <summary>Total prisoner sightings by staff (aggregate only; per-sighting detail is Phase 4).</summary>
    public int ObservationCount => _observations;

    private void Add(ulong tick, string type, string detail) =>
        _entries.Add(new TelemetryEntry(tick, type, detail));

    /// <summary>Serializes the record for offline inspection (PLAN Phase 4 will formalize this).</summary>
    public string ToJson() => JsonSerializer.Serialize(new
    {
        entries = _entries.Select(e => new { e.Tick, e.Type, e.Detail }),
        sound_counts = _soundCounts.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        observation_count = _observations,
        sound_heat_map = _soundHeatMap.Select(kv => new { kv.Key.X, kv.Key.Y, kv.Key.Floor, Count = kv.Value }),
    });
}
