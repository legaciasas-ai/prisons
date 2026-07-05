using Prison.Shared.World;

namespace Prison.Shared.Pathfinding;

public enum PathRequestStatus
{
    Pending,
    Completed,
    Failed,
}

/// <summary>Handle returned to a requester; polled (or observed via callback) when resolved.</summary>
public sealed class PathRequest
{
    internal PathRequest(TilePos start, TilePos goal, int priority)
    {
        Start = start;
        Goal = goal;
        Priority = priority;
    }

    public TilePos Start { get; }
    public TilePos Goal { get; }

    /// <summary>Higher = served first (PLAN §7.10 priority table: player-nearby 100 … sleeping 10).</summary>
    public int Priority { get; }

    public PathRequestStatus Status { get; internal set; } = PathRequestStatus.Pending;
    public List<TilePos>? Path { get; internal set; }
}

/// <summary>
/// Pathfinding requests are shared and queued — hundreds of entities must not each trigger an
/// independent expensive pathfind in the same tick (PLAN §7.3). Consumers enqueue; the host
/// calls <see cref="Process"/> once per tick with a request budget.
/// </summary>
public sealed class PathfindingService(IPathfinder pathfinder)
{
    private readonly PriorityQueue<PathRequest, int> _queue = new();

    public int PendingCount => _queue.Count;

    public PathRequest Request(TilePos start, TilePos goal, int priority = 50)
    {
        var request = new PathRequest(start, goal, priority);
        _queue.Enqueue(request, -priority); // PriorityQueue is min-first; invert for highest-first
        return request;
    }

    /// <summary>Resolves up to <paramref name="budget"/> queued requests. Returns how many were served.</summary>
    public int Process(int budget)
    {
        var served = 0;
        while (served < budget && _queue.TryDequeue(out var request, out _))
        {
            request.Path = pathfinder.FindPath(request.Start, request.Goal);
            request.Status = request.Path is null ? PathRequestStatus.Failed : PathRequestStatus.Completed;
            served++;
        }

        return served;
    }
}
