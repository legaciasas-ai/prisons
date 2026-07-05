using Prison.Shared.Generation;
using Prison.Shared.World;

namespace Prison.Shared.Evolution;

/// <summary>
/// One complete evolution cycle, ready for review: the proposal (what mutates and why) and
/// the best next-generation candidate. Nothing here deploys anything — the caller (the
/// Phase 12 Evolution Service, an admin tool, a test) decides what goes live, which *is*
/// the optional human approval gate of §9.3.
/// </summary>
public sealed record EvolutionResult(
    EvolutionProposal Proposal,
    FamilyGenerationResult NextGeneration);

/// <summary>
/// The offline evolution cycle (PLAN §9): escape reports → Escape Analyzer → Rule Engine →
/// mutated family → generation pipeline (multi-candidate generate/validate/score, §9.3).
/// Lives in the shared library so the backend service, the CLI tool and the test suite all
/// run the exact same cycle; it must never be referenced by live-match systems (Pillar #1).
/// </summary>
public sealed class EvolutionPipeline(FamilyPipeline familyPipeline)
{
    /// <summary>
    /// Runs one full cycle. Returns null when generation fails every attempt — the family
    /// is left untouched in that case (a failed candidate never burns the escape data).
    /// </summary>
    public EvolutionResult? Evolve(
        FamilyDefinition family,
        IReadOnlyList<EscapeReport> reports,
        TileRegistry tiles,
        int maxAttempts = 10)
    {
        var analysis = EscapeAnalyzer.Analyze(family.PrisonId, reports);
        var proposal = EvolutionEngine.Propose(family, analysis);

        var next = familyPipeline.GenerateGeneration(proposal.Mutated, tiles, maxAttempts);
        return next is null ? null : new EvolutionResult(proposal, next);
    }
}
