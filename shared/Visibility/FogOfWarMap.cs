using Prison.Shared.World;

namespace Prison.Shared.Visibility;

/// <summary>Player-facing knowledge state of a tile (PLAN §7.4, memory visibility).</summary>
public enum FogState : byte
{
    /// <summary>Never observed — fully hidden (black).</summary>
    Unseen = 0,

    /// <summary>Previously observed, no longer visible — grayed out, last-known state.</summary>
    Remembered = 1,

    /// <summary>Actively observed right now — full detail.</summary>
    Visible = 2,
}

/// <summary>
/// Three-state fog of war memory for one observer, per floor. Purely presentational memory —
/// the perception system (Phase 2) uses its own belief store, not this.
/// </summary>
public sealed class FogOfWarMap
{
    private readonly FogState[][] _floors;
    private readonly int _width;
    private readonly int _height;
    private readonly List<TilePos> _currentlyVisible = [];

    public FogOfWarMap(WorldGrid world)
    {
        _floors = new FogState[world.FloorCount][];
        _width = world.Floor(0).Width;
        _height = world.Floor(0).Height;
        for (var i = 0; i < world.FloorCount; i++)
            _floors[i] = new FogState[_width * _height];
    }

    public FogState StateAt(TilePos pos) => _floors[pos.Floor][pos.Y * _width + pos.X];

    /// <summary>Applies a freshly computed visible set: new tiles become Visible, previously
    /// visible ones decay to Remembered, everything else is untouched.</summary>
    public void Update(IReadOnlyCollection<TilePos> visibleTiles)
    {
        foreach (var pos in _currentlyVisible)
            _floors[pos.Floor][pos.Y * _width + pos.X] = FogState.Remembered;
        _currentlyVisible.Clear();

        foreach (var pos in visibleTiles)
        {
            _floors[pos.Floor][pos.Y * _width + pos.X] = FogState.Visible;
            _currentlyVisible.Add(pos);
        }
    }
}
