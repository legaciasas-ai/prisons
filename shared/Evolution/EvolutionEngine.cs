using Prison.Shared.Generation;

namespace Prison.Shared.Evolution;

/// <summary>An auditable evolution decision: what was observed, what mutates, and why.</summary>
public sealed record EvolutionProposal(
    FamilyDefinition Family,
    FamilyDefinition Mutated,
    EscapeAnalysis Analysis,
    IReadOnlyList<string> Rationale)
{
    public bool ChangesAnything => !Family.Doctrine.Equals(Mutated.Doctrine);
}

/// <summary>
/// The rule-based Evolution/Rule Engine (PLAN §9.2): weakness signals → targeted, permanent
/// Warden Doctrine mutations. A **one-way ratchet** (Pillar #9): every mutation is enforced
/// to be monotonic — no signal, admin, or code path here ever softens a doctrine, and there
/// is deliberately no target escape rate to balance toward. Rule-based on purpose: every
/// change ships with a human-readable rationale, so the team (and eventually players) can
/// see exactly why a prison changed the way it did.
/// </summary>
public static class EvolutionEngine
{
    /// <summary>A route must be used by at least this share of escapes to drive a mutation —
    /// aggregation over many escapes, not a knee-jerk reaction to one (§9.2).</summary>
    public const float MutationScoreThreshold = 2.5f;

    public static EvolutionProposal Propose(FamilyDefinition family, EscapeAnalysis analysis)
    {
        var doctrine = family.Doctrine;
        var rationale = new List<string>();

        if (analysis.ScoreOf(WeaknessSignal.TunnelRoute) >= MutationScoreThreshold)
        {
            doctrine = doctrine with
            {
                HardenedGroundBias = MathF.Min(1f, doctrine.HardenedGroundBias + 0.34f),
            };
            rationale.Add(
                $"Tunnel route exploited (score {analysis.ScoreOf(WeaknessSignal.TunnelRoute):F1}, " +
                $"hotspot {Describe(analysis, WeaknessSignal.TunnelRoute)}): increasing concrete flooring " +
                $"(hardened ground bias → {doctrine.HardenedGroundBias:F2}).");
        }

        if (analysis.ScoreOf(WeaknessSignal.FenceRoute) >= MutationScoreThreshold)
        {
            doctrine = doctrine with
            {
                FenceLayers = Math.Min(3, doctrine.FenceLayers + 1),
                PerimeterPatrol = true,
            };
            rationale.Add(
                $"Perimeter fence exploited (score {analysis.ScoreOf(WeaknessSignal.FenceRoute):F1}, " +
                $"hotspot {Describe(analysis, WeaknessSignal.FenceRoute)}): adding a fence layer " +
                $"(→ {doctrine.FenceLayers}) and a dedicated perimeter patrol.");
        }

        if (analysis.ScoreOf(WeaknessSignal.DisguiseRoute) >= MutationScoreThreshold)
        {
            doctrine = doctrine with { RestrictedUniformAccess = true };
            rationale.Add(
                $"Disguises worked unchallenged (score {analysis.ScoreOf(WeaknessSignal.DisguiseRoute):F1}): " +
                "restricting uniform access throughout the facility.");
        }

        if (analysis.ScoreOf(WeaknessSignal.LockpickRoute) >= MutationScoreThreshold)
        {
            doctrine = doctrine with { ExtraGuardStations = Math.Min(3, doctrine.ExtraGuardStations + 1) };
            rationale.Add(
                $"Locks picked en route (score {analysis.ScoreOf(WeaknessSignal.LockpickRoute):F1}): " +
                $"adding a guard station (→ +{doctrine.ExtraGuardStations}).");
        }

        if (analysis.ScoreOf(WeaknessSignal.LowDetection) >= MutationScoreThreshold)
        {
            doctrine = doctrine with { ExtraPatrolGuards = Math.Min(6, doctrine.ExtraPatrolGuards + 1) };
            rationale.Add(
                $"Escapes went nearly unobserved (score {analysis.ScoreOf(WeaknessSignal.LowDetection):F1}): " +
                $"hiring more patrol guards (→ +{doctrine.ExtraPatrolGuards}).");
        }

        // Structural escalation: once a generation has been beaten repeatedly, the whole
        // security posture steps up (Low → Medium → High). Still one-way.
        if (analysis.EscapeCount >= 3 && doctrine.SecurityLevel.ToLowerInvariant() != "high")
        {
            var next = doctrine.SecurityLevel.ToLowerInvariant() == "low" ? "medium" : "high";
            doctrine = doctrine with { SecurityLevel = next };
            rationale.Add($"{analysis.EscapeCount} confirmed escapes this generation: " +
                $"security level escalates to {next}.");
        }

        // The ratchet, enforced — a bug upstream must never produce a softer prison.
        doctrine = Ratchet(family.Doctrine, doctrine);

        return new EvolutionProposal(family, family with { Doctrine = doctrine }, analysis, rationale);
    }

    /// <summary>Element-wise monotonicity: the mutated doctrine can only hold the line or tighten it.</summary>
    public static FamilyDefinition.WardenDoctrine Ratchet(
        FamilyDefinition.WardenDoctrine before, FamilyDefinition.WardenDoctrine after) => new()
    {
        SecurityLevel = SecurityRank(after.SecurityLevel) >= SecurityRank(before.SecurityLevel)
            ? after.SecurityLevel
            : before.SecurityLevel,
        ExtraPatrolGuards = Math.Max(before.ExtraPatrolGuards, after.ExtraPatrolGuards),
        ExtraGuardStations = Math.Max(before.ExtraGuardStations, after.ExtraGuardStations),
        FenceLayers = Math.Max(before.FenceLayers, after.FenceLayers),
        HardenedGroundBias = MathF.Max(before.HardenedGroundBias, after.HardenedGroundBias),
        PerimeterPatrol = before.PerimeterPatrol || after.PerimeterPatrol,
        RestrictedUniformAccess = before.RestrictedUniformAccess || after.RestrictedUniformAccess,
    };

    private static int SecurityRank(string level) => level.ToLowerInvariant() switch
    {
        "low" => 0,
        "medium" => 1,
        _ => 2,
    };

    private static string Describe(EscapeAnalysis analysis, string type)
    {
        var location = analysis.Signals.FirstOrDefault(s => s.Type == type)?.Location;
        return location is { } l ? $"({l.X},{l.Y},f{l.Floor})" : "(diffuse)";
    }
}
