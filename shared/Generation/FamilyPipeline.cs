using Prison.Shared.World;

namespace Prison.Shared.Generation;

/// <summary>One generated generation of a family: the map, its lineage, and the advanced family record.</summary>
public sealed record FamilyGenerationResult(
    string PrisonId,
    string? ParentPrisonId,
    GenerationOutcome Outcome,
    FamilyDefinition NextFamily);

/// <summary>
/// The full per-family generation pipeline (PLAN §8.3): functional generation (steps 1–5, 10)
/// through <see cref="PrisonGenerator"/>, then the family's consistent visual identity —
/// style kit, exterior, decoration (steps 7–9) — then structural re-validation as a safety
/// net. A family keeps its kit and decoration across generations so players recognize
/// "this is definitely another Blackstone prison" even after major layout changes (§8.2).
/// </summary>
public sealed class FamilyPipeline(
    IReadOnlyList<BlueprintDefinition> blueprints,
    IReadOnlyList<StyleKitDefinition> styleKits,
    IReadOnlyList<DecorationRuleSet> decorationRules)
{
    public FamilyGenerationResult? GenerateGeneration(
        FamilyDefinition family, TileRegistry tiles, int maxAttempts = 8)
    {
        var kit = styleKits.FirstOrDefault(k => k.Id == family.Dna.StyleKit)
            ?? throw new InvalidOperationException($"Unknown style kit '{family.Dna.StyleKit}'");
        var rules = decorationRules.FirstOrDefault(r => r.Id == family.Dna.DecorationRuleSet)
            ?? throw new InvalidOperationException($"Unknown decoration rule set '{family.Dna.DecorationRuleSet}'");

        var seed = family.GenerationSeed();
        var intent = family.ToDesignIntent(seed);
        var outcome = new PrisonGenerator(blueprints).GenerateBest(intent, tiles, maxAttempts);
        if (outcome is null)
            return null;

        var map = outcome.Map with
        {
            Id = family.PrisonId,
            DisplayName = $"{family.DisplayName} — Generation {family.CurrentGeneration}",
        };
        map = StylePasses.ApplyStyleKit(map, kit);
        map = StylePasses.ApplyExterior(map, seed);
        map = StylePasses.ApplyDecoration(map, rules, seed);

        // Cosmetic passes are visual-only by contract; re-validate to enforce it (§8.4.A).
        var validation = PrisonValidator.Validate(map, tiles);
        if (!validation.Passed)
            return null;

        var next = family with
        {
            CurrentGeneration = family.CurrentGeneration + 1,
            ParentPrisonId = family.PrisonId,
        };

        return new FamilyGenerationResult(
            family.PrisonId,
            family.ParentPrisonId,
            outcome with { Map = map, Validation = validation },
            next);
    }
}
