namespace Prison.Shared.Lifecycle;

public enum HostType { Official, Community }

public enum PrisonVisibility { Public, Private, FriendsOnly }

public enum PrisonStatus { Generating, Testing, Ready, Active, Compromised, Retiring, Archived }

/// <summary>
/// The prison metadata record (PLAN §10.1/§11.1 `Prisons` table shape). `HostType` is
/// metadata, not a code path: simulation/generation/hosting code is identical either way —
/// only policy (data routing, visibility, admin rights) reads this flag.
/// </summary>
public sealed record PrisonRecord
{
    public required string PrisonId { get; init; }

    public required string FamilyId { get; init; }

    public int Generation { get; init; } = 1;

    public HostType HostType { get; init; }

    public PrisonStatus Status { get; init; } = PrisonStatus.Generating;

    /// <summary>Null for official prisons; the hosting player for community ones.</summary>
    public string? OwnerId { get; init; }

    public PrisonVisibility Visibility { get; init; } = PrisonVisibility.Public;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompromisedAt { get; init; }

    /// <summary>Scheduled replacement time, set the instant the prison is Compromised (§10.2).</summary>
    public DateTimeOffset? RetireAt { get; init; }

    /// <summary>Always true for official prisons (Pillar #7); host-controlled for community.</summary>
    public bool ShareEscapeData { get; init; }

    /// <summary>Previous generation — the explicit lineage tree (§10.2).</summary>
    public string? ParentPrisonId { get; init; }
}

/// <summary>
/// The lifecycle state machine (PLAN §10.2): Generating → Testing → Ready → Active →
/// Compromised → Retiring → Archived. A new generation is only ever born because players
/// defeated the old one (Pillar #6), and a Compromised prison stays playable for a minimum
/// of 24 hours — replacement is an event, not a blink.
/// </summary>
public static class PrisonLifecycle
{
    public static readonly TimeSpan MinimumCompromisedWindow = TimeSpan.FromHours(24);

    private static readonly Dictionary<PrisonStatus, PrisonStatus> Forward = new()
    {
        [PrisonStatus.Generating] = PrisonStatus.Testing,
        [PrisonStatus.Testing] = PrisonStatus.Ready,
        [PrisonStatus.Ready] = PrisonStatus.Active,
        [PrisonStatus.Active] = PrisonStatus.Compromised,
        [PrisonStatus.Compromised] = PrisonStatus.Retiring,
        [PrisonStatus.Retiring] = PrisonStatus.Archived,
    };

    // ---- creation (host-type invariants enforced at the source, §10.1/§10.3) ----

    public static PrisonRecord CreateOfficial(string familyId, int generation,
        string? parentPrisonId, DateTimeOffset now) => new()
    {
        PrisonId = $"{familyId}-gen{generation}",
        FamilyId = familyId,
        Generation = generation,
        HostType = HostType.Official,
        OwnerId = null,
        Visibility = PrisonVisibility.Public, // official prisons are always public
        ShareEscapeData = true,               // always on, non-optional (Pillar #7)
        CreatedAt = now,
        ParentPrisonId = parentPrisonId,
    };

    public static PrisonRecord CreateCommunity(string familyId, string ownerId,
        PrisonVisibility visibility, bool shareEscapeData, DateTimeOffset now) => new()
    {
        PrisonId = $"{familyId}-{ownerId}-{now.ToUnixTimeSeconds()}",
        FamilyId = familyId,
        HostType = HostType.Community,
        OwnerId = ownerId,
        Visibility = visibility,
        ShareEscapeData = shareEscapeData,
        CreatedAt = now,
    };

    // ---- host-controlled policy (community only) ----

    public static PrisonRecord WithVisibility(PrisonRecord prison, PrisonVisibility visibility) =>
        prison.HostType == HostType.Community
            ? prison with { Visibility = visibility }
            : throw new InvalidOperationException("Official prisons are always public");

    public static PrisonRecord WithShareEscapeData(PrisonRecord prison, bool share) =>
        prison.HostType == HostType.Community
            ? prison with { ShareEscapeData = share }
            : throw new InvalidOperationException("Official prisons always share escape data");

    // ---- transitions ----

    /// <summary>Marks a live prison beaten: it stays fully playable, with a visible
    /// countdown, for at least 24 hours (§10.2's reasons: verification, event-worthiness).</summary>
    public static PrisonRecord Compromise(PrisonRecord prison, DateTimeOffset now)
    {
        if (prison.Status != PrisonStatus.Active)
            throw new InvalidOperationException($"Only an Active prison can be Compromised (was {prison.Status})");
        return prison with
        {
            Status = PrisonStatus.Compromised,
            CompromisedAt = now,
            RetireAt = now + MinimumCompromisedWindow,
        };
    }

    /// <summary>
    /// Advances one lifecycle step. The Compromised→Retiring step refuses to run before
    /// the 24h window elapses unless <paramref name="adminOverride"/> — the §10.4 emergency
    /// power (e.g. manually retiring a broken prison early).
    /// </summary>
    public static PrisonRecord Advance(PrisonRecord prison, DateTimeOffset now, bool adminOverride = false)
    {
        if (!Forward.TryGetValue(prison.Status, out var next))
            throw new InvalidOperationException($"{prison.Status} is terminal");

        if (next == PrisonStatus.Compromised)
            return Compromise(prison, now); // needs the timestamp side effects

        if (prison.Status == PrisonStatus.Compromised && !adminOverride && now < prison.RetireAt)
            throw new InvalidOperationException(
                $"Compromised window not elapsed: retires at {prison.RetireAt:u} (admin override required)");

        return prison with { Status = next };
    }

    /// <summary>Remaining playable time on a Compromised prison — the player-facing countdown.</summary>
    public static TimeSpan? RetiresIn(PrisonRecord prison, DateTimeOffset now) =>
        prison is { Status: PrisonStatus.Compromised, RetireAt: { } at }
            ? at - now
            : null;

    /// <summary>The replacement generation's record, preserving family lineage (§10.2).</summary>
    public static PrisonRecord NextGeneration(PrisonRecord retired, DateTimeOffset now)
    {
        if (retired.HostType != HostType.Official)
            throw new InvalidOperationException("Community prisons evolve locally; no official lineage record");
        return CreateOfficial(retired.FamilyId, retired.Generation + 1, retired.PrisonId, now);
    }
}
