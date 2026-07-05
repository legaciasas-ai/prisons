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

    /// <summary>
    /// The heritable security identity (§8.1). Every field is a one-way ratchet knob: the
    /// Evolution AI (Phase 10) only ever tightens these in response to observed escapes —
    /// never the perception physics (Pillar #2), and never back down (Pillar #9).
    /// </summary>
    public sealed record WardenDoctrine
    {
        public string SecurityLevel { get; init; } = "medium";

        /// <summary>Patrol guards hired beyond what the security level provides.</summary>
        public int ExtraPatrolGuards { get; init; }

        public int ExtraGuardStations { get; init; }

        /// <summary>Perimeter fence rings (1–3). Mutated when fence-cutting escapes recur.</summary>
        public int FenceLayers { get; init; } = 1;

        /// <summary>0–1: share of diggable ground poured over with concrete (anti-tunnel).</summary>
        public float HardenedGroundBias { get; init; }

        /// <summary>A dedicated patrol route inside the innermost fence.</summary>
        public bool PerimeterPatrol { get; init; }

        /// <summary>Uniforms no longer lie around the facility (anti-disguise).</summary>
        public bool RestrictedUniformAccess { get; init; }
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
        ExtraPatrolGuards = Doctrine.ExtraPatrolGuards,
        ExtraGuardStations = Doctrine.ExtraGuardStations,
        FenceLayers = Doctrine.FenceLayers,
        HardenedGroundBias = Doctrine.HardenedGroundBias,
        PerimeterPatrol = Doctrine.PerimeterPatrol,
        RestrictedUniformAccess = Doctrine.RestrictedUniformAccess,
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
