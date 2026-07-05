using Prison.Shared.Utilities;
using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

/// <summary>Loads the real content/ data once for all world-dependent tests.</summary>
public sealed class TestContent
{
    public TestContent()
    {
        var root = ContentPaths.Resolve();
        Tiles = TileRegistry.LoadFromDirectory(Path.Combine(root, "tiles"));
        Map = MapDefinition.Load(Path.Combine(root, "maps", "test_prison.json"));
    }

    public TileRegistry Tiles { get; }

    public MapDefinition Map { get; }

    /// <summary>Builds a fresh world instance (tests may mutate it).</summary>
    public WorldGrid BuildWorld() => Map.BuildWorld(Tiles);
}

[CollectionDefinition("content")]
public sealed class ContentCollection : ICollectionFixture<TestContent>;
