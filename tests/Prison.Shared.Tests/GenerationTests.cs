using Prison.Shared.Generation;
using Prison.Shared.Pathfinding;
using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

/// <summary>
/// Phase 5 (PLAN §8): the offline pipeline generates a structurally valid, connected,
/// reachable prison from a Design Intent, and rejects/regenerates failures automatically.
/// Escapability is never part of any assertion here (Pillar #9).
/// </summary>
[Collection("content")]
public class GenerationTests(TestContent content)
{
    private PrisonGenerator NewGenerator() => new(content.Blueprints);

    [Fact]
    public void BlueprintLibrary_LoadsWithRequiredRoomTypes()
    {
        Assert.True(content.Blueprints.Count >= 10);
        Assert.Contains(content.Blueprints, b => b.Type == "cell_block" && b.Capacity > 0);
        foreach (var type in DesignIntent.RequiredRoomTypes)
            Assert.Contains(content.Blueprints, b => b.Type == type);
    }

    [Fact]
    public void Generation_IsDeterministicPerSeed_AndVariesAcrossSeeds()
    {
        var generator = NewGenerator();
        var intent = new DesignIntent { Seed = 42, Capacity = 24 };

        var a = generator.Generate(intent);
        var b = generator.Generate(intent);
        Assert.Equal(a.Floors[0].Rows, b.Floors[0].Rows);

        var c = generator.Generate(intent with { Seed = 43 });
        Assert.NotEqual(a.Floors[0].Rows, c.Floors[0].Rows);
    }

    [Fact]
    public void GenerateBest_ProducesAValidatedScoredPlayablePrison()
    {
        var generator = NewGenerator();
        var outcome = generator.GenerateBest(
            new DesignIntent { Seed = 7, Capacity = 30, Security = SecurityLevel.High },
            content.Tiles);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Validation.Passed, string.Join("; ", outcome.Validation.Failures));
        Assert.True(outcome.Quality.Passed, $"quality {outcome.Quality.Total:F1} below threshold");

        // The generated map loads through the exact same path as hand-authored content
        // and the *real* pathfinder routes from the spawn cell to the cafeteria.
        var world = outcome.Map.BuildWorld(content.Tiles);
        var cafeteria = outcome.Map.Rooms.First(r => r.Type == "cafeteria");
        var target = PrisonValidator.InteriorWalkable(world, cafeteria)[0];
        var path = new HierarchicalPathfinder(world).FindPath(outcome.Map.PlayerSpawn.Position, target);
        Assert.NotNull(path);
        Assert.True(path!.Count > 5);

        // Believable facility shape: enough capacity, guards present, perimeter gate locked.
        Assert.True(outcome.Map.Rooms.Count(r => r.Type == "cell_block") >= 3);
        Assert.True(outcome.Map.Guards.Count >= 3, "high security should staff several guards");
        Assert.Contains(outcome.Map.Doors, d => d.Locked);
        Assert.NotEmpty(outcome.Map.Zones); // guard station / admin restricted zones
    }

    [Fact]
    public void GeneratedPrison_RunsInTheActualSimulation()
    {
        var generator = NewGenerator();
        var outcome = generator.GenerateBest(new DesignIntent { Seed = 11, Capacity = 16 }, content.Tiles);
        Assert.NotNull(outcome);

        var world = outcome!.Map.BuildWorld(content.Tiles);
        var match = MatchFactory.Create(world, outcome.Map, content.Items, content.Recipes);
        for (var i = 0; i < 100; i++)
            match.Simulation.Tick(); // no exceptions: guards patrol, systems run

        Assert.True(match.Escape.Presence.Max > 0, "escape recorder sampled the player");
    }

    [Fact]
    public void Validator_RejectsABrokenFacility()
    {
        var generator = NewGenerator();
        var map = generator.Generate(new DesignIntent { Seed = 5, Capacity = 16 });
        Assert.True(PrisonValidator.Validate(map, content.Tiles).Passed);

        // Sabotage: wall the player's spawn row clean across the map.
        var rows = map.Floors[0].Rows;
        rows[map.PlayerSpawn.Y] = new string('#', rows[map.PlayerSpawn.Y].Length);

        var report = PrisonValidator.Validate(map, content.Tiles);
        Assert.False(report.Passed);
        Assert.NotEmpty(report.Failures);
    }

    [Fact]
    public void QualityScorer_ReportsBoundedMetrics()
    {
        var generator = NewGenerator();
        var map = generator.Generate(new DesignIntent { Seed = 13, Capacity = 20 });
        var report = QualityScorer.Score(map, content.Tiles);

        Assert.InRange(report.Total, 0f, 100f);
        Assert.True(report.Metrics.Count >= 5);
        foreach (var (metric, value) in report.Metrics)
            Assert.InRange(value, 0f, 100f);
    }
}
