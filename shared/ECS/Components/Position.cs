namespace Prison.Shared.ECS.Components;

/// <summary>
/// World-space position in tile units, plus the floor the entity is on (PLAN §7.2:
/// the world is organized into floors for multi-story buildings).
/// </summary>
public record struct Position(float X, float Y, int Floor);
