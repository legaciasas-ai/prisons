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
    private readonly List<Action<object>> _catchAll = [];

    public void Subscribe<TEvent>(Action<TEvent> handler)
    {
        if (!_subscribers.TryGetValue(typeof(TEvent), out var handlers))
            _subscribers[typeof(TEvent)] = handlers = [];
        handlers.Add(handler);
    }

    /// <summary>
    /// Receives *every* published event, type-erased. This is what makes the bus a first-class
    /// system (PLAN §7.9 / Phase 4): journaling consumers (replay recorder, debug log) observe
    /// the complete stream without enumerating event types. Typed subscribers run first.
    /// </summary>
    public void SubscribeAll(Action<object> handler) => _catchAll.Add(handler);

    public void Publish<TEvent>(TEvent evt)
    {
        if (_subscribers.TryGetValue(typeof(TEvent), out var handlers))
            foreach (var handler in handlers)
                ((Action<TEvent>)handler)(evt);

        if (_catchAll.Count > 0 && evt is not null)
            foreach (var handler in _catchAll)
                handler(evt);
    }
}
