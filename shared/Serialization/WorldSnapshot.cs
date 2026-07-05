using Prison.Shared.World;

namespace Prison.Shared.Serialization;

/// <summary>
/// The tile-layer snapshot shared by the save format (PLAN §7.12) and the network Welcome
/// message (PLAN §7.11): a tile *name table* plus per-floor layer data. Tile ids travel as
/// names so the stream survives tile-registry reordering between versions and hosts.
/// </summary>
public static class WorldSnapshot
{
    public static void Write(WorldGrid world, BinaryWriter w)
    {
        w.Write(world.Tiles.Count);
        for (ushort id = 0; id < world.Tiles.Count; id++)
            w.Write(world.Tiles.Get(id).Id);

        w.Write(world.FloorCount);
        for (var f = 0; f < world.FloorCount; f++)
        {
            var floor = world.Floor(f);
            w.Write(floor.Width);
            w.Write(floor.Height);
            w.Write(floor.AmbientLight);
            for (var y = 0; y < floor.Height; y++)
                for (var x = 0; x < floor.Width; x++)
                {
                    w.Write(floor.GetFloorTile(x, y));
                    w.Write(floor.GetWallTile(x, y));
                }
        }
    }

    /// <summary>
    /// Restores the layers into a world freshly built from the same map. Ambient light stays
    /// authoritative from the map; renamed/removed tiles fail loudly (name table lookup).
    /// </summary>
    public static void Apply(WorldGrid world, BinaryReader r)
    {
        var tableSize = r.ReadInt32();
        var tileIdOf = new ushort[tableSize];
        for (var i = 0; i < tableSize; i++)
            tileIdOf[i] = world.Tiles.IdOf(r.ReadString());

        var floorCount = r.ReadInt32();
        if (floorCount != world.FloorCount)
            throw new InvalidDataException("Snapshot floor count does not match the map");
        for (var f = 0; f < floorCount; f++)
        {
            var width = r.ReadInt32();
            var height = r.ReadInt32();
            _ = r.ReadSingle(); // ambient light: authoritative from the map
            var floor = world.Floor(f);
            if (width != floor.Width || height != floor.Height)
                throw new InvalidDataException($"Snapshot floor {f} is {width}x{height}, map has {floor.Width}x{floor.Height}");
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    floor.SetFloorTile(x, y, tileIdOf[r.ReadUInt16()]);
                    floor.SetWallTile(x, y, tileIdOf[r.ReadUInt16()]);
                }
        }
    }
}
