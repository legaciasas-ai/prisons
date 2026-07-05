using Prison.Shared.Lifecycle;
using Xunit;

namespace Prison.Shared.Tests;

/// <summary>Phase 11 (PLAN §10): prison metadata invariants and the lifecycle state machine.</summary>
public class LifecycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private static PrisonRecord ActiveOfficial()
    {
        var p = PrisonLifecycle.CreateOfficial("blackstone", 3, "blackstone-gen2", T0);
        p = PrisonLifecycle.Advance(p, T0); // Testing
        p = PrisonLifecycle.Advance(p, T0); // Ready
        return PrisonLifecycle.Advance(p, T0); // Active
    }

    [Fact]
    public void Official_InvariantsAreForced()
    {
        var p = PrisonLifecycle.CreateOfficial("blackstone", 3, "blackstone-gen2", T0);
        Assert.True(p.ShareEscapeData);
        Assert.Equal(PrisonVisibility.Public, p.Visibility);
        Assert.Null(p.OwnerId);
        Assert.Equal("blackstone-gen2", p.ParentPrisonId);
        Assert.Equal(PrisonStatus.Generating, p.Status);

        // Policy knobs are host-only: officials can't hide or stop sharing (Pillar #7).
        Assert.Throws<InvalidOperationException>(() =>
            PrisonLifecycle.WithVisibility(p, PrisonVisibility.Private));
        Assert.Throws<InvalidOperationException>(() =>
            PrisonLifecycle.WithShareEscapeData(p, false));
    }

    [Fact]
    public void Community_IsHostControlled()
    {
        var p = PrisonLifecycle.CreateCommunity("myjail", "evan", PrisonVisibility.FriendsOnly,
            shareEscapeData: false, T0);
        Assert.Equal("evan", p.OwnerId);
        Assert.False(p.ShareEscapeData);

        p = PrisonLifecycle.WithShareEscapeData(p, true);
        p = PrisonLifecycle.WithVisibility(p, PrisonVisibility.Private);
        Assert.True(p.ShareEscapeData);
        Assert.Equal(PrisonVisibility.Private, p.Visibility);
    }

    [Fact]
    public void HappyPath_WalksEveryState()
    {
        var p = ActiveOfficial();
        Assert.Equal(PrisonStatus.Active, p.Status);

        p = PrisonLifecycle.Advance(p, T0); // Compromised
        p = PrisonLifecycle.Advance(p, T0.AddHours(25)); // Retiring
        p = PrisonLifecycle.Advance(p, T0.AddHours(26)); // Archived
        Assert.Equal(PrisonStatus.Archived, p.Status);

        // Archived is terminal (§10.2: preserved for the museum, never revived in place).
        Assert.Throws<InvalidOperationException>(() => PrisonLifecycle.Advance(p, T0.AddHours(27)));
    }

    [Fact]
    public void Compromise_StampsTheMandatory24hWindow()
    {
        var p = PrisonLifecycle.Compromise(ActiveOfficial(), T0);
        Assert.Equal(T0, p.CompromisedAt);
        Assert.Equal(T0.AddHours(24), p.RetireAt);
        Assert.Equal(TimeSpan.FromHours(6), PrisonLifecycle.RetiresIn(p, T0.AddHours(18)));

        // Only Active prisons can be beaten; and the window blocks early retirement.
        Assert.Throws<InvalidOperationException>(() => PrisonLifecycle.Compromise(p, T0));
        Assert.Throws<InvalidOperationException>(() => PrisonLifecycle.Advance(p, T0.AddHours(23)));

        // §10.4 emergency power: an admin may force it early.
        var forced = PrisonLifecycle.Advance(p, T0.AddHours(1), adminOverride: true);
        Assert.Equal(PrisonStatus.Retiring, forced.Status);
    }

    [Fact]
    public void NextGeneration_PreservesLineage()
    {
        var retired = PrisonLifecycle.Compromise(ActiveOfficial(), T0);
        var next = PrisonLifecycle.NextGeneration(retired, T0.AddHours(25));

        Assert.Equal("blackstone", next.FamilyId);
        Assert.Equal(4, next.Generation);
        Assert.Equal(retired.PrisonId, next.ParentPrisonId);
        Assert.Equal(PrisonStatus.Generating, next.Status);

        var community = PrisonLifecycle.CreateCommunity("myjail", "evan", PrisonVisibility.Public, true, T0);
        Assert.Throws<InvalidOperationException>(() => PrisonLifecycle.NextGeneration(community, T0));
    }
}
