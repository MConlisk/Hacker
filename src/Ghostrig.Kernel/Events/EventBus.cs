using Ghostrig.Kernel.Time;

namespace Ghostrig.Kernel.Events;

/// <summary>Publishes kernel events to subscribers.</summary>
public interface IEventPublisher
{
    void Publish(KernelEvent kernelEvent);
}

/// <summary>
/// Deterministic, single-threaded Observer hub. Subscribers are invoked in subscription order; events published
/// while dispatching are queued and delivered afterwards (breadth-first), so ordering never depends on handler
/// recursion. A failing subscriber cannot corrupt kernel state: its exception is captured and rethrown after the
/// queue has drained.
/// </summary>
public sealed class EventBus : IEventPublisher
{
    private readonly SimClock _clock;
    private readonly List<Subscription> _subscriptions = [];
    private readonly Queue<KernelEvent> _pending = new();
    private bool _dispatching;

    public EventBus(SimClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>Number of events published so far (next sequence number).</summary>
    public long PublishedCount { get; private set; }

    public IDisposable Subscribe(Action<KernelEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, handler);
        _subscriptions.Add(subscription);
        return subscription;
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : KernelEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Subscribe(e =>
        {
            if (e is TEvent typed)
            {
                handler(typed);
            }
        });
    }

    public void Publish(KernelEvent kernelEvent)
    {
        ArgumentNullException.ThrowIfNull(kernelEvent);
        _pending.Enqueue(kernelEvent with { At = _clock.Now, Sequence = PublishedCount++ });
        if (_dispatching)
        {
            return;
        }

        _dispatching = true;
        List<Exception>? failures = null;
        try
        {
            while (_pending.TryDequeue(out var next))
            {
                foreach (var subscription in _subscriptions.ToArray())
                {
                    if (subscription.Active)
                    {
                        try
                        {
                            subscription.Handler(next);
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }
                    }
                }
            }
        }
        finally
        {
            _dispatching = false;
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more kernel event subscribers failed.", failures);
        }
    }

    internal void RestoreSequence(long publishedCount) => PublishedCount = publishedCount;

    private sealed class Subscription(EventBus owner, Action<KernelEvent> handler) : IDisposable
    {
        public Action<KernelEvent> Handler { get; } = handler;
        public bool Active { get; private set; } = true;

        public void Dispose()
        {
            if (Active)
            {
                Active = false;
                owner._subscriptions.Remove(this);
            }
        }
    }
}
