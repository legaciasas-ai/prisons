namespace Prison.Shared.World;

/// <summary>A static point light baked into a floor's light layer (PLAN §7.4).</summary>
public readonly record struct PointLight(TilePos Position, float Radius, float Intensity);

/// <summary>
/// The whole prison world: a stack of floors connected by designated stair nodes (PLAN §7.2–7.3),
/// plus the tile registry the layers index into. This is pure simulation state — no rendering.
/// </summary>
public sealed class WorldGrid
{
    private readonly FloorGrid[] _floors;
    private readonly List<StairConnection> _stairs = [];
    private readonly List<Zone> _zones = [];

    public WorldGrid(TileRegistry tiles, IReadOnlyList<FloorGrid> floors)
    {
        if (floors.Count == 0)
            throw new ArgumentException("A world needs at least one floor");
        Tiles = tiles;
        _floors = [.. floors];
    }

    public TileRegistry Tiles { get; }

    public int FloorCount => _floors.Length;

    public IReadOnlyList<StairConnection> Stairs => _stairs;

    public FloorGrid Floor(int index) => _floors[index];

    public bool InBounds(TilePos pos) =>
        pos.Floor >= 0 && pos.Floor < _floors.Length && _floors[pos.Floor].InBounds(pos.X, pos.Y);

    public void AddStairs(StairConnection connection)
    {
        if (!InBounds(connection.A) || !InBounds(connection.B))
            throw new ArgumentException($"Stair connection out of bounds: {connection.A} <-> {connection.B}");
        _stairs.Add(connection);
    }

    public IReadOnlyList<Zone> Zones => _zones;

    public void AddZone(Zone zone) => _zones.Add(zone);

    /// <summary>Is this tile inside any restricted zone (PLAN §7.7 observable signal)?</summary>
    public bool IsRestricted(TilePos pos)
    {
        foreach (var zone in _zones)
            if (zone.Kind == ZoneKind.Restricted && zone.Contains(pos))
                return true;
        return false;
    }

    public StairConnection? StairAt(TilePos pos)
    {
        foreach (var stairs in _stairs)
            if (stairs.Touches(pos))
                return stairs;
        return null;
    }

    /// <summary>A tile can be stood on iff its floor tile is walkable and no blocking wall occupies it.</summary>
    public bool IsWalkable(TilePos pos)
    {
        if (!InBounds(pos))
            return false;
        var grid = _floors[pos.Floor];
        var floorTile = Tiles.Get(grid.GetFloorTile(pos.X, pos.Y));
        if (!floorTile.Walkable)
            return false;
        var wall = grid.GetWallTile(pos.X, pos.Y);
        return wall == TileRegistry.EmptyId || Tiles.Get(wall).Walkable;
    }

    /// <summary>Pathfinding cost of entering this tile (>= its floor tile's movement cost).</summary>
    public float MovementCost(TilePos pos)
    {
        var grid = _floors[pos.Floor];
        return Tiles.Get(grid.GetFloorTile(pos.X, pos.Y)).MovementCost;
    }

    /// <summary>
    /// How much line of sight passes through this tile, 0..1. Walls dominate; a tile with no
    /// floor at all (void) is transparent — there is just nothing to see.
    /// </summary>
    public float Transparency(TilePos pos)
    {
        if (!InBounds(pos))
            return 0f;
        var grid = _floors[pos.Floor];
        var wall = grid.GetWallTile(pos.X, pos.Y);
        if (wall != TileRegistry.EmptyId)
            return Tiles.Get(wall).VisibilityTransparency;
        return Tiles.Get(grid.GetFloorTile(pos.X, pos.Y)).VisibilityTransparency;
    }

    public bool BlocksSight(TilePos pos) => Transparency(pos) < 0.5f;

    public float LightAt(TilePos pos) =>
        InBounds(pos) ? _floors[pos.Floor].GetLight(pos.X, pos.Y) : 0f;

    /// <summary>Bakes ambient light plus radial point lights into each floor's light layer.</summary>
    public void BakeLighting(IEnumerable<PointLight> lights)
    {
        foreach (var floor in _floors)
            for (var y = 0; y < floor.Height; y++)
                for (var x = 0; x < floor.Width; x++)
                    floor.SetLight(x, y, floor.AmbientLight);

        foreach (var light in lights)
        {
            var floor = _floors[light.Position.Floor];
            var r = (int)MathF.Ceiling(light.Radius);
            for (var y = light.Position.Y - r; y <= light.Position.Y + r; y++)
            {
                for (var x = light.Position.X - r; x <= light.Position.X + r; x++)
                {
                    if (!floor.InBounds(x, y))
                        continue;
                    var distance = TilePos.EuclideanDistance(new TilePos(x, y, light.Position.Floor), light.Position);
                    if (distance > light.Radius)
                        continue;
                    var contribution = light.Intensity * (1f - distance / light.Radius);
                    floor.SetLight(x, y, floor.GetLight(x, y) + contribution);
                }
            }
        }
    }
}
