
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimJS.AI;

/// <summary>
/// Identifies the kind of operation received by Blackboard middleware.
/// </summary>
public enum BlackboardOperationKind
{
    /// <summary>A key/value state update.</summary>
    Change,

    /// <summary>A named event with an optional payload.</summary>
    Signal
}

/// <summary>
/// Represents a committed state change.
/// </summary>
/// <param name="Key">The changed state's key.</param>
/// <param name="OldValue">The previous value, or null if absent.</param>
/// <param name="NewValue">The committed value.</param>
public sealed record BlackboardChange(
    string Key,
    object? OldValue,
    object? NewValue)
{
    /// <summary>
    /// Retrieves the new value as <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <returns>The typed new value.</returns>
    /// <exception cref="InvalidCastException">
    /// The value is incompatible with the requested type.
    /// </exception>
    public T GetValue<T>() =>
        BlackboardTypes.Cast<T>(NewValue, $"State '{Key}'");
}

/// <summary>
/// Represents a named event emitted into a Blackboard scope.
/// </summary>
/// <param name="Name">The event name.</param>
/// <param name="Data">Optional event payload.</param>
public sealed record BlackboardSignal(
    string Name,
    object? Data)
{
    /// <summary>
    /// Retrieves the event payload as <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Expected payload type.</typeparam>
    /// <returns>The typed payload.</returns>
    /// <exception cref="InvalidCastException">
    /// The payload is incompatible with the requested type.
    /// </exception>
    public T GetData<T>() =>
        BlackboardTypes.Cast<T>(Data, $"Event '{Name}'");
}

/// <summary>
/// Shared API for the root Blackboard and its isolated channels.
/// </summary>
/// <remarks>
/// Each scope owns independent state, multicast events, interceptors,
/// and middleware. Operations are processed by the owning Blackboard.
/// Public events are delivered only to subscribers of their own scope.
///
/// This implementation is designed for single-threaded execution.
/// Concurrent mutation from multiple threads requires external
/// synchronization.
/// </remarks>
public abstract class BlackboardScope
{
    private readonly Blackboard? _owner;
    private readonly Dictionary<string, object?> _state = new();
    private readonly List<Action<BlackboardContext>> _middleware = new();
    private readonly List<Action<BlackboardContext>> _interceptors = new();

    internal Blackboard Owner => _owner ?? (Blackboard)this;

    /// <summary>
    /// Gets the channel name, or null for the root scope.
    /// </summary>
    public string? ChannelName { get; }

    /// <summary>
    /// Occurs after a state value changes successfully.
    /// </summary>
    /// <remarks>
    /// This is a standard multicast C# event.
    /// Subscribers are not automatically transferred between scopes.
    /// </remarks>
    /// <example>
    /// <code>
    /// bb.Changed += change =>
    ///     Console.WriteLine(change.Key);
    /// </code>
    /// </example>
    public event Action<BlackboardChange>? Changed;

    /// <summary>
    /// Occurs when a named event is accepted by this scope.
    /// </summary>
    /// <example>
    /// <code>
    /// bb.Signaled += signal =>
    ///     Console.WriteLine(signal.Name);
    /// </code>
    /// </example>
    public event Action<BlackboardSignal>? Signaled;

    internal BlackboardScope(Blackboard? owner, string? channelName)
    {
        _owner = owner;
        ChannelName = channelName;
    }

    /// <summary>
    /// Gets a state value using its expected type.
    /// </summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">State key.</param>
    /// <returns>The stored value.</returns>
    /// <exception cref="KeyNotFoundException">
    /// The key does not exist in this scope.
    /// </exception>
    /// <exception cref="InvalidCastException">
    /// The stored value is incompatible with T.
    /// </exception>
    /// <example>
    /// <code>
    /// int health = bb.Get&lt;int&gt;("health");
    /// </code>
    /// </example>
    public T Get<T>(string key)
    {
        ValidateName(key, nameof(key));

        if (!_state.TryGetValue(key, out var value))
            throw new KeyNotFoundException(
                $"State '{key}' does not exist in scope '{ScopeName}'.");

        return BlackboardTypes.Cast<T>(value, $"State '{key}'");
    }

    /// <summary>
    /// Attempts to retrieve a typed state value.
    /// </summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">State key.</param>
    /// <param name="value">
    /// Receives the value if found, otherwise default.
    /// </param>
    /// <returns>True if the key exists; otherwise false.</returns>
    /// <exception cref="InvalidCastException">
    /// The key exists but contains an incompatible type.
    /// </exception>
    /// <example>
    /// <code>
    /// if (bb.TryGet&lt;bool&gt;("alert", out var alert))
    ///     Console.WriteLine(alert);
    /// </code>
    /// </example>
    public bool TryGet<T>(string key, out T value)
    {
        ValidateName(key, nameof(key));

        if (!_state.TryGetValue(key, out var raw))
        {
            value = default!;
            return false;
        }

        value = BlackboardTypes.Cast<T>(raw, $"State '{key}'");
        return true;
    }

    /// <summary>
    /// Gets a value, or returns a fallback if the key is absent.
    /// </summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">State key.</param>
    /// <param name="fallback">Value returned when the key is absent.</param>
    /// <returns>The stored value or fallback.</returns>
    /// <exception cref="InvalidCastException">
    /// An existing value has an incompatible type.
    /// </exception>
    public T GetOrDefault<T>(string key, T fallback = default!) =>
        TryGet<T>(key, out var value) ? value : fallback;

    /// <summary>
    /// Checks whether a key exists in this scope.
    /// </summary>
    /// <param name="key">State key.</param>
    /// <returns>True when the key exists.</returns>
    public bool Contains(string key)
    {
        ValidateName(key, nameof(key));
        return _state.ContainsKey(key);
    }

    /// <summary>Copies the current state of this scope. Mutating the copy does not change the board.</summary>
    public IReadOnlyDictionary<string, object?> Snapshot() =>
        new Dictionary<string, object?>(_state, StringComparer.Ordinal);

    /// <summary>
    /// Requests a state update.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="key">State key.</param>
    /// <param name="value">New value.</param>
    /// <remarks>
    /// The update passes through interceptors before being committed.
    /// Equivalent values do not generate change notifications.
    ///
    /// Updates requested inside callbacks are queued for later
    /// processing in the same dispatch cycle.
    /// </remarks>
    /// <example>
    /// <code>
    /// bb.Set("health", 100);
    /// bb.Set("target", new TargetInfo("enemy", 10f));
    /// </code>
    /// </example>
    public void Set<T>(string key, T value)
    {
        ValidateName(key, nameof(key));

        Owner.Dispatch(new BlackboardOperation(
            this,
            BlackboardOperationKind.Change,
            key,
            value));
    }

    /// <summary>
    /// Emits a named event with an optional strongly typed payload.
    /// </summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="name">Event name.</param>
    /// <param name="data">Event payload.</param>
    /// <example>
    /// <code>
    /// bb.Emit("damage", 25);
    /// bb.Emit("target-found", new TargetInfo("enemy", 10f));
    /// </code>
    /// </example>
    public void Emit<T>(string name, T data)
    {
        ValidateName(name, nameof(name));

        Owner.Dispatch(new BlackboardOperation(
            this,
            BlackboardOperationKind.Signal,
            name,
            data));
    }

    /// <summary>
    /// Emits a named event without a payload.
    /// </summary>
    /// <param name="name">Event name.</param>
    /// <example>
    /// <code>bb.Emit("heartbeat");</code>
    /// </example>
    public void Emit(string name) =>
        Emit<object?>(name, null);

    /// <summary>
    /// Registers reactive middleware executed after an operation
    /// has been accepted.
    /// </summary>
    /// <param name="handler">Middleware callback.</param>
    /// <returns>
    /// A disposable registration. Dispose it to unregister the callback.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// The handler is null.
    /// </exception>
    /// <remarks>
    /// Root middleware observes operations from every scope.
    /// Channel middleware observes only operations from its channel.
    ///
    /// Middleware may read state, request changes, or emit events.
    /// Root middleware can additionally access other channels.
    /// </remarks>
    /// <example>
    /// <code>
    /// using var registration = bb.Use(ctx =>
    /// {
    ///     if (ctx.IsEvent("damage"))
    ///         ctx.Set("damaged", true);
    /// });
    /// </code>
    /// </example>
    public IDisposable Use(Action<BlackboardContext> handler) =>
        Register(_middleware, handler);

    /// <summary>
    /// Registers middleware that executes before an operation commits.
    /// </summary>
    /// <param name="handler">Interceptor callback.</param>
    /// <returns>A disposable registration.</returns>
    /// <remarks>
    /// Interceptors may inspect, replace, or cancel an operation.
    /// Root interceptors execute before channel interceptors.
    /// </remarks>
    /// <example>
    /// <code>
    /// using var guard = bb.UseInterceptor(ctx =>
    /// {
    ///     if (ctx.IsChange("health", -1))
    ///         ctx.Cancel();
    /// });
    /// </code>
    /// </example>
    public IDisposable UseInterceptor(Action<BlackboardContext> handler) =>
        Register(_interceptors, handler);

    private IDisposable Register(
        List<Action<BlackboardContext>> handlers,
        Action<BlackboardContext> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        handlers.Add(handler);
        return new BlackboardRegistration(() => handlers.Remove(handler));
    }

    internal Action<BlackboardContext>[] GetMiddleware() =>
        _middleware.ToArray();

    internal Action<BlackboardContext>[] GetInterceptors() =>
        _interceptors.ToArray();

    internal bool TryGetRaw(string key, out object? value) =>
        _state.TryGetValue(key, out value);

    internal void Commit(BlackboardOperation operation)
    {
        if (operation.Kind == BlackboardOperationKind.Signal)
        {
            Signaled?.Invoke(new BlackboardSignal(
                operation.Key,
                operation.Value));

            return;
        }

        bool existed = _state.TryGetValue(
            operation.Key,
            out var previous);

        if (existed && Equals(previous, operation.Value))
            return;

        _state[operation.Key] = operation.Value;

        Changed?.Invoke(new BlackboardChange(
            operation.Key,
            previous,
            operation.Value));
    }

    internal bool IsUnchanged(BlackboardOperation operation) =>
        operation.Kind == BlackboardOperationKind.Change &&
        _state.TryGetValue(operation.Key, out var previous) &&
        Equals(previous, operation.Value);

    internal string ScopeName => ChannelName ?? "<root>";

    internal static void ValidateName(string name, string parameter)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "A non-empty name is required.",
                parameter);
    }
}

/// <summary>
/// An independent, reactive state container with isolated named channels.
/// </summary>
/// <remarks>
/// Use <see cref="Global"/> for the default shared instance, or create
/// independent instances using the public constructor.
///
/// Root middleware observes all operations, while root public events
/// only report operations belonging to root state.
/// </remarks>
/// <example>
/// <code>
/// var global = Blackboard.Global;
/// var local = new Blackboard();
///
/// var birds = local.Channels.Get("birds");
/// birds.Set("alert", false);
/// </code>
/// </example>
public sealed class Blackboard : BlackboardScope
{
    private static readonly Lazy<Blackboard> Shared = new(
        () => new Blackboard());

    private readonly Queue<BlackboardOperation> _queue = new();
    private bool _processing;

    /// <summary>
    /// Gets the default, lazily initialized singleton Blackboard.
    /// </summary>
    public static Blackboard Global => Shared.Value;

    /// <summary>
    /// Gets the registry of isolated, named channels.
    /// </summary>
    public BlackboardChannels Channels { get; }

    /// <summary>
    /// Maximum operations permitted in one dispatch cycle.
    /// </summary>
    /// <remarks>
    /// Prevents accidental unbounded reaction cascades.
    /// Defaults to 10,000.
    /// </remarks>
    public int MaxOperationsPerDispatch { get; set; } = 10_000;

    /// <summary>
    /// Creates an independent Blackboard instance.
    /// </summary>
    public Blackboard() : base(null, null)
    {
        Channels = new BlackboardChannels(this);
    }

    internal void Dispatch(BlackboardOperation operation)
    {
        _queue.Enqueue(operation);

        if (_processing)
            return;

        _processing = true;
        int processed = 0;

        try
        {
            while (_queue.Count > 0)
            {
                if (MaxOperationsPerDispatch <= 0)
                    throw new InvalidOperationException(
                        "MaxOperationsPerDispatch must be greater than zero.");

                if (++processed > MaxOperationsPerDispatch)
                    throw new InvalidOperationException(
                        "Blackboard dispatch limit exceeded. " +
                        "Possible cyclic middleware or event propagation.");

                var next = _queue.Dequeue();

                try
                {
                    Process(next);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Blackboard operation '{next.Kind}' " +
                        $"on '{next.Key}' in scope " +
                        $"'{next.Scope.ScopeName}' failed.",
                        ex);
                }
            }
        }
        catch
        {
            // Do not replay operations from a failed dispatch.
            _queue.Clear();
            throw;
        }
        finally
        {
            _processing = false;
        }
    }

    private void Process(BlackboardOperation operation)
    {
        var scope = operation.Scope;

        // The root is the only privileged middleware scope.
        var rootContext = new BlackboardContext(
            operation, this, true, true);

        var localContext = new BlackboardContext(
            operation, this, false, true);

        // Pre-commit interception.
        foreach (var handler in GetInterceptors())
        {
            handler(rootContext);

            if (operation.Cancelled)
                return;
        }

        if (!ReferenceEquals(scope, this))
        {
            foreach (var handler in scope.GetInterceptors())
            {
                handler(localContext);

                if (operation.Cancelled)
                    return;
            }
        }

        // Skip duplicate state updates after interceptor transforms.
        if (scope.IsUnchanged(operation))
            return;

        // Commit and notify local public subscribers.
        scope.Commit(operation);

        // Post-commit middleware cannot cancel or replace the operation.
        rootContext.IsIntercepting = false;
        localContext.IsIntercepting = false;

        foreach (var handler in GetMiddleware())
            handler(rootContext);

        if (!ReferenceEquals(scope, this))
        {
            foreach (var handler in scope.GetMiddleware())
                handler(localContext);
        }
    }
}

/// <summary>
/// An isolated, named scope within a parent Blackboard.
/// </summary>
/// <remarks>
/// A channel shares its parent's dispatch pipeline but not its state,
/// ordinary event subscribers, or locally registered middleware.
/// </remarks>
public sealed class BlackboardChannel : BlackboardScope
{
    internal BlackboardChannel(Blackboard owner, string name)
        : base(owner, name)
    {
    }
}

/// <summary>
/// Provides stable, named channels belonging to a Blackboard.
/// </summary>
/// <remarks>
/// A channel is created on first access and subsequent calls with
/// the same name return the identical instance.
/// </remarks>
public sealed class BlackboardChannels
{
    private readonly Blackboard _owner;
    private readonly Dictionary<string, BlackboardChannel> _channels =
        new(StringComparer.Ordinal);

    internal BlackboardChannels(Blackboard owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Gets or creates an isolated channel.
    /// </summary>
    /// <param name="name">Unique channel name.</param>
    /// <returns>The existing or newly created channel.</returns>
    /// <exception cref="ArgumentException">
    /// The channel name is empty or whitespace.
    /// </exception>
    /// <example>
    /// <code>
    /// var birds = bb.Channels.Get("birds");
    /// var sameBirds = bb.Channels.Get("birds");
    /// </code>
    /// </example>
    public BlackboardChannel Get(string name)
    {
        BlackboardScope.ValidateName(name, nameof(name));

        if (!_channels.TryGetValue(name, out var channel))
        {
            channel = new BlackboardChannel(_owner, name);
            _channels.Add(name, channel);
        }

        return channel;
    }

    /// <summary>
    /// Attempts to find a channel without creating it.
    /// </summary>
    /// <param name="name">Channel name.</param>
    /// <param name="channel">Matching channel, if found.</param>
    /// <returns>True if the channel exists.</returns>
    public bool TryGet(string name, out BlackboardChannel? channel)
    {
        BlackboardScope.ValidateName(name, nameof(name));
        return _channels.TryGetValue(name, out channel);
    }

    /// <summary>
    /// Gets the names of all currently registered channels.
    /// </summary>
    public IReadOnlyCollection<string> Names =>
        Array.AsReadOnly(_channels.Keys.ToArray());
}

/// <summary>
/// Context supplied to Blackboard interceptors and middleware.
/// </summary>
/// <remarks>
/// The context always represents the operation's originating scope,
/// regardless of which middleware registration is executing.
///
/// Root middleware can access <see cref="Channels"/>.
/// Channel middleware is restricted to its originating scope.
/// </remarks>
public sealed class BlackboardContext
{
    private readonly BlackboardOperation _operation;
    private readonly Blackboard _owner;
    private readonly bool _allowChannels;

    internal bool IsIntercepting { get; set; }

    internal BlackboardContext(
        BlackboardOperation operation,
        Blackboard owner,
        bool allowChannels,
        bool intercepting)
    {
        _operation = operation;
        _owner = owner;
        _allowChannels = allowChannels;
        IsIntercepting = intercepting;
    }

    /// <summary>
    /// Gets the scope where the operation originated.
    /// </summary>
    public BlackboardScope Scope => _operation.Scope;

    /// <summary>
    /// Gets the originating channel name, or null for root.
    /// </summary>
    public string? ChannelName => Scope.ChannelName;

    /// <summary>
    /// Gets the kind of the current operation.
    /// </summary>
    public BlackboardOperationKind Kind => _operation.Kind;

    /// <summary>
    /// Gets the state key or event name.
    /// </summary>
    public string Key => _operation.Key;

    /// <summary>
    /// Gets the pending or committed operation value.
    /// </summary>
    public object? Value => _operation.Value;

    /// <summary>
    /// Gets the previous state value for a change operation.
    /// Returns null if the key did not previously exist.
    /// </summary>
    public object? OldValue =>
        Kind == BlackboardOperationKind.Change
            ? _operation.OldValue
            : null;

    /// <summary>
    /// Gets the parent Blackboard's channels.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The current middleware was registered on an isolated channel.
    /// </exception>
    /// <remarks>
    /// Cross-channel access is intentionally granted only to
    /// middleware registered on the root Blackboard.
    /// </remarks>
    public BlackboardChannels Channels =>
        _allowChannels
            ? _owner.Channels
            : throw new InvalidOperationException(
                "Cross-channel access is unavailable to channel middleware. " +
                "Register this behaviour on the root Blackboard instead.");

    /// <summary>
    /// Determines whether this is a named event.
    /// </summary>
    /// <param name="name">Expected event name.</param>
    /// <returns>True if the event name matches.</returns>
    public bool IsEvent(string name) =>
        Kind == BlackboardOperationKind.Signal &&
        string.Equals(Key, name, StringComparison.Ordinal);

    /// <summary>
    /// Determines whether this is a state change to a given key.
    /// </summary>
    /// <param name="key">Expected state key.</param>
    /// <returns>True if the key matches.</returns>
    public bool IsChange(string key) =>
        Kind == BlackboardOperationKind.Change &&
        string.Equals(Key, key, StringComparison.Ordinal);

    /// <summary>
    /// Determines whether this is a state change with a matching value.
    /// </summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">Expected key.</param>
    /// <param name="value">Expected value.</param>
    /// <returns>True if both key and value match.</returns>
    public bool IsChange<T>(string key, T value) =>
        IsChange(key) && Equals(Value, value);

    /// <summary>
    /// Gets the current operation value as a specific type.
    /// </summary>
    /// <typeparam name="T">Expected type.</typeparam>
    /// <returns>The typed operation value.</returns>
    /// <exception cref="InvalidCastException">
    /// The value cannot be represented as T.
    /// </exception>
    public T GetValue<T>() =>
        BlackboardTypes.Cast<T>(Value, $"Operation '{Key}'");

    /// <summary>
    /// Reads state from the originating scope.
    /// </summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">State key.</param>
    /// <returns>The typed value.</returns>
    public T Get<T>(string key) => Scope.Get<T>(key);

    /// <summary>
    /// Reads state from the originating scope with a fallback.
    /// </summary>
    /// <typeparam name="T">Expected value type.</typeparam>
    /// <param name="key">State key.</param>
    /// <param name="fallback">Fallback when absent.</param>
    /// <returns>The stored value or fallback.</returns>
    public T GetOrDefault<T>(string key, T fallback = default!) =>
        Scope.GetOrDefault(key, fallback);

    /// <summary>
    /// Requests a state update in the originating scope.
    /// </summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="key">State key.</param>
    /// <param name="value">New value.</param>
    public void Set<T>(string key, T value) =>
        Scope.Set(key, value);

    /// <summary>
    /// Emits an event into the originating scope.
    /// </summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="name">Event name.</param>
    /// <param name="data">Event payload.</param>
    public void Emit<T>(string name, T data) =>
        Scope.Emit(name, data);

    /// <summary>
    /// Emits a payload-free event into the originating scope.
    /// </summary>
    /// <param name="name">Event name.</param>
    public void Emit(string name) =>
        Scope.Emit(name);

    /// <summary>
    /// Cancels the pending operation before it commits.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Called outside interceptor execution.
    /// </exception>
    public void Cancel()
    {
        RequireInterceptor();
        _operation.Cancelled = true;
    }

    /// <summary>
    /// Replaces the value of the pending operation.
    /// </summary>
    /// <typeparam name="T">Replacement value type.</typeparam>
    /// <param name="value">Replacement value.</param>
    /// <exception cref="InvalidOperationException">
    /// Called outside interceptor execution.
    /// </exception>
    /// <remarks>
    /// Can transform state values and event payloads.
    /// Does not alter the operation's key or originating scope.
    /// </remarks>
    /// <example>
    /// <code>
    /// bb.UseInterceptor(ctx =>
    /// {
    ///     if (ctx.IsChange("health"))
    ///         ctx.Replace(Math.Max(0, ctx.GetValue&lt;int&gt;()));
    /// });
    /// </code>
    /// </example>
    public void Replace<T>(T value)
    {
        RequireInterceptor();
        _operation.Value = value;
    }

    private void RequireInterceptor()
    {
        if (!IsIntercepting)
            throw new InvalidOperationException(
                "Cancel() and Replace() are only valid inside " +
                "pre-commit interceptors.");
    }
}

/// <summary>
/// Internal representation of an operation awaiting dispatch.
/// </summary>
internal sealed class BlackboardOperation
{
    internal BlackboardScope Scope { get; }
    internal BlackboardOperationKind Kind { get; }
    internal string Key { get; }
    internal object? Value { get; set; }
    internal object? OldValue { get; }
    internal bool Cancelled { get; set; }

    internal BlackboardOperation(
        BlackboardScope scope,
        BlackboardOperationKind kind,
        string key,
        object? value)
    {
        Scope = scope;
        Kind = kind;
        Key = key;
        Value = value;

        object? previous = null;
        if (kind == BlackboardOperationKind.Change)
        {
            scope.TryGetRaw(key, out previous);
            OldValue = previous;
        }
    }
}

/// <summary>
/// A disposable registration that unregisters exactly once.
/// </summary>
internal sealed class BlackboardRegistration : IDisposable
{
    private Action? _remove;

    internal BlackboardRegistration(Action remove)
    {
        _remove = remove;
    }

    /// <summary>
    /// Unregisters the associated middleware callback.
    /// Subsequent calls have no effect.
    /// </summary>
    public void Dispose()
    {
        var action = _remove;
        _remove = null;
        action?.Invoke();
    }
}

/// <summary>
/// Type conversion helpers that preserve strict generic type safety.
/// </summary>
internal static class BlackboardTypes
{
    internal static T Cast<T>(object? value, string description)
    {
        if (value is T typed)
            return typed;

        if (value is null && default(T) is null)
            return default!;

        string actual = value?.GetType().FullName ?? "null";
        string expected = typeof(T).FullName ?? typeof(T).Name;

        throw new InvalidCastException(
            $"{description} contains '{actual}', " +
            $"but the caller requested '{expected}'.");
    }
}
