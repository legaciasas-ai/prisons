using Prison.Shared.Generation;
using Prison.Shared.Items;
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
        Items = ItemRegistry.LoadFromDirectory(Path.Combine(root, "items"));
        Recipes = RecipeDefinition.LoadFromDirectory(Path.Combine(root, "recipes"));
        Blueprints = BlueprintDefinition.LoadFromDirectory(Path.Combine(root, "rooms"));
        Map = MapDefinition.Load(Path.Combine(root, "maps", "test_prison.json"));
    }

    public IReadOnlyList<BlueprintDefinition> Blueprints { get; }

    public TileRegistry Tiles { get; }

    public ItemRegistry Items { get; }

    public IReadOnlyList<RecipeDefinition> Recipes { get; }

    public MapDefinition Map { get; }

    /// <summary>Builds a fresh world instance (tests may mutate it).</summary>
    public WorldGrid BuildWorld() => Map.BuildWorld(Tiles);
}

[CollectionDefinition("content")]
public sealed class ContentCollection : ICollectionFixture<TestContent>;
