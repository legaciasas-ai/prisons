using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

[Collection("content")]
public class WorldTests(TestContent content)
{
    [Fact]
    public void TileRegistry_LoadsDataDrivenDefinitions()
    {
        var tiles = content.Tiles;

        var concreteFloor = tiles.Get(tiles.IdOf("concrete_floor"));
        Assert.True(concreteFloor.Walkable);
        Assert.True(concreteFloor.CanDig);

        var wall = tiles.Get(tiles.IdOf("concrete_wall"));
        Assert.False(wall.Walkable);
        Assert.True(wall.BlocksSight);

        // A glass wall blocks movement but not sight — purely from data, no special-casing (Pillar #4).
        var glass = tiles.Get(tiles.IdOf("glass_wall"));
        Assert.False(glass.Walkable);
        Assert.False(glass.BlocksSight);
    }

    [Fact]
    public void Map_BuildsMultiFloorWorldWithStairs()
    {
        var world = content.BuildWorld();

        Assert.Equal(2, world.FloorCount);
        Assert.True(world.IsWalkable(content.Map.PlayerSpawn.Position));

        var stairsBottom = new TilePos(25, 12, 0);
        var stairsTop = new TilePos(25, 12, 1);
        Assert.NotNull(world.StairAt(stairsBottom));
        Assert.Equal(stairsTop, world.StairAt(stairsBottom)!.Value.OtherEnd(stairsBottom));
        Assert.True(world.IsWalkable(stairsTop));
    }

    [Fact]
    public void Walls_BlockWalkability_VoidIsNotWalkable()
    {
        var world = content.BuildWorld();

        Assert.False(world.IsWalkable(new TilePos(4, 3, 0)));   // building wall
        Assert.False(world.IsWalkable(new TilePos(0, 0, 0)));   // perimeter fence
        Assert.False(world.IsWalkable(new TilePos(0, 0, 1)));   // void outside upper floor
        Assert.True(world.IsWalkable(new TilePos(10, 7, 0)));   // ground corridor
    }

    [Fact]
    public void Lighting_BakesAmbientAndPointLights()
    {
        var world = content.BuildWorld();

        // Floor 1 is dark (ambient 0.3) except near its two lamps.
        Assert.Equal(0.3f, world.LightAt(new TilePos(24, 4, 1)), precision: 2);
        Assert.True(world.LightAt(new TilePos(7, 6, 1)) > 0.9f);

        // Floor 0 is bright everywhere.
        Assert.True(world.LightAt(new TilePos(10, 7, 0)) >= 0.9f);
    }
}
