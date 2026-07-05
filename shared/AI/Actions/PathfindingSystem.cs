using Prison.Shared.ECS;
using Prison.Shared.Pathfinding;

namespace Prison.Shared.AI.Actions;

/// <summary>
/// Drains the shared pathfinding queue with a fixed per-tick budget (PLAN §7.3/§7.10):
/// hundreds of agents share one worker pool instead of each pathfinding independently.
/// The budget becomes dynamic with the Phase 9 AI budget manager.
/// </summary>
public sealed class PathfindingSystem(PathfindingService pathfinding, int budgetPerTick = 4) : ISimulationSystem
{
    public string Name => "Pathfinding";

    public void Update(Arch.Core.World ecsWorld, in SimTime time) => pathfinding.Process(budgetPerTick);
}
