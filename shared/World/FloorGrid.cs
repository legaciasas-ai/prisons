namespace Prison.Shared.World;

/// <summary>
/// One floor of the world, as a stack of independently-updatable layers (PLAN §7.2):
/// floor tiles, wall tiles, and baked light. Further layers (furniture, power, water,
/// heat, sound, ownership) are added in later phases.
/// Storage is a flat array per layer; chunked storage arrives with simulation bubbles (§7.10).
/// </summary>
public sealed class FloorGrid
{
    private readonly ushort[] _floorTiles;
    private readonly ushort[] _wallTiles;
    private readonly float[] _light;

    public FloorGrid(int width, int height, float ambientLight = 1f)
    {
        Width = width;
        Height = height;
        AmbientLight = ambientLight;
        _floorTiles = new ushort[width * height];
        _wallTiles = new ushort[width * height];
        _light = new float[width * height];
        Array.Fill(_light, ambientLight);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Base light level applied everywhere on this floor, 0..1 (PLAN §7.4 lighting layer).</summary>
    public float AmbientLight { get; }

    public bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

    public ushort GetFloorTile(int x, int y) => _floorTiles[Index(x, y)];

    public void SetFloorTile(int x, int y, ushort id) => _floorTiles[Index(x, y)] = id;

    public ushort GetWallTile(int x, int y) => _wallTiles[Index(x, y)];

    public void SetWallTile(int x, int y, ushort id) => _wallTiles[Index(x, y)] = id;

    public float GetLight(int x, int y) => _light[Index(x, y)];

    public void SetLight(int x, int y, float value) => _light[Index(x, y)] = Math.Clamp(value, 0f, 1f);

    private int Index(int x, int y)
    {
        if (!InBounds(x, y))
            throw new ArgumentOutOfRangeException($"({x},{y}) outside {Width}x{Height} floor");
        return y * Width + x;
    }
}
