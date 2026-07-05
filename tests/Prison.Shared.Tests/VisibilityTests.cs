using Prison.Shared.Visibility;
using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

[Collection("content")]
public class VisibilityTests(TestContent content)
{
    private static readonly VisionParameters PlayerVision = VisionParameters.Omnidirectional(12f, 3f);

    [Fact]
    public void Walls_BlockLineOfSight()
    {
        var world = content.BuildWorld();
        var inCell = new TilePos(5, 4, 0);

        var visible = FieldOfView.Compute(world, inCell, PlayerVision);

        Assert.Contains(new TilePos(6, 5, 0), visible);        // same cell
        Assert.DoesNotContain(new TilePos(8, 4, 0), visible);  // neighbouring cell, behind wall
        Assert.DoesNotContain(new TilePos(15, 12, 0), visible); // common room, far behind walls
    }

    [Fact]
    public void GlassWalls_AllowSightButNotMovement()
    {
        var world = content.BuildWorld();
        var corridorBeforeGlass = new TilePos(24, 8, 0); // corridor; glass wall at y=9 guards the stair room

        var visible = FieldOfView.Compute(world, corridorBeforeGlass, PlayerVision);

        Assert.Contains(new TilePos(24, 10, 0), visible); // inside stair room, through glass
        Assert.False(world.IsWalkable(new TilePos(24, 9, 0)));
    }

    [Fact]
    public void Darkness_ShortensEffectiveSightRange_LightRestoresIt()
    {
        var world = content.BuildWorld();

        // Same geometry, same distance (8 tiles of open room/corridor); floor 0 is lit, floor 1 is dark.
        var litOrigin = new TilePos(6, 12, 0);
        var litTarget = new TilePos(14, 12, 0);
        var darkOrigin = new TilePos(6, 12, 1);
        var darkTarget = new TilePos(14, 12, 1);

        Assert.Contains(litTarget, FieldOfView.Compute(world, litOrigin, PlayerVision));
        Assert.DoesNotContain(darkTarget, FieldOfView.Compute(world, darkOrigin, PlayerVision));

        // Nearby tiles are still visible even in the dark (DarkDistance = 3).
        Assert.Contains(new TilePos(8, 12, 1), FieldOfView.Compute(world, darkOrigin, PlayerVision));
    }

    [Fact]
    public void VisionCone_LimitsPerception_OmnidirectionalDoesNot()
    {
        var world = content.BuildWorld();
        var origin = new TilePos(10, 7, 0); // ground corridor, open east and west

        var east = new TilePos(14, 7, 0);
        var west = new TilePos(6, 7, 0);

        // Guard-style 120° cone facing east (PLAN §7.5): sees ahead, not behind.
        var coneEast = VisionParameters.Cone(12f, 3f, facingRadians: 0f);
        var visible = FieldOfView.Compute(world, origin, coneEast);
        Assert.Contains(east, visible);
        Assert.DoesNotContain(west, visible);

        var omni = FieldOfView.Compute(world, origin, PlayerVision);
        Assert.Contains(east, omni);
        Assert.Contains(west, omni);
    }

    [Fact]
    public void FogOfWar_TracksUnseenVisibleRemembered()
    {
        var world = content.BuildWorld();
        var fog = new FogOfWarMap(world);
        var cell = new TilePos(5, 4, 0);
        var corridor = new TilePos(10, 7, 0);

        Assert.Equal(FogState.Unseen, fog.StateAt(cell));

        fog.Update(FieldOfView.Compute(world, cell, PlayerVision));
        Assert.Equal(FogState.Visible, fog.StateAt(cell));

        // Player "moves" to the corridor: the old cell interior is no longer visible.
        fog.Update(FieldOfView.Compute(world, corridor, PlayerVision));
        Assert.Equal(FogState.Remembered, fog.StateAt(cell));
        Assert.Equal(FogState.Visible, fog.StateAt(corridor));
        Assert.Equal(FogState.Unseen, fog.StateAt(new TilePos(20, 5, 1))); // never observed
    }
}
