using System;
using System.Collections.Generic;

namespace ValheimJS.Utils;

/// <summary>
/// Samples named boolean conditions on a caller-driven cadence and turns their edges into
/// callbacks: <c>name:Enter</c> on false→true, <c>name:Exit</c> on true→false, and the bare
/// <c>name</c> on later true samples. Host, not plugin, owns the clock; call <see cref="Advance"/>
/// from one update loop.
/// <para>
/// This is an observation layer, not a state machine: conditions are sampled (a pulse shorter than
/// the interval is invisible), failures freeze the last value, and handler failures are isolated.
/// Definitions keep insertion order, and a handler may add or remove conditions during dispatch.
/// </para>
/// </summary>
public sealed class ConditionObserver : IDisposable
{
    private sealed class Condition
    {
        internal string Name = string.Empty;
        internal Func<bool> Evaluate = null!;
        internal bool Current;
        internal bool Last;
        internal bool Dirty;
        internal bool Failed;
    }

    private sealed class Subscription
    {
        internal Action Handler = null!;
        internal bool Once;
    }

    private readonly List<Condition> _conditions = new();
    private readonly Dictionary<string, Condition> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Subscription>> _handlers = new(StringComparer.Ordinal);
    private readonly Action<string>? _log;
    private double _accumulatedMs;
    private double _intervalMs;
    private bool _disposed;

    public ConditionObserver(double intervalMs = 250, Action<string>? log = null)
    {
        IntervalMs = intervalMs;
        _log = log;
    }

    /// <summary>Sampling period in milliseconds. Advance accumulates time and evaluates at most once per call.</summary>
    public double IntervalMs
    {
        get => _intervalMs;
        set
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The observation interval must be a finite positive number of milliseconds.");
            }

            _intervalMs = value;
        }
    }

    /// <summary>Number of defined conditions.</summary>
    public int Count => _conditions.Count;

    /// <summary>Defines or replaces a condition, keeping its handlers and resetting its history.</summary>
    public void Observe(string name, Func<bool> condition)
    {
        EnsureAlive();
        ValidateName(name);
        if (condition == null)
        {
            throw new ArgumentNullException(nameof(condition));
        }

        if (_byName.TryGetValue(name, out var existing))
        {
            existing.Evaluate = condition;
            existing.Current = false;
            existing.Last = false;
            existing.Dirty = false;
            existing.Failed = false;
            return;
        }

        var entry = new Condition { Name = name, Evaluate = condition };
        _conditions.Add(entry);
        _byName.Add(name, entry);
    }

    /// <summary>Removes the condition, leaving its subscriptions in place.</summary>
    public bool Remove(string name)
    {
        EnsureAlive();
        ValidateName(name);
        if (!_byName.Remove(name, out var condition))
        {
            return false;
        }

        _conditions.Remove(condition);
        return true;
    }

    /// <summary>Last sampled value; never evaluates.</summary>
    public bool Is(string name)
    {
        EnsureAlive();
        return _byName.TryGetValue(name, out var condition) && condition.Current;
    }

    /// <summary>True when the condition is defined, regardless of its value.</summary>
    public bool Has(string name)
    {
        EnsureAlive();
        return _byName.ContainsKey(name);
    }

    /// <summary>True when the last evaluation changed the value; reading does not clear it.</summary>
    public bool Changed(string name)
    {
        EnsureAlive();
        return _byName.TryGetValue(name, out var condition) && condition.Dirty;
    }

    /// <summary>Independent copy of every condition and its last value; evaluates nothing.</summary>
    public Dictionary<string, bool> Snapshot()
    {
        EnsureAlive();
        var result = new Dictionary<string, bool>(_conditions.Count, StringComparer.Ordinal);
        foreach (var condition in _conditions)
        {
            result[condition.Name] = condition.Current;
        }

        return result;
    }

    /// <summary>Forgets the condition's history without removing it or firing an exit.</summary>
    public bool Reset(string name)
    {
        EnsureAlive();
        ValidateName(name);
        if (!_byName.TryGetValue(name, out var condition))
        {
            return false;
        }

        condition.Current = false;
        condition.Last = false;
        condition.Dirty = false;
        condition.Failed = false;
        return true;
    }

    /// <summary>Removes every condition and subscription, leaving the observer reusable.</summary>
    public void Clear()
    {
        EnsureAlive();
        _conditions.Clear();
        _byName.Clear();
        _handlers.Clear();
    }

    /// <summary>Subscribes to <c>name</c>, <c>name:Enter</c> or <c>name:Exit</c>. Dispose to unsubscribe.</summary>
    public IDisposable On(string eventName, Action handler)
    {
        EnsureAlive();
        ValidateName(eventName);
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        return Add(eventName, handler, once: false);
    }

    /// <summary>Subscribes for exactly one invocation, including when the handler throws.</summary>
    public IDisposable Once(string eventName, Action handler)
    {
        EnsureAlive();
        ValidateName(eventName);
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        return Add(eventName, handler, once: true);
    }

    /// <summary>Number of subscriptions on one event name.</summary>
    public int HandlerCount(string eventName)
    {
        EnsureAlive();
        return _handlers.TryGetValue(eventName, out var list) ? list.Count : 0;
    }

    /// <summary>Accumulates elapsed time; evaluates once when the interval is due. Returns dispatched events.</summary>
    public int Advance(double elapsedMs)
    {
        EnsureAlive();
        if (double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs))
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedMs));
        }

        if (elapsedMs < 0)
        {
            elapsedMs = 0;
        }

        _accumulatedMs += elapsedMs;
        if (_accumulatedMs < _intervalMs)
        {
            return 0;
        }

        // One evaluation per call: a long stall must not replay dozens of samples.
        _accumulatedMs = 0;
        return EvaluateAll();
    }

    /// <summary>Samples one condition immediately and dispatches its edges.</summary>
    public bool EvaluateNow(string name)
    {
        EnsureAlive();
        ValidateName(name);
        if (!_byName.TryGetValue(name, out var condition))
        {
            return false;
        }

        Sample(condition);
        Dispatch(condition);
        return condition.Current;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _conditions.Clear();
        _byName.Clear();
        _handlers.Clear();
    }

    private IDisposable Add(string eventName, Action handler, bool once)
    {
        var subscription = new Subscription { Handler = handler, Once = once };
        if (!_handlers.TryGetValue(eventName, out var list))
        {
            list = new List<Subscription>();
            _handlers.Add(eventName, list);
        }

        list.Add(subscription);
        return new Unsubscribe(() => RemoveSubscription(eventName, subscription));
    }

    private void RemoveSubscription(string eventName, Subscription subscription)
    {
        if (_disposed || !_handlers.TryGetValue(eventName, out var list))
        {
            return;
        }

        list.Remove(subscription);
        if (list.Count == 0)
        {
            _handlers.Remove(eventName);
        }
    }

    private int EvaluateAll()
    {
        // Snapshot so handlers may add or remove conditions without invalidating iteration.
        var snapshot = _conditions.ToArray();
        var dispatched = 0;
        foreach (var condition in snapshot)
        {
            if (!_byName.ContainsKey(condition.Name))
            {
                continue;
            }

            Sample(condition);
            dispatched += Dispatch(condition);
        }

        return dispatched;
    }

    private void Sample(Condition condition)
    {
        condition.Last = condition.Current;
        condition.Dirty = false;
        try
        {
            condition.Current = condition.Evaluate();
            condition.Failed = false;
            condition.Dirty = condition.Current != condition.Last;
        }
        catch (Exception exception)
        {
            if (!condition.Failed)
            {
                _log?.Invoke($"condition '{condition.Name}' threw: {exception.GetType().Name}: {exception.Message}");
            }

            condition.Failed = true; // keep the last value; report only the first consecutive failure
        }
    }

    private int Dispatch(Condition condition)
    {
        if (condition.Dirty)
        {
            return condition.Current
                ? Trigger(condition.Name + ":Enter")
                : Trigger(condition.Name + ":Exit");
        }

        return condition.Current ? Trigger(condition.Name) : 0;
    }

    private int Trigger(string eventName)
    {
        if (!_handlers.TryGetValue(eventName, out var list) || list.Count == 0)
        {
            return 0;
        }

        var dispatched = 0;
        var snapshot = list.ToArray();
        foreach (var subscription in snapshot)
        {
            if (subscription.Once)
            {
                RemoveSubscription(eventName, subscription);
            }

            dispatched++;
            try
            {
                subscription.Handler();
            }
            catch (Exception exception)
            {
                _log?.Invoke($"'{eventName}' handler threw: {exception.GetType().Name}: {exception.Message}");
            }
        }

        return dispatched;
    }

    private void EnsureAlive()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConditionObserver));
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Condition and event names must be nonempty and contain no NUL.", nameof(name));
        }
    }

    private sealed class Unsubscribe : IDisposable
    {
        private Action? _release;

        internal Unsubscribe(Action release) => _release = release;

        public void Dispose()
        {
            var release = _release;
            _release = null;
            release?.Invoke();
        }
    }
}
