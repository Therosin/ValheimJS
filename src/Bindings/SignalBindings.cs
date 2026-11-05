using System;
using System.Collections.Generic;
using System.Diagnostics;
using ValheimJS.Networking;
using ValheimJS.Scripting.Binding;
using ValheimJS.Utils;

namespace ValheimJS.Bindings;

/// <summary>
/// Plugin-scoped observed conditions. The native <see cref="ConditionObserver"/> owns sampling,
/// edges and error isolation; this class adapts JS callbacks, applies the host cadence and keeps
/// every registration release-checked on plugin unload.
/// </summary>
[JsBinder("signals", Phase = JsBindPhase.Engine, Order = 30,
    Description = "Observed conditions sampled by the host; enter/exit events for reactive plugin logic.")]
internal sealed class SignalBindings : JsBindings
{
    private sealed class Entry
    {
        internal ConditionObserver Observer = null!;
        internal readonly Dictionary<int, IDisposable> Subscriptions = new();
        internal int NextSubscription;
    }

    private readonly Dictionary<string, Dictionary<int, Entry>> _plugins = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = new();
    private long _lastTimestamp;
    private int _nextObserver;

    public SignalBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding(Scoped = true, Description = "Creates an observer with the given sampling interval; returns its id.")]
    public int Create(string plugin, double intervalMs)
    {
        var entry = new Entry
        {
            Observer = new ConditionObserver(intervalMs, message => LogError($"[{plugin}] {message}"))
        };

        var observers = _plugins.TryGetValue(plugin, out var existing) ? existing : _plugins[plugin] = new Dictionary<int, Entry>();
        var id = ++_nextObserver;
        observers.Add(id, entry);
        return id;
    }

    [JsBinding(Scoped = true, Description = "Defines or replaces a condition; the predicate runs at the observer's interval.")]
    public void Observe(string plugin, int observer, string name, JsCallback predicate)
    {
        var entry = Find(plugin, observer);
        entry.Observer.Observe(name, () => predicate.InvokeBoolean());
    }

    [JsBinding(Scoped = true, Description = "Subscribes to name, name:Enter or name:Exit; returns a subscription id.")]
    public int On(string plugin, int observer, string eventName, JsCallback handler)
    {
        var entry = Find(plugin, observer);
        var subscription = ++entry.NextSubscription;
        var disposable = entry.Observer.On(eventName, () => handler.Invoke());
        entry.Subscriptions[subscription] = disposable;
        return subscription;
    }

    [JsBinding(Scoped = true, Description = "Subscribes for exactly one invocation, including when the handler throws.")]
    public void Once(string plugin, int observer, string eventName, JsCallback handler) =>
        Find(plugin, observer).Observer.Once(eventName, () => handler.Invoke());

    [JsBinding(Scoped = true, Description = "Removes a subscription created with on().")]
    public void Off(string plugin, int observer, int subscription)
    {
        var entry = Find(plugin, observer);
        if (!entry.Subscriptions.Remove(subscription, out var disposable))
        {
            return;
        }

        disposable.Dispose();
    }

    [JsBinding(Scoped = true, Description = "Last sampled value; never evaluates.")]
    public bool Is(string plugin, int observer, string name) => Find(plugin, observer).Observer.Is(name);

    [JsBinding(Scoped = true, Description = "True when the last evaluation changed the value; reading does not clear it.")]
    public bool Changed(string plugin, int observer, string name) => Find(plugin, observer).Observer.Changed(name);

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Json, Returns = "Record<string, boolean>",
        Description = "Independent copy of every condition and its last value; evaluates nothing.")]
    public string Snapshot(string plugin, int observer) =>
        NetworkJson.Write(Find(plugin, observer).Observer.Snapshot());

    [JsBinding(Scoped = true, Description = "Removes a condition, leaving its subscriptions in place.")]
    public void Remove(string plugin, int observer, string name) => Find(plugin, observer).Observer.Remove(name);

    [JsBinding(Scoped = true, Description = "Forgets a condition's history without removing it or firing an exit.")]
    public void Reset(string plugin, int observer, string name) => Find(plugin, observer).Observer.Reset(name);

    [JsBinding(Scoped = true, Description = "Removes every condition and subscription; the observer stays usable.")]
    public void Clear(string plugin, int observer)
    {
        var entry = Find(plugin, observer);
        entry.Observer.Clear();
        entry.Subscriptions.Clear();
    }

    [JsBinding(Scoped = true, Description = "Disposes the observer and every subscription it owns.")]
    public void Dispose(string plugin, int observer)
    {
        var observers = _plugins.TryGetValue(plugin, out var existing) ? existing : null;
        if (observers == null || !observers.Remove(observer, out var entry))
        {
            return;
        }

        foreach (var disposable in entry.Subscriptions.Values)
        {
            disposable.Dispose();
        }

        entry.Subscriptions.Clear();
        entry.Observer.Dispose();
    }

    [JsBinding(Scoped = true, Description = "Number of defined conditions.")]
    public int Count(string plugin, int observer) => Find(plugin, observer).Observer.Count;

    [JsBinding(Scoped = true, Description = "Sampling period in milliseconds.")]
    public double Interval(string plugin, int observer) => Find(plugin, observer).Observer.IntervalMs;

    [JsBinding(Scoped = true, Description = "Changes the sampling period in milliseconds.")]
    public void SetInterval(string plugin, int observer, double intervalMs) =>
        Find(plugin, observer).Observer.IntervalMs = intervalMs;

    /// <summary>Samples every observer using real elapsed time; at most one evaluation per tick.</summary>
    public override void Pump()
    {
        var timestamp = _clock.IsRunning ? _clock.ElapsedTicks : 0;
        if (!_clock.IsRunning)
        {
            _clock.Start();
            timestamp = 0;
            _lastTimestamp = timestamp;
            return;
        }

        var delta = (timestamp - _lastTimestamp) / (double)Stopwatch.Frequency * 1000d;
        _lastTimestamp = timestamp;
        if (delta <= 0)
        {
            return;
        }

        foreach (var observers in _plugins.Values)
        {
            foreach (var entry in observers.Values)
            {
                entry.Observer.Advance(delta);
            }
        }
    }

    public override void Release(string plugin)
    {
        var observers = _plugins.TryGetValue(plugin, out var existing) ? existing : null;
        if (observers == null)
        {
            return;
        }

        foreach (var entry in observers.Values)
        {
            foreach (var disposable in entry.Subscriptions.Values)
            {
                disposable.Dispose();
            }

            entry.Observer.Dispose();
        }

        _plugins.Remove(plugin);
    }

    public override void Dispose()
    {
        foreach (var plugin in new List<string>(_plugins.Keys))
        {
            Release(plugin);
        }

        _plugins.Clear();
    }

    private Entry Find(string plugin, int observer)
    {
        var observers = _plugins.TryGetValue(plugin, out var existing) ? existing : null;
        if (observers == null || !observers.TryGetValue(observer, out var entry))
        {
            throw new InvalidOperationException($"signals: observer {observer} does not exist for plugin '{plugin}'.");
        }

        return entry;
    }
}
