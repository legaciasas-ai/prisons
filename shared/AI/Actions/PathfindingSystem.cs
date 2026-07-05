using Prison.Shared.ECS;
using Prison.Shared.Pathfinding;

namespace Prison.Shared.AI.Actions;

/// <summary>
/// Drains the shared pathfinding queue with a per-tick budget (PLAN §7.3/§7.10): hundreds
/// of agents share one worker pool instead of each pathfinding independently. The budget is
/// read live from the simulation budget, so the auto-tuner throttles it under load.
/// </summary>
public sealed class PathfindingSystem(PathfindingService pathfinding, AI.Scheduling.SimulationBudget budget) : ISimulationSystem
{
    public string Name => "Pathfinding";

    public void Update(Arch.Core.World ecsWorld, in SimTime time) =>
        pathfinding.Process(budget.PathfindingBudgetPerTick);
}
