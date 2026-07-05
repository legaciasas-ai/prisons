using Arch.Core;
using Prison.Shared.ECS;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.Items;
using Prison.Shared.World;

namespace Prison.Shared.Interaction;

/// <summary>
/// Executes escapist interactions (PLAN §7.8): pick up, craft, dig, cut fences, lockpick,
/// open/close doors, disguise, throw diversions. Everything physical goes through the world's
/// data tiles, and every outcome is announced on the event bus — the Staff AI reacts through
/// its ordinary senses (digging is *heard*, not magically known), telemetry records it all.
/// </summary>
public sealed class InteractionSystem(
    WorldGrid world, ItemRegistry items, IReadOnlyList<RecipeDefinition> recipes, EventBus events)
    : ISimulationSystem
{
    /// <summary>Arm's reach for physical interactions, in tiles (matches arrest range scale).</summary>
    public const float ReachTiles = 1.6f;

    public const float ThrowRangeTiles = 8f;

    /// <summary>Moving further than this from the work anchor cancels the work.</summary>
    public const float WorkAnchorTolerance = 0.6f;

    /// <summary>PLAN §7.5 hearing table: digging 15m, metal cutting 20m, thrown impact ~10m.</summary>
    public const float DigSoundRadius = 15f;
    public const float CutSoundRadius = 20f;
    public const float ImpactSoundRadius = 10f;

    /// <summary>Noisy work re-emits its sound this often while in progress.</summary>
    public const float WorkSoundIntervalSeconds = 1f;

    /// <summary>What a diggable floor tile becomes once dug through.</summary>
    public const string TunnelTileId = "tunnel";

    /// <summary>Wall tiles carrying this data tag can be cut with a cutting item.</summary>
    public const string CuttableTag = "cuttable";

    private static readonly QueryDescription Interactors =
        new QueryDescription().WithAll<Position, Interactor, Inventory>();

    private static readonly QueryDescription WorldItems =
        new QueryDescription().WithAll<WorldItem>();

    private static readonly QueryDescription Doors =
        new QueryDescription().WithAll<Door>();

    private readonly List<Entity> _pendingDestroy = [];

    public string Name => "Interaction";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        var tick = time.Tick;
        var dt = time.DeltaSeconds;

        ecsWorld.Query(in Interactors, (Entity actor, ref Position position, ref Interactor interactor, ref Inventory inventory) =>
        {
            if (interactor.Request is { } request)
            {
                interactor.Request = null;
                StartOrExecute(ecsWorld, actor, ref position, interactor, inventory, request, tick);
            }

            ProgressWork(ecsWorld, actor, ref position, interactor, inventory, tick, dt);
        });

        // Structural changes (item entity removal) deferred out of query iteration.
        foreach (var entity in _pendingDestroy)
            ecsWorld.Destroy(entity);
        _pendingDestroy.Clear();
    }

    private void StartOrExecute(Arch.Core.World ecsWorld, Entity actor, ref Position position,
        Interactor interactor, Inventory inventory, InteractionRequest request, ulong tick)
    {
        var actorTile = new TilePos((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), position.Floor);

        switch (request.Kind)
        {
            case InteractionKind.PickUp:
            {
                if (inventory.IsFull || !WithinReach(position, request.Target))
                    return;
                var found = FindWorldItem(ecsWorld, request.Target);
                if (found is not { } item)
                    return;
                inventory.Items.Add(item.ItemId);
                _pendingDestroy.Add(item.Entity);
                events.Publish(new ItemPickedUpEvent(tick, actor, item.ItemId, request.Target));
                break;
            }

            case InteractionKind.Craft:
            {
                var recipe = recipes.FirstOrDefault(r => r.Id == request.Id);
                if (recipe is null || !inventory.HasAll(recipe.Ingredients))
                    return;
                if (recipe.CraftSeconds <= 0f)
                {
                    CompleteCraft(actor, inventory, recipe, tick);
                    return;
                }
                interactor.Work = NewWork(request.Kind, request.Target, recipe.Id, recipe.CraftSeconds, position);
                break;
            }

            case InteractionKind.Dig:
            {
                if (!WithinReach(position, request.Target) || !world.InBounds(request.Target))
                    return;
                var grid = world.Floor(request.Target.Floor);
                var floorTile = world.Tiles.Get(grid.GetFloorTile(request.Target.X, request.Target.Y));
                if (!floorTile.CanDig || grid.GetWallTile(request.Target.X, request.Target.Y) != TileRegistry.EmptyId)
                    return;
                var tool = FirstTool(inventory, item => item.DigSeconds > 0f);
                if (tool is null)
                    return;
                interactor.Work = NewWork(request.Kind, request.Target, tool.Id, tool.DigSeconds, position);
                break;
            }

            case InteractionKind.CutFence:
            {
                if (!WithinReach(position, request.Target) || !world.InBounds(request.Target))
                    return;
                var grid = world.Floor(request.Target.Floor);
                var wall = grid.GetWallTile(request.Target.X, request.Target.Y);
                if (wall == TileRegistry.EmptyId || !world.Tiles.Get(wall).HasTag(CuttableTag))
                    return;
                var tool = FirstTool(inventory, item => item.CutSeconds > 0f);
                if (tool is null)
                    return;
                interactor.Work = NewWork(request.Kind, request.Target, tool.Id, tool.CutSeconds, position);
                break;
            }

            case InteractionKind.Lockpick:
            {
                if (!WithinReach(position, request.Target))
                    return;
                var door = FindDoor(ecsWorld, request.Target);
                if (door is null || !door.Locked)
                    return;
                var tool = FirstTool(inventory, item => item.LockpickSeconds > 0f);
                if (tool is null)
                    return;
                interactor.Work = NewWork(request.Kind, request.Target, tool.Id, tool.LockpickSeconds, position);
                break;
            }

            case InteractionKind.ToggleDoor:
            {
                if (!WithinReach(position, request.Target) || actorTile == request.Target)
                    return;
                var door = FindDoor(ecsWorld, request.Target);
                if (door is null || door.Locked)
                    return;
                door.Open = !door.Open;
                door.ApplyToWorld(world);
                events.Publish(new DoorToggledEvent(tick, actor, request.Target, door.Open));
                break;
            }

            case InteractionKind.SetDisguise:
            {
                if (!ecsWorld.Has<Appearance>(actor))
                    return;
                string? role = null;
                if (request.Id is { } itemId)
                {
                    if (!inventory.Has(itemId) || !items.TryGet(itemId, out var item) || item.DisguiseRole is null)
                        return;
                    role = item.DisguiseRole;
                }
                ecsWorld.Set(actor, new Appearance(role));
                events.Publish(new DisguiseChangedEvent(tick, actor, role));
                break;
            }

            case InteractionKind.Throw:
            {
                if (request.Id is not { } thrownId || !inventory.Has(thrownId))
                    return;
                if (request.Target.Floor != position.Floor
                    || TilePos.EuclideanDistance(actorTile, request.Target) > ThrowRangeTiles)
                    return;
                inventory.Remove(thrownId);
                events.Publish(new DiversionEvent(tick, actor, thrownId, request.Target));
                events.Publish(new SoundEmittedEvent(tick, request.Target, ImpactSoundRadius, SoundKind.Impact, actor));
                break;
            }
        }
    }

    private void ProgressWork(Arch.Core.World ecsWorld, Entity actor, ref Position position,
        Interactor interactor, Inventory inventory, ulong tick, float dt)
    {
        if (interactor.Work is not { } work)
            return;

        // Stepping away from the spot abandons the work (no progress is kept).
        var drift = MathF.Sqrt(
            (position.X - work.AnchorX) * (position.X - work.AnchorX) +
            (position.Y - work.AnchorY) * (position.Y - work.AnchorY));
        if (drift > WorkAnchorTolerance)
        {
            interactor.Work = null;
            return;
        }

        var previous = work.ElapsedSeconds;
        work.ElapsedSeconds += dt;

        // Noisy work is *heard*, periodically, through the ordinary sound pipeline (§7.5).
        var (soundKind, soundRadius) = work.Kind switch
        {
            InteractionKind.Dig => (SoundKind.Digging, DigSoundRadius),
            InteractionKind.CutFence => (SoundKind.MetalCutting, CutSoundRadius),
            _ => ((SoundKind?)null, 0f),
        };
        if (soundKind is { } kind
            && MathF.Floor(previous / WorkSoundIntervalSeconds) != MathF.Floor(work.ElapsedSeconds / WorkSoundIntervalSeconds))
        {
            events.Publish(new SoundEmittedEvent(tick, work.Target, soundRadius, kind, actor));
        }

        if (work.ElapsedSeconds < work.TotalSeconds)
            return;

        interactor.Work = null;
        CompleteWork(ecsWorld, actor, inventory, work, tick);
    }

    private void CompleteWork(Arch.Core.World ecsWorld, Entity actor, Inventory inventory, ActiveWork work, ulong tick)
    {
        switch (work.Kind)
        {
            case InteractionKind.Craft:
                var recipe = recipes.FirstOrDefault(r => r.Id == work.Id);
                if (recipe is not null && inventory.HasAll(recipe.Ingredients))
                    CompleteCraft(actor, inventory, recipe, tick);
                break;

            case InteractionKind.Dig:
                world.Floor(work.Target.Floor)
                    .SetFloorTile(work.Target.X, work.Target.Y, world.Tiles.IdOf(TunnelTileId));
                events.Publish(new TileDugEvent(tick, actor, work.Target));
                break;

            case InteractionKind.CutFence:
                world.Floor(work.Target.Floor)
                    .SetWallTile(work.Target.X, work.Target.Y, TileRegistry.EmptyId);
                events.Publish(new FenceCutEvent(tick, actor, work.Target));
                break;

            case InteractionKind.Lockpick:
                if (FindDoor(ecsWorld, work.Target) is { } door && door.Locked)
                {
                    door.Locked = false;
                    events.Publish(new DoorUnlockedEvent(tick, actor, work.Target));
                }
                break;
        }
    }

    private void CompleteCraft(Entity actor, Inventory inventory, RecipeDefinition recipe, ulong tick)
    {
        foreach (var ingredient in recipe.Ingredients)
            inventory.Remove(ingredient);
        inventory.Items.Add(recipe.Output);
        events.Publish(new ItemCraftedEvent(tick, actor, recipe.Id, recipe.Output));
    }

    private static ActiveWork NewWork(InteractionKind kind, TilePos target, string? id, float totalSeconds, in Position position) =>
        new()
        {
            Kind = kind,
            Target = target,
            Id = id,
            TotalSeconds = totalSeconds,
            AnchorX = position.X,
            AnchorY = position.Y,
        };

    private ItemDefinition? FirstTool(Inventory inventory, Func<ItemDefinition, bool> capability)
    {
        foreach (var id in inventory.Items)
            if (items.TryGet(id, out var item) && capability(item))
                return item;
        return null;
    }

    private static bool WithinReach(in Position position, TilePos target) =>
        target.Floor == position.Floor
        && MathF.Sqrt(
            (position.X - (target.X + 0.5f)) * (position.X - (target.X + 0.5f)) +
            (position.Y - (target.Y + 0.5f)) * (position.Y - (target.Y + 0.5f))) <= ReachTiles;

    private (Entity Entity, string ItemId)? FindWorldItem(Arch.Core.World ecsWorld, TilePos tile)
    {
        (Entity, string)? found = null;
        ecsWorld.Query(in WorldItems, (Entity entity, ref WorldItem item) =>
        {
            if (found is null && item.Tile == tile && !_pendingDestroy.Contains(entity))
                found = (entity, item.ItemId);
        });
        return found;
    }

    private static Door? FindDoor(Arch.Core.World ecsWorld, TilePos tile)
    {
        Door? found = null;
        ecsWorld.Query(in Doors, (ref Door door) =>
        {
            if (found is null && door.Tile == tile)
                found = door;
        });
        return found;
    }
}
