namespace Prison.Shared.ECS.Components;

/// <summary>Velocity in tile units per second, applied by <c>MovementSystem</c>.</summary>
public record struct Velocity(float X, float Y);
