using Arch.Core;
using Prison.Shared.ECS;
using Prison.Shared.ECS.Components;
using Prison.Shared.World;

namespace Prison.Shared.AI.Scheduling;

/// <summary>
/// Assigns each staff NPC its simulation LOD (PLAN §7.10) from *relevance*: distance to the
/// nearest prisoner (the simulation-bubble center) and current engagement. Engagement always
/// wins — a guard chasing someone is Full detail no matter where it happens, so LOD can
/// never change an outcome the player is part of, only the update rate of what nobody sees.
/// </summary>
public sealed class LodSystem(SimulationBudget budget) : ISimulationSystem
{
    /// <summary>Re-evaluation cadence (0.5s at 20 ticks/s) — LOD promotion latency ceiling.</summary>
    public const uint EvaluationIntervalTicks = 10;

    private static readonly QueryDescription Staff =
        new QueryDescription().WithAll<GuardTag, Position, AiState, SimulationDetail>();

    private static readonly QueryDescription Prisoners =
        new QueryDescription().WithAll<PrisonerTag, Position>();

    private readonly List<Position> _prisoners = [];

    public string Name => "Lod";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        var tick = time.Tick;

        _prisoners.Clear();
        ecsWorld.Query(in Prisoners, (ref Position position) => _prisoners.Add(position));

        ecsWorld.Query(in Staff, (Entity entity, ref Position position, ref AiState state,
            ref SimulationDetail detail) =>
        {
            if (tick < detail.NextEvaluationTick)
                return;
            // Staggered by entity id so hundreds of guards don't all re-evaluate on one tick.
            detail.NextEvaluationTick = tick + EvaluationIntervalTicks
                + (uint)(entity.Id % EvaluationIntervalTicks);

            detail.Lod = Assign(position, state.Action);
        });
    }

    private SimulationLod Assign(Position guard, GuardAction action)
    {
        // Engagement overrides distance (§7.10 priority: chasing 90, investigating 70).
        if (action is GuardAction.Chase or GuardAction.Arrest)
            return SimulationLod.Full;

        var sameFloor = float.MaxValue;
        var anyFloor = float.MaxValue;
        foreach (var prisoner in _prisoners)
        {
            var dx = prisoner.X - guard.X;
            var dy = prisoner.Y - guard.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            anyFloor = MathF.Min(anyFloor, distance);
            if (prisoner.Floor == guard.Floor)
                sameFloor = MathF.Min(sameFloor, distance);
        }

        var byDistance =
            sameFloor <= budget.FullDetailRadiusTiles ? SimulationLod.Full
            : sameFloor <= budget.ReducedDetailRadiusTiles ? SimulationLod.Reduced
            : sameFloor < float.MaxValue ? SimulationLod.Coarse
            : anyFloor < float.MaxValue ? SimulationLod.EventOnly
            : SimulationLod.Statistical;

        // An investigating guard stays responsive even if the noise source moved away.
        if (action == GuardAction.Investigate && byDistance > SimulationLod.Reduced)
            return SimulationLod.Reduced;

        return byDistance;
    }
}
