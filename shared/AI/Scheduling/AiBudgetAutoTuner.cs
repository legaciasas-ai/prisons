using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Prison.Shared.AI.Scheduling;

/// <summary>
/// The dynamic AI budget manager (PLAN §7.10/§12.3): the host reports how long simulation
/// ticks actually take, and the tuner degrades or restores the budget's dynamic knobs —
/// low-priority end first (far-away update rates, pathfinding throughput), never gameplay
/// rules. The host calls <see cref="Adjust"/> periodically (every second or two).
/// </summary>
public sealed class AiBudgetAutoTuner
{
    private readonly SimulationBudget _budget;
    private readonly double _tickIntervalSeconds;
    private readonly ILogger _log;

    public AiBudgetAutoTuner(SimulationBudget budget, int ticksPerSecond, ILogger? logger = null)
    {
        _budget = budget;
        _tickIntervalSeconds = 1.0 / ticksPerSecond;
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>Above this fraction of the tick interval spent simulating, degrade.</summary>
    public double HighWatermark { get; init; } = 0.75;

    /// <summary>Below this fraction, restore toward the profile baseline.</summary>
    public double LowWatermark { get; init; } = 0.35;

    public float MaxDegradationScale { get; init; } = 8f;

    /// <summary>Simulation cost as a fraction of the tick interval, from the last report.</summary>
    public double LastUtilization { get; private set; }

    /// <summary>True when any dynamic knob currently sits away from its profile baseline.</summary>
    public bool Degraded =>
        _budget.DegradationScale > 1f
        || _budget.PathfindingBudgetPerTick < _budget.BasePathfindingBudgetPerTick;

    public void Adjust(double averageTickSeconds)
    {
        LastUtilization = averageTickSeconds / _tickIntervalSeconds;

        if (LastUtilization > HighWatermark)
        {
            var before = (_budget.DegradationScale, _budget.PathfindingBudgetPerTick);
            _budget.DegradationScale = MathF.Min(_budget.DegradationScale * 1.5f, MaxDegradationScale);
            _budget.PathfindingBudgetPerTick = Math.Max(1, _budget.PathfindingBudgetPerTick - 1);
            if (before != (_budget.DegradationScale, _budget.PathfindingBudgetPerTick))
            {
                _log.LogWarning(
                    "AI budget degraded (simulation at {Utilization:P0} of tick): interval scale {Scale:F1}, pathfinding {Pathfinding}/tick",
                    LastUtilization, _budget.DegradationScale, _budget.PathfindingBudgetPerTick);
            }
        }
        else if (LastUtilization < LowWatermark && Degraded)
        {
            _budget.DegradationScale = MathF.Max(1f, _budget.DegradationScale * 0.75f);
            _budget.PathfindingBudgetPerTick = Math.Min(
                _budget.BasePathfindingBudgetPerTick, _budget.PathfindingBudgetPerTick + 1);
            if (!Degraded)
                _log.LogInformation("AI budget fully restored to profile '{Profile}'", _budget.ProfileName);
        }
    }
}
