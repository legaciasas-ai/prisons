using Prison.Shared.World;

namespace Prison.Shared.Visibility;

/// <summary>
/// The one and only line-of-sight implementation (Pillar #2): recursive shadowcasting over
/// the world's transparency data, constrained by a vision cone and by lighting-dependent
/// range. No raycast, no line of sight ⇒ no detection, ever — for players and NPCs alike.
/// </summary>
public static class FieldOfView
{
    // Octant transform multipliers for recursive shadowcasting (Björn Bergström's algorithm).
    private static readonly int[] MultXx = [1, 0, 0, -1, -1, 0, 0, 1];
    private static readonly int[] MultXy = [0, 1, -1, 0, 0, -1, 1, 0];
    private static readonly int[] MultYx = [0, 1, 1, 0, 0, -1, -1, 0];
    private static readonly int[] MultYy = [1, 0, 0, 1, -1, 0, 0, -1];

    /// <summary>Computes the set of tiles visible from <paramref name="origin"/> on its floor.</summary>
    public static HashSet<TilePos> Compute(WorldGrid world, TilePos origin, in VisionParameters vision)
    {
        var visible = new HashSet<TilePos> { origin };
        if (!world.InBounds(origin))
            return visible;

        var range = (int)MathF.Ceiling(vision.MaxDistance);
        for (var octant = 0; octant < 8; octant++)
            CastLight(world, origin, vision, range, 1, 1f, 0f,
                MultXx[octant], MultXy[octant], MultYx[octant], MultYy[octant], visible);

        return visible;
    }

    /// <summary>True if a specific target tile is visible from origin under these parameters.</summary>
    public static bool CanSee(WorldGrid world, TilePos origin, TilePos target, in VisionParameters vision) =>
        Compute(world, origin, vision).Contains(target);

    private static void CastLight(
        WorldGrid world, TilePos origin, in VisionParameters vision, int range,
        int row, float start, float end, int xx, int xy, int yx, int yy, HashSet<TilePos> visible)
    {
        if (start < end)
            return;

        var newStart = 0f;
        var blocked = false;
        for (var distance = row; distance <= range && !blocked; distance++)
        {
            var deltaY = -distance;
            for (var deltaX = -distance; deltaX <= 0; deltaX++)
            {
                var leftSlope = (deltaX - 0.5f) / (deltaY + 0.5f);
                var rightSlope = (deltaX + 0.5f) / (deltaY - 0.5f);
                if (start < rightSlope)
                    continue;
                if (end > leftSlope)
                    break;

                var tile = origin with
                {
                    X = origin.X + deltaX * xx + deltaY * xy,
                    Y = origin.Y + deltaX * yx + deltaY * yy,
                };
                var inBounds = world.InBounds(tile);

                if (inBounds && IsWithinSight(world, origin, tile, vision))
                    visible.Add(tile);

                var tileBlocks = !inBounds || world.BlocksSight(tile);
                if (blocked)
                {
                    if (tileBlocks)
                    {
                        newStart = rightSlope;
                    }
                    else
                    {
                        blocked = false;
                        start = newStart;
                    }
                }
                else if (tileBlocks && distance < range)
                {
                    blocked = true;
                    CastLight(world, origin, vision, range, distance + 1, start, leftSlope, xx, xy, yx, yy, visible);
                    newStart = rightSlope;
                }
            }
        }
    }

    private static bool IsWithinSight(WorldGrid world, TilePos origin, TilePos tile, in VisionParameters vision)
    {
        var distance = TilePos.EuclideanDistance(origin, tile);

        // Lighting shortens (or restores) the effective range at the *target* tile (PLAN §7.4).
        var light = Math.Clamp(world.LightAt(tile), 0f, 1f);
        var effectiveRange = vision.DarkDistance + (vision.MaxDistance - vision.DarkDistance) * light;
        if (distance > effectiveRange)
            return false;

        if (vision.FovDegrees < 360f && distance > 0.5f)
        {
            var angleToTile = MathF.Atan2(tile.Y - origin.Y, tile.X - origin.X);
            var delta = MathF.Abs(NormalizeAngle(angleToTile - vision.FacingRadians));
            if (delta > float.DegreesToRadians(vision.FovDegrees) / 2f)
                return false;
        }

        return true;
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > MathF.PI)
            angle -= MathF.Tau;
        while (angle < -MathF.PI)
            angle += MathF.Tau;
        return angle;
    }

}
