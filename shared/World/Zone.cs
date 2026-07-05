namespace Prison.Shared.World;

public enum ZoneKind
{
    /// <summary>Prisoners are not allowed here; being *seen* here raises suspicion (PLAN §7.7).</summary>
    Restricted,
}

/// <summary>An axis-aligned named region of one floor.</summary>
public sealed record Zone(string Id, ZoneKind Kind, int Floor, int X0, int Y0, int X1, int Y1)
{
    public bool Contains(TilePos pos) =>
        pos.Floor == Floor && pos.X >= X0 && pos.X <= X1 && pos.Y >= Y0 && pos.Y <= Y1;
}
