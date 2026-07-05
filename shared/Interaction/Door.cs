using Prison.Shared.World;

namespace Prison.Shared.Interaction;

/// <summary>
/// A door occupying one wall-layer tile. Its open/closed state is written into the world's
/// wall layer as ordinary data tiles ("door_closed"/"door_open"), so movement, vision and
/// pathfinding all react through the exact same tile properties as any wall — no special
/// door code path in those systems (Pillar #4).
/// </summary>
public sealed class Door
{
    public const string ClosedTileId = "door_closed";
    public const string OpenTileId = "door_open";

    public required TilePos Tile { get; init; }

    public bool Locked { get; set; }

    public bool Open { get; set; }

    /// <summary>Writes the door's current state into the world's wall layer.</summary>
    public void ApplyToWorld(WorldGrid world)
    {
        var tileId = world.Tiles.IdOf(Open ? OpenTileId : ClosedTileId);
        world.Floor(Tile.Floor).SetWallTile(Tile.X, Tile.Y, tileId);
    }
}
