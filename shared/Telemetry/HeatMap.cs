using Prison.Shared.World;

namespace Prison.Shared.Telemetry;

/// <summary>
/// A per-tile counter over the whole prison (PLAN §7.9 heat maps): most-used corridor,
/// most-heard tile, most-escaped fence... Aggregated per match here; cross-match aggregation
/// happens backend-side (Phase 10+).
/// </summary>
public sealed class HeatMap
{
    private readonly Dictionary<TilePos, int> _counts = [];

    public IReadOnlyDictionary<TilePos, int> Counts => _counts;

    /// <summary>Highest single-tile count — the natural normalizer for overlay rendering.</summary>
    public int Max { get; private set; }

    public void Increment(TilePos pos, int amount = 1)
    {
        var value = _counts.GetValueOrDefault(pos) + amount;
        _counts[pos] = value;
        if (value > Max)
            Max = value;
    }

    public int At(TilePos pos) => _counts.GetValueOrDefault(pos);

    public object ToSerializable() =>
        _counts.Select(kv => new { kv.Key.X, kv.Key.Y, kv.Key.Floor, Count = kv.Value });
}
