using Arch.Core;
using Prison.Shared.ECS;
using Prison.Shared.ECS.Components;
using Prison.Shared.Pathfinding;
using Prison.Shared.World;

namespace Prison.Shared.AI.Actions;

/// <summary>
/// Moves entities along their resolved paths (tile-center to tile-center), turning their
/// facing with the direction of travel and hopping floors at stair steps.
/// </summary>
public sealed class NavAgentSystem : ISimulationSystem
{
    private const float ArriveEpsilon = 0.05f;

    private static readonly QueryDescription Agents =
        new QueryDescription().WithAll<Position, Facing, NavAgent>();

    public string Name => "NavAgent";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        var dt = time.DeltaSeconds;

        ecsWorld.Query(in Agents, (ref Position position, ref Facing facing, ref NavAgent nav) =>
        {
            // Adopt a completed pathfinding request.
            if (nav.Pending is { } pending && pending.Status != PathRequestStatus.Pending)
            {
                nav.Path = pending.Status == PathRequestStatus.Completed ? pending.Path : null;
                nav.NextIndex = 0;
                nav.Pending = null;
                if (nav.Path is null)
                    nav.Destination = null; // unreachable — give up; the AI will re-decide
            }

            if (nav.Path is null)
                return;

            var remaining = nav.SpeedTilesPerSecond * dt;
            while (remaining > 0f && nav.NextIndex < nav.Path.Count)
            {
                var target = nav.Path[nav.NextIndex];

                // Stair hop: the path changes floor between vertically-connected nodes.
                if (target.Floor != position.Floor)
                {
                    position = new Position(target.X + 0.5f, target.Y + 0.5f, target.Floor);
                    nav.NextIndex++;
                    continue;
                }

                var targetX = target.X + 0.5f;
                var targetY = target.Y + 0.5f;
                var dx = targetX - position.X;
                var dy = targetY - position.Y;
                var distance = MathF.Sqrt(dx * dx + dy * dy);

                if (distance <= ArriveEpsilon)
                {
                    nav.NextIndex++;
                    continue;
                }

                facing.Radians = MathF.Atan2(dy, dx);
                var step = MathF.Min(distance, remaining);
                position.X += dx / distance * step;
                position.Y += dy / distance * step;
                remaining -= step;

                if (step >= distance)
                    nav.NextIndex++;
            }

            if (nav.NextIndex >= nav.Path.Count)
            {
                nav.Path = null;
                nav.NextIndex = 0;
                nav.Destination = null;
            }
        });
    }
}
