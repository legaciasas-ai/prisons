using Prison.Shared.World;

namespace Prison.Shared.Generation;

public sealed record QualityReport(float Total, IReadOnlyDictionary<string, float> Metrics)
{
    public bool Passed => Total >= QualityScorer.PassThreshold;
}

/// <summary>
/// Quality scoring v1 (PLAN §8.3 step 10, §8.4.B): believability and enjoyability heuristics,
/// each 0–100, combined into a weighted total. Escapability is deliberately not scored
/// (Pillar #9). These are first-pass heuristics — tune with the Phase 5 preview tool in hand.
/// </summary>
public static class QualityScorer
{
    public const float PassThreshold = 55f;

    public static QualityReport Score(MapDefinition map, TileRegistry tiles)
    {
        var world = map.BuildWorld(tiles);
        var metrics = new Dictionary<string, float>
        {
            // Believability: a real prison has varied facilities, not endless identical blocks.
            ["room_variety"] = 100f * Math.Min(1f, map.Rooms.Select(r => r.Type).Distinct().Count() / 9f),

            // Believability: enough service rooms for the population it claims to house.
            ["service_coverage"] = 100f * Math.Min(1f,
                map.Rooms.Count(r => r.Type != "cell_block") / (float)DesignIntent.RequiredRoomTypes.Length),

            ["walkable_density"] = DensityScore(world, map),
            ["meal_walk"] = MealWalkScore(world, map),
            ["patrol_coverage"] = PatrolCoverageScore(map),
        };

        var total =
            metrics["room_variety"] * 0.25f +
            metrics["service_coverage"] * 0.10f +
            metrics["walkable_density"] * 0.20f +
            metrics["meal_walk"] * 0.25f +
            metrics["patrol_coverage"] * 0.20f;

        return new QualityReport(total, metrics);
    }

    /// <summary>Enjoyability: neither a featureless open box nor a claustrophobic wall maze.</summary>
    private static float DensityScore(WorldGrid world, MapDefinition map)
    {
        if (map.Rooms.Count == 0)
            return 0f;
        var x0 = map.Rooms.Min(r => r.X0);
        var y0 = map.Rooms.Min(r => r.Y0);
        var x1 = map.Rooms.Max(r => r.X1);
        var y1 = map.Rooms.Max(r => r.Y1);

        var walkable = 0;
        var area = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                area++;
                if (world.IsWalkable(new TilePos(x, y, 0)))
                    walkable++;
            }

        var density = walkable / (float)area;
        // Ideal band ~[0.45, 0.75]; linear falloff outside it.
        return density switch
        {
            < 0.45f => 100f * density / 0.45f,
            > 0.75f => 100f * Math.Max(0f, 1f - (density - 0.75f) / 0.25f),
            _ => 100f,
        };
    }

    /// <summary>Enjoyability/believability: the walk from your cell to food should be sane.</summary>
    private static float MealWalkScore(WorldGrid world, MapDefinition map)
    {
        var cafeterias = map.Rooms.Where(r => r.Type == "cafeteria").ToList();
        if (cafeterias.Count == 0)
            return 0f;
        var targets = cafeterias.SelectMany(r => PrisonValidator.InteriorWalkable(world, r)).ToHashSet();
        var distance = GridFlood.Distance(world, map.PlayerSpawn.Position, targets);
        if (distance < 0)
            return 0f;
        // ≤50 tiles: fine; degrades to 0 at 150 (tedious trek — §8.4.B enjoyability).
        return distance <= 50 ? 100f : Math.Max(0f, 100f * (1f - (distance - 50) / 100f));
    }

    /// <summary>Believability: patrol routes should pass near most of the facility.</summary>
    private static float PatrolCoverageScore(MapDefinition map)
    {
        if (map.Guards.Count == 0 || map.Rooms.Count == 0)
            return 0f;
        var waypoints = map.Guards
            .SelectMany(g => g.Patrol.Select(wp => new TilePos(wp[0], wp[1], g.Floor)))
            .ToList();

        var covered = map.Rooms.Count(room =>
        {
            var center = new TilePos((room.X0 + room.X1) / 2, (room.Y0 + room.Y1) / 2, room.Floor);
            return waypoints.Any(wp => TilePos.EuclideanDistance(center, wp) <= 14f);
        });

        return 100f * covered / map.Rooms.Count;
    }
}
