using Arch.Core;
using Prison.Shared.ECS.Components;
using Prison.Shared.World;

namespace Prison.Shared.ECS.Systems;

/// <summary>
/// Lets an entity standing on a stair node travel to the connected floor (PLAN §7.3).
/// The one-tick <see cref="PlayerInput.UseStairs"/> intent is consumed on use.
/// </summary>
public sealed class StairTraversalSystem(WorldGrid world) : ISimulationSystem
{
    private static readonly QueryDescription Players =
        new QueryDescription().WithAll<Position, PlayerInput>();

    public string Name => "StairTraversal";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        ecsWorld.Query(in Players, (ref Position position, ref PlayerInput input) =>
        {
            if (!input.UseStairs)
                return;
            input.UseStairs = false;

            var tile = new TilePos((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), position.Floor);
            if (world.StairAt(tile) is not { } stairs)
                return;

            var destination = stairs.OtherEnd(tile);
            position = new Position(destination.X + 0.5f, destination.Y + 0.5f, destination.Floor);
        });
    }
}
