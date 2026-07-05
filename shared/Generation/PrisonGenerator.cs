using Prison.Shared.World;

namespace Prison.Shared.Generation;

/// <summary>
/// Generation pipeline v1 (PLAN §8.3 steps 1–4, offline): resolves a Design Intent into a
/// playable <see cref="MapDefinition"/> by assembling Functional Blueprints along a central
/// circulation spine, wrapped in a yard + dirt strip + perimeter fence. Deliberately simple
/// grammar (two room rows flanking one corridor) — districts, multi-corridor circulation and
/// style kits layer on in later phases. Escapability is *never* tested (Pillar #9).
/// </summary>
public sealed class PrisonGenerator(IReadOnlyList<BlueprintDefinition> blueprints)
{
    /// <summary>Ring sizes, outermost in: grass(2) · fence(1) · dirt(2) → compound offset 5.</summary>
    public const int PerimeterOffset = 5;

    public const int CorridorHeight = 2;

    /// <summary>Standard glyph legend shared by generated maps and hand-authored ones.</summary>
    private static readonly Dictionary<string, MapDefinition.LegendEntry> Legend = new()
    {
        ["#"] = new() { Wall = "concrete_wall", Floor = "concrete_floor" },
        ["."] = new() { Floor = "concrete_floor" },
        ["m"] = new() { Floor = "metal_floor" },
        ["g"] = new() { Wall = "glass_wall", Floor = "concrete_floor" },
        ["f"] = new() { Wall = "chain_fence", Floor = "dirt" },
        [","] = new() { Floor = "grass" },
        ["d"] = new() { Floor = "dirt" },
        [" "] = new(),
    };

    private sealed record Placement(BlueprintDefinition Blueprint, int X, int Y, bool Mirrored);

    public MapDefinition Generate(DesignIntent intent)
    {
        var rng = new Random(intent.Seed);
        var rooms = SelectRooms(intent, rng);

        // Circulation planning (step 3): balance the rooms into two rows flanking the spine.
        var (north, south) = SplitBalanced(rooms, rng);

        var northMaxH = north.Max(b => b.Height);
        var southMaxH = south.Max(b => b.Height);
        var compoundW = Math.Max(RowWidth(north), RowWidth(south));
        var compoundH = northMaxH + CorridorHeight + southMaxH;

        var width = compoundW + 2 * PerimeterOffset;
        var height = compoundH + 2 * PerimeterOffset;
        var canvas = NewCanvas(width, height);

        // Corridor spine.
        var corrY0 = PerimeterOffset + northMaxH;
        for (var y = corrY0; y < corrY0 + CorridorHeight; y++)
            for (var x = PerimeterOffset; x < PerimeterOffset + compoundW; x++)
                canvas[y][x] = '.';

        // Architecture assembly (step 4): stamp rooms, bottom-aligned above / top-aligned below.
        var placements = new List<Placement>();
        var cursor = PerimeterOffset;
        foreach (var blueprint in north)
        {
            placements.Add(new Placement(blueprint, cursor, corrY0 - blueprint.Height, Mirrored: false));
            cursor += blueprint.Width - 1; // share the wall column with the neighbour
        }

        cursor = PerimeterOffset;
        foreach (var blueprint in south)
        {
            placements.Add(new Placement(blueprint, cursor, corrY0 + CorridorHeight, Mirrored: true));
            cursor += blueprint.Width - 1;
        }

        var doors = new List<MapDefinition.MapDoor>();
        foreach (var placement in placements)
            Stamp(canvas, placement);

        // Close the corridor's flanks where no room provides a wall; both ends stay open to the yard.
        for (var x = PerimeterOffset; x < PerimeterOffset + compoundW; x++)
        {
            if (canvas[corrY0 - 1][x] == ',')
                canvas[corrY0 - 1][x] = '#';
            if (canvas[corrY0 + CorridorHeight][x] == ',')
                canvas[corrY0 + CorridorHeight][x] = '#';
        }

        // The perimeter gate: a locked door punched through the south fence.
        doors.Add(new MapDefinition.MapDoor { X = width / 2, Y = height - 3, Locked = true });

        return Compose(intent, rng, canvas, width, height, placements, doors);
    }

    /// <summary>Runs the full offline loop (§8.3 steps 5 & 10): generate → validate → score,
    /// discarding failures, returning the best-scoring candidate (null if every attempt failed).</summary>
    public GenerationOutcome? GenerateBest(DesignIntent intent, TileRegistry tiles, int maxAttempts = 8)
    {
        GenerationOutcome? best = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var seed = intent.Seed + attempt * 1009;
            var map = Generate(intent with { Seed = seed });
            var validation = PrisonValidator.Validate(map, tiles);
            if (!validation.Passed)
                continue;

            var quality = QualityScorer.Score(map, tiles);
            if (!quality.Passed)
                continue;

            if (best is null || quality.Total > best.Quality.Total)
                best = new GenerationOutcome(map, validation, quality, seed, attempt + 1);
        }

        return best is null ? null : best with { Attempts = maxAttempts };
    }

    // ---------- room selection (step 1/2) ----------

    private List<BlueprintDefinition> SelectRooms(DesignIntent intent, Random rng)
    {
        var cellBlocks = blueprints.Where(b => b.Type == "cell_block").ToList();
        if (cellBlocks.Count == 0)
            throw new InvalidOperationException("Blueprint library has no cell_block blueprints");

        var rooms = new List<BlueprintDefinition>();
        var housed = 0;
        while (housed < intent.Capacity)
        {
            var block = PickWeighted(cellBlocks, intent, rng);
            rooms.Add(block);
            housed += Math.Max(1, block.Capacity);
        }

        foreach (var type in DesignIntent.RequiredRoomTypes)
        {
            var candidates = blueprints.Where(b => b.Type == type).ToList();
            if (candidates.Count == 0)
                throw new InvalidOperationException($"Blueprint library has no '{type}' blueprint");
            rooms.Add(PickWeighted(candidates, intent, rng));
        }

        var stations = blueprints.Where(b => b.Type == "guard_station").ToList();
        if (stations.Count == 0)
            throw new InvalidOperationException("Blueprint library has no guard_station blueprint");
        for (var i = 0; i < intent.GuardStations; i++)
            rooms.Add(PickWeighted(stations, intent, rng));

        return rooms;
    }

    /// <summary>Family-preferred blueprints (§8.1 DNA) are three times as likely to be chosen.</summary>
    private static BlueprintDefinition PickWeighted(
        List<BlueprintDefinition> candidates, DesignIntent intent, Random rng)
    {
        if (intent.PreferredBlueprints.Count == 0)
            return candidates[rng.Next(candidates.Count)];

        var weighted = candidates
            .SelectMany(b => Enumerable.Repeat(b, intent.PreferredBlueprints.Contains(b.Id) ? 3 : 1))
            .ToList();
        return weighted[rng.Next(weighted.Count)];
    }

    private static (List<BlueprintDefinition> North, List<BlueprintDefinition> South) SplitBalanced(
        List<BlueprintDefinition> rooms, Random rng)
    {
        var shuffled = rooms.OrderBy(_ => rng.Next()).ToList();
        List<BlueprintDefinition> north = [], south = [];
        var northW = 0;
        var southW = 0;
        foreach (var room in shuffled.OrderByDescending(b => b.Width))
        {
            if (northW <= southW)
            {
                north.Add(room);
                northW += room.Width - 1;
            }
            else
            {
                south.Add(room);
                southW += room.Width - 1;
            }
        }

        return (north, south);
    }

    private static int RowWidth(List<BlueprintDefinition> row) =>
        row.Sum(b => b.Width - 1) + 1;

    // ---------- canvas & stamping ----------

    private static char[][] NewCanvas(int width, int height)
    {
        var canvas = new char[height][];
        for (var y = 0; y < height; y++)
        {
            canvas[y] = new char[width];
            for (var x = 0; x < width; x++)
            {
                // Ring index from the map edge: grass(0-1), fence(2), dirt strip(3-4), yard inside.
                var ring = Math.Min(Math.Min(x, y), Math.Min(width - 1 - x, height - 1 - y));
                canvas[y][x] = ring switch
                {
                    < 2 => ',',
                    2 => 'f',
                    < 5 => 'd',
                    _ => ',',
                };
            }
        }

        return canvas;
    }

    private static void Stamp(char[][] canvas, Placement placement)
    {
        var rows = placement.Blueprint.Rows;
        for (var ry = 0; ry < rows.Count; ry++)
        {
            var sourceRow = rows[placement.Mirrored ? rows.Count - 1 - ry : ry];
            for (var rx = 0; rx < sourceRow.Length; rx++)
            {
                var glyph = sourceRow[rx];
                var x = placement.X + rx;
                var y = placement.Y + ry;
                if (glyph == 'D')
                {
                    // v1: interior doorways are open gaps — Staff AI can't operate doors yet,
                    // so a real door entity here would strand guards inside their rooms.
                    // Actual Door entities are reserved for the perimeter gate.
                    canvas[y][x] = '.';
                }
                else if (glyph != ' ')
                {
                    canvas[y][x] = glyph;
                }
            }
        }
    }

    // ---------- final map composition (guards, zones, items, lights, spawn) ----------

    private MapDefinition Compose(DesignIntent intent, Random rng, char[][] canvas,
        int width, int height, List<Placement> placements, List<MapDefinition.MapDoor> doors)
    {
        var mapRooms = placements.Select(p => new MapDefinition.MapRoom
        {
            Id = p.Blueprint.Id,
            Type = p.Blueprint.Type,
            X0 = p.X,
            Y0 = p.Y,
            X1 = p.X + p.Blueprint.Width - 1,
            Y1 = p.Y + p.Blueprint.Height - 1,
        }).ToList();

        // Patrol waypoints spread along the corridor spine.
        var corrY = placements.Where(p => !p.Mirrored).Max(p => p.Y + p.Blueprint.Height);
        var compoundW = width - 2 * PerimeterOffset;
        var waypoints = Enumerable.Range(0, 4)
            .Select(i => new[] { PerimeterOffset + 2 + i * (compoundW - 5) / 3, corrY })
            .ToList();

        var guards = new List<MapDefinition.MapGuard>();
        foreach (var station in placements.Where(p => p.Blueprint.Type == "guard_station"))
        {
            guards.Add(new MapDefinition.MapGuard
            {
                X = station.X + station.Blueprint.Width / 2,
                Y = station.Y + station.Blueprint.Height / 2,
                Patrol = waypoints,
            });
        }

        for (var i = 0; i < intent.PatrolGuards; i++)
        {
            var start = waypoints[i % waypoints.Count];
            // Offset each guard's route so they don't march in lockstep.
            var rotated = waypoints.Skip(i % waypoints.Count).Concat(waypoints.Take(i % waypoints.Count)).ToList();
            guards.Add(new MapDefinition.MapGuard { X = start[0], Y = start[1], Patrol = rotated });
        }

        var items = new List<MapDefinition.MapItem>();
        foreach (var p in placements)
        {
            foreach (var item in p.Blueprint.Items)
            {
                var y = p.Mirrored ? p.Blueprint.Height - 1 - item.Y : item.Y;
                items.Add(new MapDefinition.MapItem { Id = item.Id, X = p.X + item.X, Y = p.Y + y });
            }
        }

        var zones = placements
            .Where(p => p.Blueprint.HasTag("restricted"))
            .Select((p, i) => new MapDefinition.MapZone
            {
                Id = $"{p.Blueprint.Type}_{i}",
                Kind = "restricted",
                X0 = p.X,
                Y0 = p.Y,
                X1 = p.X + p.Blueprint.Width - 1,
                Y1 = p.Y + p.Blueprint.Height - 1,
            })
            .ToList();

        var lights = mapRooms.Select(r => new MapDefinition.MapLight
        {
            X = (r.X0 + r.X1) / 2,
            Y = (r.Y0 + r.Y1) / 2,
            Radius = 6f,
            Intensity = 0.8f,
        }).ToList();

        var spawn = FindSpawn(canvas, mapRooms);

        return new MapDefinition
        {
            Id = $"generated_{intent.Seed}",
            DisplayName = $"Generated Prison (seed {intent.Seed}, {intent.Security} security)",
            Legend = new Dictionary<string, MapDefinition.LegendEntry>(Legend),
            Floors = [new MapDefinition.MapFloor { AmbientLight = 0.85f, Rows = canvas.Select(row => new string(row)).ToList() }],
            Guards = guards,
            Items = items,
            Doors = doors,
            Rooms = mapRooms,
            Zones = zones,
            Lights = lights,
            PlayerSpawn = spawn,
        };
    }

    private static MapDefinition.MapSpawn FindSpawn(char[][] canvas, List<MapDefinition.MapRoom> rooms)
    {
        var cellBlock = rooms.First(r => r.Type == "cell_block");
        for (var y = cellBlock.Y0 + 1; y < cellBlock.Y1; y++)
            for (var x = cellBlock.X0 + 1; x < cellBlock.X1; x++)
                if (canvas[y][x] == '.')
                    return new MapDefinition.MapSpawn { X = x, Y = y };

        throw new InvalidOperationException($"Cell block '{cellBlock.Id}' has no walkable interior tile");
    }
}

/// <summary>The best validated, scored candidate of one offline generation run (§9.3 shape).</summary>
public sealed record GenerationOutcome(
    MapDefinition Map, ValidationReport Validation, QualityReport Quality, int SeedUsed, int Attempts);
