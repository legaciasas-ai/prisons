using Prison.Shared.Generation;
using Xunit;

namespace Prison.Shared.Tests;

/// <summary>
/// Phase 6 (PLAN §8.1–8.2, §8.5): the same functional layout rendered through two style kits
/// is gameplay-identical but visually distinct, and a family's next generation stays visually
/// recognizable while its layout changes.
/// </summary>
[Collection("content")]
public class StyleAndFamilyTests(TestContent content)
{
    private FamilyPipeline NewPipeline() =>
        new(content.Blueprints, content.StyleKits, content.DecorationRules);

    [Fact]
    public void StyleKits_AreVisualOnly_MaterialsMatchDefaultGameplayProperties()
    {
        Assert.True(content.StyleKits.Count >= 5);
        var reference = content.Tiles.Get(content.Tiles.IdOf("concrete_wall"));
        var referenceFloor = content.Tiles.Get(content.Tiles.IdOf("concrete_floor"));

        foreach (var kit in content.StyleKits)
        {
            var wall = content.Tiles.Get(content.Tiles.IdOf(kit.WallMaterial));
            Assert.False(wall.Walkable);
            Assert.Equal(reference.BlocksSight, wall.BlocksSight);
            Assert.Equal(reference.CanDig, wall.CanDig);

            var floor = content.Tiles.Get(content.Tiles.IdOf(kit.FloorMaterial));
            Assert.True(floor.Walkable);
            Assert.Equal(referenceFloor.MovementCost, floor.MovementCost);
            Assert.Equal(referenceFloor.BlocksSight, floor.BlocksSight);
        }
    }

    [Fact]
    public void SameLayout_TwoKits_IdenticalGameplay_DifferentVisuals()
    {
        var generator = new PrisonGenerator(content.Blueprints);
        var map = generator.Generate(new DesignIntent { Seed = 21, Capacity = 20 });

        var brutalist = StylePasses.ApplyStyleKit(map, content.StyleKits.First(k => k.Id == "brutalist_concrete"));
        var victorian = StylePasses.ApplyStyleKit(map, content.StyleKits.First(k => k.Id == "redbrick_victorian"));

        // Identical layout (same glyph rows), different materials behind the glyphs.
        Assert.Equal(brutalist.Floors[0].Rows, victorian.Floors[0].Rows);
        Assert.NotEqual(brutalist.Legend["#"].Wall, victorian.Legend["#"].Wall);

        // Gameplay-identical: same walkability everywhere, through real world builds.
        var worldA = brutalist.BuildWorld(content.Tiles);
        var worldB = victorian.BuildWorld(content.Tiles);
        for (var y = 0; y < worldA.Floor(0).Height; y++)
            for (var x = 0; x < worldA.Floor(0).Width; x++)
                Assert.Equal(
                    worldA.IsWalkable(new World.TilePos(x, y, 0)),
                    worldB.IsWalkable(new World.TilePos(x, y, 0)));
    }

    [Fact]
    public void Decoration_IsDeterministic_Sparse_AndKeepsTheFacilityValid()
    {
        var generator = new PrisonGenerator(content.Blueprints);
        var map = generator.Generate(new DesignIntent { Seed = 33, Capacity = 20 });
        var rules = content.DecorationRules.First(r => r.Id == "aging_facility");

        var a = StylePasses.ApplyDecoration(map, rules, seed: 5);
        var b = StylePasses.ApplyDecoration(map, rules, seed: 5);
        Assert.Equal(a.Floors[0].Rows, b.Floors[0].Rows); // deterministic

        var decorated = string.Join("", a.Floors[0].Rows);
        Assert.Contains('c', decorated); // some cracks appeared...
        var crackRatio = decorated.Count(ch => ch == 'c') / (float)decorated.Count(ch => ch == '.');
        Assert.True(crackRatio < 0.3f, "decoration must stay sparse");

        Assert.True(PrisonValidator.Validate(a, content.Tiles).Passed,
            "decoration is cosmetic — the facility must still validate");
    }

    [Fact]
    public void Exterior_AddsRoadFromGate_AndVegetation()
    {
        var generator = new PrisonGenerator(content.Blueprints);
        var map = generator.Generate(new DesignIntent { Seed = 8, Capacity = 20 });
        var exterior = StylePasses.ApplyExterior(map, seed: 8);

        var gate = exterior.Doors.First(d => d.Locked);
        var rowBelowGate = exterior.Floors[0].Rows[gate.Y + 1];
        Assert.Equal('d', rowBelowGate[gate.X]); // access road leaves the gate

        Assert.Contains(exterior.Floors[0].Rows, r => r.Contains('T')); // trees outside
        Assert.True(exterior.Items.Count(i => i.Id == "rock") >= map.Items.Count(i => i.Id == "rock"));
    }

    [Fact]
    public void FamilyPipeline_ProducesLineage_AndConsistentVisualIdentity()
    {
        var pipeline = NewPipeline();
        var blackstone = content.Families.First(f => f.Id == "blackstone");

        var gen1 = pipeline.GenerateGeneration(blackstone, content.Tiles);
        Assert.NotNull(gen1);
        Assert.Equal("blackstone-gen1", gen1!.PrisonId);
        Assert.Null(gen1.ParentPrisonId);
        Assert.True(gen1.Outcome.Validation.Passed);

        var gen2 = pipeline.GenerateGeneration(gen1.NextFamily, content.Tiles);
        Assert.NotNull(gen2);
        Assert.Equal("blackstone-gen2", gen2!.PrisonId);
        Assert.Equal("blackstone-gen1", gen2.ParentPrisonId); // explicit lineage (§10.2)

        // Recognizably the same family: identical construction materials...
        Assert.Equal(gen1.Outcome.Map.Legend["#"].Wall, gen2.Outcome.Map.Legend["#"].Wall);
        // ...but an evolved, different layout.
        Assert.NotEqual(gen1.Outcome.Map.Floors[0].Rows, gen2.Outcome.Map.Floors[0].Rows);
    }

    [Fact]
    public void TwoFamilies_AreVisuallyDistinct()
    {
        var pipeline = NewPipeline();
        var blackstone = pipeline.GenerateGeneration(content.Families.First(f => f.Id == "blackstone"), content.Tiles);
        var sunmesa = pipeline.GenerateGeneration(content.Families.First(f => f.Id == "sunmesa"), content.Tiles);

        Assert.NotNull(blackstone);
        Assert.NotNull(sunmesa);
        Assert.NotEqual(
            blackstone!.Outcome.Map.Legend["#"].Wall,
            sunmesa!.Outcome.Map.Legend["#"].Wall);
        Assert.NotEqual(
            blackstone.Outcome.Map.Legend["."].Floor,
            sunmesa.Outcome.Map.Legend["."].Floor);
    }
}
