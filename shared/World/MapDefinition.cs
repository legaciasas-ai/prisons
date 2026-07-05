using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prison.Shared.World;

/// <summary>
/// A hand-authored map data file (<c>content/maps/*.json</c>). Used for test maps and,
/// later, as the serialized output format of the generation pipeline's layout stage.
/// </summary>
public sealed record MapDefinition
{
    public required string Id { get; init; }
    public string DisplayName { get; init; } = "";

    /// <summary>Single-character legend: map glyph → floor/wall tile ids.</summary>
    public required Dictionary<string, LegendEntry> Legend { get; init; }

    public required List<MapFloor> Floors { get; init; }

    public List<MapStairs> Stairs { get; init; } = [];

    public List<MapLight> Lights { get; init; } = [];

    public List<MapZone> Zones { get; init; } = [];

    public List<MapGuard> Guards { get; init; } = [];

    public List<MapItem> Items { get; init; } = [];

    public List<MapDoor> Doors { get; init; } = [];

    /// <summary>Room placements, filled by the generation pipeline (empty on hand-authored maps).</summary>
    public List<MapRoom> Rooms { get; init; } = [];

    public MapSpawn PlayerSpawn { get; init; } = new();

    public sealed record LegendEntry
    {
        public string? Floor { get; init; }
        public string? Wall { get; init; }
    }

    public sealed record MapFloor
    {
        public float AmbientLight { get; init; } = 1f;
        public required List<string> Rows { get; init; }
    }

    public sealed record MapStairs
    {
        // Explicit names: SnakeCaseLower would render XA as "xa", not "x_a".
        [JsonPropertyName("floor_a")] public int FloorA { get; init; }
        [JsonPropertyName("x_a")] public int XA { get; init; }
        [JsonPropertyName("y_a")] public int YA { get; init; }
        [JsonPropertyName("floor_b")] public int FloorB { get; init; }
        [JsonPropertyName("x_b")] public int XB { get; init; }
        [JsonPropertyName("y_b")] public int YB { get; init; }
    }

    public sealed record MapLight
    {
        public int Floor { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public float Radius { get; init; } = 5f;
        public float Intensity { get; init; } = 1f;
    }

    public sealed record MapZone
    {
        public required string Id { get; init; }
        public string Kind { get; init; } = "restricted";
        public int Floor { get; init; }
        public int X0 { get; init; }
        public int Y0 { get; init; }
        public int X1 { get; init; }
        public int Y1 { get; init; }
    }

    public sealed record MapGuard
    {
        public int Floor { get; init; }
        public int X { get; init; }
        public int Y { get; init; }

        /// <summary>Patrol waypoints as [x, y] pairs on the guard's floor.</summary>
        public List<int[]> Patrol { get; init; } = [];

        [JsonIgnore]
        public TilePos Position => new(X, Y, Floor);
    }

    public sealed record MapRoom
    {
        /// <summary>Blueprint id this room was stamped from.</summary>
        public required string Id { get; init; }
        public required string Type { get; init; }
        public int Floor { get; init; }
        public int X0 { get; init; }
        public int Y0 { get; init; }
        public int X1 { get; init; }
        public int Y1 { get; init; }

        public bool Contains(TilePos pos) =>
            pos.Floor == Floor && pos.X >= X0 && pos.X <= X1 && pos.Y >= Y0 && pos.Y <= Y1;
    }

    public sealed record MapItem
    {
        public required string Id { get; init; }
        public int Floor { get; init; }
        public int X { get; init; }
        public int Y { get; init; }

        [JsonIgnore]
        public TilePos Position => new(X, Y, Floor);
    }

    public sealed record MapDoor
    {
        public int Floor { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public bool Locked { get; init; }

        [JsonIgnore]
        public TilePos Position => new(X, Y, Floor);
    }

    public sealed record MapSpawn
    {
        public int Floor { get; init; }
        public int X { get; init; }
        public int Y { get; init; }

        [JsonIgnore]
        public TilePos Position => new(X, Y, Floor);
    }

    public static MapDefinition Load(string path) =>
        JsonSerializer.Deserialize<MapDefinition>(File.ReadAllText(path), TileDefinition.JsonOptions)
            ?? throw new InvalidDataException($"Empty map file: {path}");

    /// <summary>Builds the simulation world from this map data.</summary>
    public WorldGrid BuildWorld(TileRegistry tiles)
    {
        var width = Floors.SelectMany(f => f.Rows).Max(r => r.Length);
        var height = Floors.Max(f => f.Rows.Count);

        var grids = new List<FloorGrid>();
        foreach (var mapFloor in Floors)
        {
            var grid = new FloorGrid(width, height, mapFloor.AmbientLight);
            for (var y = 0; y < mapFloor.Rows.Count; y++)
            {
                var row = mapFloor.Rows[y];
                for (var x = 0; x < row.Length; x++)
                {
                    var glyph = row[x].ToString();
                    if (!Legend.TryGetValue(glyph, out var entry))
                        throw new InvalidDataException($"Map '{Id}': glyph '{glyph}' missing from legend");
                    if (entry.Floor is not null)
                        grid.SetFloorTile(x, y, tiles.IdOf(entry.Floor));
                    if (entry.Wall is not null)
                        grid.SetWallTile(x, y, tiles.IdOf(entry.Wall));
                }
            }

            grids.Add(grid);
        }

        var world = new WorldGrid(tiles, grids);
        foreach (var s in Stairs)
            world.AddStairs(new StairConnection(new TilePos(s.XA, s.YA, s.FloorA), new TilePos(s.XB, s.YB, s.FloorB)));

        foreach (var z in Zones)
        {
            var kind = z.Kind.Equals("restricted", StringComparison.OrdinalIgnoreCase)
                ? ZoneKind.Restricted
                : throw new InvalidDataException($"Map '{Id}': unknown zone kind '{z.Kind}'");
            world.AddZone(new Zone(z.Id, kind, z.Floor, z.X0, z.Y0, z.X1, z.Y1));
        }

        world.BakeLighting(Lights.Select(l =>
            new PointLight(new TilePos(l.X, l.Y, l.Floor), l.Radius, l.Intensity)));

        return world;
    }
}
