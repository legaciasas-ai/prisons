using Prison.Shared.ECS.Components;
using Prison.Shared.Evolution;
using Prison.Shared.Generation;
using Prison.Shared.Interaction;
using Prison.Shared.Items;
using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

/// <summary>
/// Phase 10 (PLAN §9): Escape Analyzer, rule-based Evolution Engine (one-way ratchet), and
/// the full cycle — recorded escapes in, a measurably harder, specifically-countering next
/// generation out, with no human intervention required.
/// </summary>
[Collection("content")]
public class EvolutionTests(TestContent content)
{
    // ---------- helpers ----------

    private static EscapeReport Report(string prisonId = "test-gen1", bool escaped = true,
        TilePos[]? tunnels = null, TilePos[]? fences = null, TilePos[]? lockpicks = null,
        int disguises = 0, int compromises = 0, int observed = 10) => new()
    {
        PrisonId = prisonId,
        Escaped = escaped,
        TunnelsDug = tunnels ?? [],
        FencesCut = fences ?? [],
        DoorsLockpicked = lockpicks ?? [],
        DisguisesWorn = disguises,
        DisguiseCompromises = compromises,
        TimesObserved = observed,
    };

    private FamilyDefinition Blackstone => content.Families.First(f => f.Id == "blackstone");

    private static int CountTiles(MapDefinition map, char glyph) =>
        map.Floors[0].Rows.Sum(row => row.Count(c => c == glyph));

    // ---------- escape detection & report (the pipeline's input) ----------

    [Fact]
    public void CuttingOut_ProducesAnEscapeReport()
    {
        var world = content.BuildWorld();
        var match = MatchFactory.Create(world, content.Map, content.Items, content.Recipes,
            includeMapGuards: false);
        var sim = match.Simulation;

        sim.World.Get<Inventory>(match.Player).Items.Add("wire_cutters");
        sim.World.Get<Position>(match.Player) = new Position(1.5f, 1.5f, 0);
        sim.World.Get<Interactor>(match.Player).Request =
            new InteractionRequest(InteractionKind.CutFence, new TilePos(0, 1, 0));
        for (var i = 0; i < 100; i++)
            sim.Tick();

        // Step through the hole to the map border: that *is* the escape.
        Assert.False(match.Report.EscapeHappened);
        sim.World.Get<Position>(match.Player) = new Position(0.5f, 1.5f, 0);
        for (var i = 0; i < 10; i++)
            sim.Tick();

        Assert.True(match.Report.EscapeHappened, "reaching the border must count as escaped");
        var report = match.Report.Build();
        Assert.True(report.Escaped);
        Assert.Equal(new TilePos(0, 1, 0), report.ExitPosition);
        Assert.Contains(new TilePos(0, 1, 0), report.FencesCut);
        Assert.Equal(content.Map.Id, report.PrisonId);
    }

    // ---------- escape analyzer (§9.1) ----------

    [Fact]
    public void Analyzer_AggregatesAcrossEscapes_AndFindsTheHotspot()
    {
        var cell14 = new TilePos(14, 3, 0);
        var reports = new[]
        {
            Report(tunnels: [cell14, new TilePos(15, 3, 0)]),
            Report(tunnels: [cell14]),
            Report(tunnels: [cell14]),
            Report(fences: [new TilePos(0, 5, 0)]),
        };

        var analysis = EscapeAnalyzer.Analyze("test-gen1", reports);

        Assert.Equal(4, analysis.EscapeCount);
        var tunnel = analysis.Top!;
        Assert.Equal(WeaknessSignal.TunnelRoute, tunnel.Type);
        Assert.Equal(cell14, tunnel.Location);
        // 3 of 4 escapes tunnelled: 9 × 0.75 — a strong aggregate signal (§9.1).
        Assert.Equal(6.75f, tunnel.Score, 2);
        // 1 of 4 cut the fence: a weaker signal, ranked below.
        Assert.Equal(2.5f, analysis.ScoreOf(WeaknessSignal.FenceRoute), 2);
    }

    [Fact]
    public void Analyzer_FailedAttempts_ProduceNoRouteSignals()
    {
        var reports = new[]
        {
            Report(escaped: false, tunnels: [new TilePos(3, 3, 0)]),
            Report(escaped: false, fences: [new TilePos(0, 5, 0)]),
        };
        var analysis = EscapeAnalyzer.Analyze("test-gen1", reports);
        Assert.Equal(0, analysis.EscapeCount);
        Assert.Empty(analysis.Signals);
    }

    // ---------- evolution engine: targeted, one-way mutations (§9.2) ----------

    [Fact]
    public void Engine_CountersTheObservedWeakness_WithRationale()
    {
        var family = Blackstone;
        var analysis = EscapeAnalyzer.Analyze(family.PrisonId, [
            Report(tunnels: [new TilePos(14, 3, 0)]),
            Report(tunnels: [new TilePos(14, 3, 0)]),
            Report(tunnels: [new TilePos(14, 3, 0)]),
        ]);

        var proposal = EvolutionEngine.Propose(family, analysis);

        Assert.True(proposal.ChangesAnything);
        Assert.True(proposal.Mutated.Doctrine.HardenedGroundBias > 0f,
            "tunnel escapes must harden the ground");
        Assert.Equal(family.Doctrine.FenceLayers, proposal.Mutated.Doctrine.FenceLayers);
        Assert.Contains(proposal.Rationale, r => r.Contains("concrete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Engine_NoEscapes_MutatesNothing()
    {
        var family = Blackstone;
        var analysis = EscapeAnalyzer.Analyze(family.PrisonId,
            [Report(escaped: false), Report(escaped: false)]);

        var proposal = EvolutionEngine.Propose(family, analysis);
        Assert.False(proposal.ChangesAnything);
        Assert.Empty(proposal.Rationale);
    }

    [Fact]
    public void Engine_IsAOneWayRatchet()
    {
        var family = Blackstone;

        // Generation N: fence escapes → fence counter-measures.
        var afterFence = EvolutionEngine.Propose(family, EscapeAnalyzer.Analyze(family.PrisonId, [
            Report(fences: [new TilePos(0, 5, 0)]),
            Report(fences: [new TilePos(0, 5, 0)]),
        ])).Mutated;
        Assert.Equal(2, afterFence.Doctrine.FenceLayers);
        Assert.True(afterFence.Doctrine.PerimeterPatrol);

        // Generation N+1: only tunnels this time — fence measures MUST NOT soften (Pillar #9).
        var afterTunnel = EvolutionEngine.Propose(afterFence, EscapeAnalyzer.Analyze(afterFence.PrisonId, [
            Report(tunnels: [new TilePos(9, 9, 0)]),
        ])).Mutated;
        Assert.Equal(2, afterTunnel.Doctrine.FenceLayers);
        Assert.True(afterTunnel.Doctrine.PerimeterPatrol);
        Assert.True(afterTunnel.Doctrine.HardenedGroundBias > 0f);

        // And the ratchet is enforced even against a hostile "softer" doctrine.
        var softened = EvolutionEngine.Ratchet(afterTunnel.Doctrine,
            afterTunnel.Doctrine with { FenceLayers = 1, HardenedGroundBias = 0f, PerimeterPatrol = false });
        Assert.Equal(afterTunnel.Doctrine, softened);
    }

    // ---------- doctrine → generator: counter-measures are physically real ----------

    [Fact]
    public void Generator_ExpressesDoctrineCounterMeasures()
    {
        var generator = new PrisonGenerator(content.Blueprints);
        var baseline = new DesignIntent { Seed = 7 };
        var hardened = baseline with
        {
            FenceLayers = 2,
            HardenedGroundBias = 1f,
            ExtraPatrolGuards = 2,
            PerimeterPatrol = true,
            RestrictedUniformAccess = true,
        };

        var before = generator.Generate(baseline);
        var after = generator.Generate(hardened);

        Assert.True(CountTiles(after, 'f') > CountTiles(before, 'f'),
            "an extra fence layer must add fence tiles");
        Assert.Equal(0, CountTiles(after, 'd')); // fully hardened: nothing left to dig
        Assert.True(after.Guards.Count >= before.Guards.Count + 3, // 2 patrols + 1 perimeter
            $"expected more guards ({before.Guards.Count} → {after.Guards.Count})");
        Assert.DoesNotContain(after.Items, i => i.Id.Contains("uniform"));
        Assert.True(after.Doors.Count(d => d.Locked) >= 2, "each fence ring needs its gate");

        // Harder, but still a working, believable facility (§8.4 gates still apply).
        Assert.True(PrisonValidator.Validate(after, content.Tiles).Passed,
            "counter-measures must never break structural validation");
    }

    // ---------- the Phase 10 deliverable: full cycle, measurably harder next generation ----------

    [Fact]
    public void FullCycle_ProducesAMeasurablyHarderCounteringGeneration()
    {
        var pipeline = new EvolutionPipeline(
            new FamilyPipeline(content.Blueprints, content.StyleKits, content.DecorationRules));
        var family = content.Families.First(f => f.Id == "sunmesa"); // low security: room to grow

        // Generation 1 as players experienced it.
        var familyPipeline = new FamilyPipeline(content.Blueprints, content.StyleKits, content.DecorationRules);
        var gen1 = familyPipeline.GenerateGeneration(family, content.Tiles);
        Assert.NotNull(gen1);

        // A batch of recorded escapes: tunnels dominate, and patrols barely saw anyone.
        var dig = new TilePos(10, 5, 0);
        var reports = new[]
        {
            Report(family.PrisonId, tunnels: [dig], observed: 0),
            Report(family.PrisonId, tunnels: [dig], observed: 1),
            Report(family.PrisonId, tunnels: [dig], observed: 2),
            Report(family.PrisonId, escaped: false, tunnels: [dig]),
        };

        var result = pipeline.Evolve(gen1!.NextFamily, reports, content.Tiles);
        Assert.NotNull(result);

        // The mutation is targeted (anti-tunnel + coverage), never a softening, and explained.
        var doctrine = result!.Proposal.Mutated.Doctrine;
        Assert.True(doctrine.HardenedGroundBias > 0f);
        Assert.True(doctrine.ExtraPatrolGuards > 0);
        Assert.NotEmpty(result.Proposal.Rationale);

        // The next generation is measurably harder in exactly those dimensions.
        var before = gen1.Outcome.Map;
        var after = result.NextGeneration.Outcome.Map;
        Assert.True(CountTiles(after, 'd') < CountTiles(before, 'd'),
            $"diggable ground must shrink ({CountTiles(before, 'd')} → {CountTiles(after, 'd')})");
        Assert.True(after.Guards.Count > before.Guards.Count,
            $"guard presence must grow ({before.Guards.Count} → {after.Guards.Count})");

        // And it still passed every §8.4 gate on the way out (validation + scoring ran inside).
        Assert.True(result.NextGeneration.Outcome.Validation.Passed);
        Assert.True(result.NextGeneration.Outcome.Quality.Passed);

        // Lineage: the new generation knows exactly which prison it evolved from (§10.2).
        Assert.Equal(gen1.NextFamily.PrisonId, result.NextGeneration.PrisonId);
        Assert.Equal(gen1.NextFamily.CurrentGeneration + 1,
            result.NextGeneration.NextFamily.CurrentGeneration);
    }
}
