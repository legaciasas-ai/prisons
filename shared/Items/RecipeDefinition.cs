using System.Text.Json;
using Prison.Shared.World;

namespace Prison.Shared.Items;

/// <summary>
/// A crafting recipe (<c>content/recipes/*.json</c>): consume the ingredients, gain the
/// output after <see cref="CraftSeconds"/> of uninterrupted work (PLAN §7.8).
/// </summary>
public sealed record RecipeDefinition
{
    public required string Id { get; init; }

    public required string Output { get; init; }

    public required IReadOnlyList<string> Ingredients { get; init; }

    public float CraftSeconds { get; init; }

    public static IReadOnlyList<RecipeDefinition> LoadFromDirectory(string directory)
    {
        var recipes = new List<RecipeDefinition>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            recipes.Add(JsonSerializer.Deserialize<RecipeDefinition>(
                File.ReadAllText(file), TileDefinition.JsonOptions)
                ?? throw new InvalidDataException($"Empty recipe definition: {file}"));
        }

        return recipes;
    }
}
