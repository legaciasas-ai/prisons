namespace Prison.Shared.Events;

/// <summary>
/// The in-simulation event bus (PLAN §7.9): decouples producers from consumers. A digging
/// action can feed sound propagation, guard AI, heat maps and the replay recorder without
/// any of those referencing each other. Dispatch is synchronous and deterministic (order of
/// subscription), which matters for a fixed-tick, replayable simulation.
/// </summary>
public sealed class EventBus
{
    private readonly Dictionary<Type, List<Delegate>> _subscribers = [];

    public void Subscribe<TEvent>(Action<TEvent> handler)
    {
        if (!_subscribers.TryGetValue(typeof(TEvent), out var handlers))
            _subscribers[typeof(TEvent)] = handlers = [];
        handlers.Add(handler);
    }

    public void Publish<TEvent>(TEvent evt)
    {
        if (!_subscribers.TryGetValue(typeof(TEvent), out var handlers))
            return;
        foreach (var handler in handlers)
            ((Action<TEvent>)handler)(evt);
    }
}
