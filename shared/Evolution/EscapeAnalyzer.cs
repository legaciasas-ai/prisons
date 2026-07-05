using Prison.Shared.World;

namespace Prison.Shared.Evolution;

/// <summary>One translated weakness (PLAN §9.1/§17.7): a typed signal, not raw player data.</summary>
public sealed record WeaknessSignal(string Type, TilePos? Location, float Score)
{
    public const string TunnelRoute = "tunnel_route";
    public const string FenceRoute = "fence_route";
    public const string DisguiseRoute = "disguise_route";
    public const string LockpickRoute = "lockpick_route";
    public const string LowDetection = "low_detection";
}

/// <summary>The Escape Analyzer's output for one prison generation: aggregated, ranked weaknesses.</summary>
public sealed record EscapeAnalysis(
    string PrisonId,
    int ReportCount,
    int EscapeCount,
    IReadOnlyList<WeaknessSignal> Signals)
{
    public WeaknessSignal? Top => Signals.Count > 0 ? Signals[0] : null;

    public float ScoreOf(string type) =>
        Signals.FirstOrDefault(s => s.Type == type)?.Score ?? 0f;
}

/// <summary>
/// Converts raw escape reports into structured weakness signals (PLAN §9.1). Deliberately
/// aggregate-first: "72% of escapes used the tunnel route" outweighs any single run, which
/// keeps the one-way ratchet (§9.2) aimed at real, recurring weaknesses instead of
/// overreacting to one lucky or exploit-driven escape.
/// </summary>
public static class EscapeAnalyzer
{
    /// <summary>Base danger weights per exploited route (§9.1's example scores).</summary>
    private const float TunnelWeight = 9f;
    private const float FenceWeight = 10f;
    private const float DisguiseWeight = 8f;
    private const float LockpickWeight = 6f;
    private const float LowDetectionWeight = 7f;

    /// <summary>A prisoner sighted fewer times than this during a successful escape means
    /// patrols simply never saw them — a coverage weakness, not a route one.</summary>
    public const int LowDetectionObservationThreshold = 3;

    public static EscapeAnalysis Analyze(string prisonId, IReadOnlyList<EscapeReport> reports)
    {
        var escapes = reports.Where(r => r.Escaped).ToList();
        var signals = new List<WeaknessSignal>();

        if (escapes.Count > 0)
        {
            AddRouteSignal(signals, escapes, WeaknessSignal.TunnelRoute, TunnelWeight, r => r.TunnelsDug);
            AddRouteSignal(signals, escapes, WeaknessSignal.FenceRoute, FenceWeight, r => r.FencesCut);
            AddRouteSignal(signals, escapes, WeaknessSignal.LockpickRoute, LockpickWeight, r => r.DoorsLockpicked);

            var disguised = escapes.Count(r => r.DisguisesWorn > 0 && r.DisguiseCompromises == 0);
            if (disguised > 0)
                signals.Add(new WeaknessSignal(WeaknessSignal.DisguiseRoute, null,
                    DisguiseWeight * disguised / escapes.Count));

            var unseen = escapes.Count(r => r.TimesObserved < LowDetectionObservationThreshold);
            if (unseen > 0)
                signals.Add(new WeaknessSignal(WeaknessSignal.LowDetection, null,
                    LowDetectionWeight * unseen / escapes.Count));
        }

        return new EscapeAnalysis(prisonId, reports.Count, escapes.Count,
            signals.OrderByDescending(s => s.Score).ToList());
    }

    private static void AddRouteSignal(List<WeaknessSignal> signals, List<EscapeReport> escapes,
        string type, float weight, Func<EscapeReport, IReadOnlyList<TilePos>> positions)
    {
        var used = escapes.Where(r => positions(r).Count > 0).ToList();
        if (used.Count == 0)
            return;

        // The hottest spot of the route becomes the signal's location (e.g. "tunnels start
        // in cell 14"), so mutations can stay targeted rather than global.
        var hotspot = used.SelectMany(positions)
            .GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .First().Key;

        signals.Add(new WeaknessSignal(type, hotspot, weight * used.Count / escapes.Count));
    }
}
