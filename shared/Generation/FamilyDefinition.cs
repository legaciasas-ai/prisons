using System.Text.Json;
using System.Text.Json.Serialization;
using Prison.Shared.World;

namespace Prison.Shared.Generation;

/// <summary>
/// A Prison Family (PLAN §8.1, §17.5): the persistent identity of a prison across
/// generations. Architectural DNA changes slowly (visual/structural), Warden Doctrine
/// mutates in response to escapes (Evolution AI, Phase 10). A family never resets — it evolves.
/// </summary>
public sealed record FamilyDefinition
{
    public required string Id { get; init; }

    public string DisplayName { get; init; } = "";

    [JsonPropertyName("architectural_dna")]
    public required ArchitecturalDna Dna { get; init; }

    [JsonPropertyName("warden_doctrine")]
    public required WardenDoctrine Doctrine { get; init; }

    public int CurrentGeneration { get; init; } = 1;

    public string? ParentPrisonId { get; init; }

    [JsonIgnore]
    public string PrisonId => $"{Id}-gen{CurrentGeneration}";

    public sealed record ArchitecturalDna
    {
        public required string StyleKit { get; init; }
        public required string DecorationRuleSet { get; init; }

        /// <summary>small | medium | large — resolved to a concrete inmate count.</summary>
        public string Capacity { get; init; } = "medium";

        public List<string> PreferredBlueprints { get; init; } = [];
    }

    public sealed record WardenDoctrine
    {
        public string SecurityLevel { get; init; } = "medium";
    }

    /// <summary>Step 1 of the pipeline (§8.3): resolve heritable identity into a concrete intent.</summary>
    public DesignIntent ToDesignIntent(int seed) => new()
    {
        Seed = seed,
        Capacity = Dna.Capacity.ToLowerInvariant() switch
        {
            "small" => 16,
            "large" => 48,
            _ => 30,
        },
        Security = Enum.Parse<SecurityLevel>(Doctrine.SecurityLevel, ignoreCase: true),
        PreferredBlueprints = Dna.PreferredBlueprints,
    };

    /// <summary>Deterministic per (family, generation): the same family history regenerates identically.</summary>
    public int GenerationSeed()
    {
        var hash = 17;
        foreach (var c in Id)
            hash = unchecked(hash * 31 + c);
        return unchecked(hash + CurrentGeneration * 7919);
    }

    public static FamilyDefinition Load(string path) =>
        JsonSerializer.Deserialize<FamilyDefinition>(File.ReadAllText(path), TileDefinition.JsonOptions)
            ?? throw new InvalidDataException($"Empty family definition: {path}");

    public static IReadOnlyList<FamilyDefinition> LoadFromDirectory(string directory) =>
        Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal)
            .Select(Load)
            .ToList();
}
