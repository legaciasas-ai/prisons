namespace Prison.Shared.World;

/// <summary>An integer tile coordinate on a specific floor.</summary>
public readonly record struct TilePos(int X, int Y, int Floor)
{
    public override string ToString() => $"({X},{Y} F{Floor})";

    public static float ManhattanDistance(TilePos a, TilePos b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    public static float EuclideanDistance(TilePos a, TilePos b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
