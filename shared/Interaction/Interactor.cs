using Prison.Shared.World;

namespace Prison.Shared.Interaction;

public enum InteractionKind
{
    PickUp,
    Craft,
    Dig,
    CutFence,
    Lockpick,
    ToggleDoor,
    SetDisguise,
    Throw,
}

/// <summary>
/// One requested interaction, set by the host (client input / network message) and consumed
/// by <see cref="InteractionSystem"/> on the next tick. <see cref="Id"/> is the recipe id for
/// Craft, the item id for SetDisguise (null = undress) and Throw; unused otherwise.
/// </summary>
public readonly record struct InteractionRequest(InteractionKind Kind, TilePos Target, string? Id = null);

/// <summary>A timed interaction in progress (digging, cutting, lockpicking, crafting).</summary>
public sealed class ActiveWork
{
    public required InteractionKind Kind { get; init; }
    public required TilePos Target { get; init; }
    public string? Id { get; init; }
    public required float TotalSeconds { get; init; }
    public float ElapsedSeconds { get; set; }

    /// <summary>Where the worker stood when starting — moving away cancels the work.</summary>
    public required float AnchorX { get; init; }
    public required float AnchorY { get; init; }
}

/// <summary>Component making an entity able to interact with the world (players, later NPCs).</summary>
public sealed class Interactor
{
    public InteractionRequest? Request { get; set; }

    public ActiveWork? Work { get; set; }
}
