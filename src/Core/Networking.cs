using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValheimJS.Networking;

namespace ValheimJS.Core;

/// <summary>
/// ValheimJS's socket-style channel/event system over RpcBus. Scopes own listeners and pending
/// requests; the transport owns framing, response correlation and wire-level limits. No JS types.
/// </summary>
public sealed class Networking : IDisposable
{
    private readonly IRpcTransport _transport;
    private readonly Action<string> _logError;
    private readonly Queue<Action> _posted = new();
    private readonly HashSet<NetworkScope> _scopes = new();
    private bool _disposed;

    public Networking(IRpcTransport transport, Action<string>? logError = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _logError = logError ?? (_ => { });
        Sync = new NetworkSync(transport);
    }
    public bool Available => !_disposed && _transport.Available;
    public long Everybody => _transport.Everybody;
    public long ServerPeer => _transport.ServerPeer;
    public NetworkSync Sync { get; }

    public NetworkScope OpenScope()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Networking));
        var scope = new NetworkScope(_transport, Post, _logError, item => _scopes.Remove(item));
        _scopes.Add(scope);
        return scope;
    }
    public void Post(Action action) { lock (_posted) { if (!_disposed) _posted.Enqueue(action); } }
    /// <summary>Called by the Unity main-thread host, independently of the JS timer cadence.</summary>
    public void Pump()
    {
        while (true)
        {
            Action action;
            lock (_posted) { if (_posted.Count == 0) return; action = _posted.Dequeue(); }
            try { action(); } catch (Exception exception) { _logError("Network completion failed: " + exception.Message); }
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        lock (_posted) { _disposed = true; _posted.Clear(); }
        foreach (var scope in new List<NetworkScope>(_scopes)) scope.Dispose();
        Sync.Dispose();
    }
}

/// <summary>A consumer's subscriptions and requests, grouped by owner for plugin unload.</summary>
public sealed class NetworkScope : IDisposable
{
    private sealed class Subscription { internal string Owner = ""; internal IDisposable Registration = null!; }
    private sealed class Pending
    {
        internal string Owner = "";
        internal CancellationTokenSource Cancellation = new();
        internal Action<string?, string?> Callback = null!;
    }
    private readonly IRpcTransport _transport;
    private readonly Action<Action> _post;
    private readonly Action<string> _log;
    private readonly Action<NetworkScope> _onDispose;
    private readonly Dictionary<int, Subscription> _subscriptions = new();
    private readonly Dictionary<Guid, Pending> _requests = new();
    private int _nextId;
    private bool _disposed;

    internal NetworkScope(IRpcTransport transport, Action<Action> post, Action<string> log, Action<NetworkScope> onDispose)
    { _transport = transport; _post = post; _log = log; _onDispose = onDispose; }

    public NetworkChannel Channel(string channel, string owner = "service") => new(this, owner, channel);
    public int On(string owner, string channel, string eventName, bool response, Func<NetworkMessage, string, string?> handler)
    {
        EnsureAlive();
        var id = ++_nextId;
        var registration = _transport.Subscribe(channel, eventName, response, (message, bytes) =>
        {
            if (_disposed || !_subscriptions.ContainsKey(id)) return Array.Empty<byte>();
            var result = handler(message, Encoding.UTF8.GetString(bytes));
            return response ? Encoding.UTF8.GetBytes(result ?? "null") : Array.Empty<byte>();
        });
        _subscriptions.Add(id, new Subscription { Owner = owner, Registration = registration });
        return id;
    }
    public void Off(string owner, int id)
    {
        if (!_subscriptions.TryGetValue(id, out var subscription) || subscription.Owner != owner) return;
        _subscriptions.Remove(id);
        subscription.Registration.Dispose();
    }
    public void Emit(long peer, string channel, string eventName, string json)
    { EnsureAlive(); _transport.Notify(peer, channel, eventName, Encoding.UTF8.GetBytes(json)); }

    public void Request(string owner, long peer, string channel, string eventName, string json, int timeoutMs, Action<string?, string?> callback)
    {
        EnsureAlive();
        if (timeoutMs < 1 || timeoutMs > 300000) throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeoutMs must be between 1 and 300000.");
        var id = Guid.NewGuid();
        var pending = new Pending { Owner = owner, Callback = callback };
        _requests.Add(id, pending);
        Task<byte[]> task;
        try { task = _transport.Request(peer, channel, eventName, Encoding.UTF8.GetBytes(json), TimeSpan.FromMilliseconds(timeoutMs), pending.Cancellation.Token); }
        catch { _requests.Remove(id); pending.Cancellation.Dispose(); throw; }
        _ = Observe(id, task);
    }
    private async Task Observe(Guid id, Task<byte[]> task)
    {
        string? error = null;
        string? data = null;
        try { data = Encoding.UTF8.GetString(await task.ConfigureAwait(false)); }
        catch (OperationCanceledException) { error = "Request cancelled"; }
        catch (Exception exception) { error = exception.GetBaseException().Message; }
        _post(() =>
        {
            if (!_requests.TryGetValue(id, out var pending)) return;
            _requests.Remove(id);
            pending.Cancellation.Dispose();
            try { pending.Callback(error, data); }
            catch (Exception exception) { _log($"[{pending.Owner}] request callback failed: {exception.GetType().Name}: {exception.Message}"); }
        });
    }
    public void Release(string owner)
    {
        foreach (var id in new List<int>(_subscriptions.Keys)) Off(owner, id);
        foreach (var id in new List<Guid>(_requests.Keys))
        {
            var pending = _requests[id];
            if (pending.Owner != owner) continue;
            _requests.Remove(id);
            pending.Cancellation.Cancel();
            pending.Cancellation.Dispose();
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var owners = new HashSet<string>();
        foreach (var subscription in _subscriptions.Values) owners.Add(subscription.Owner);
        foreach (var request in _requests.Values) owners.Add(request.Owner);
        foreach (var owner in owners) Release(owner);
        _onDispose(this);
    }
    private void EnsureAlive() { if (_disposed) throw new ObjectDisposedException(nameof(NetworkScope)); }
}

/// <summary>Named event namespace; payloads are JSON text shared by C# services and JS adapters.</summary>
public sealed class NetworkChannel
{
    private readonly NetworkScope _scope;
    private readonly string _owner;
    public string Name { get; }
    internal NetworkChannel(NetworkScope scope, string owner, string name) { _scope = scope; _owner = owner; Name = name; }
    public int OnJson(string eventName, Action<NetworkMessage, string> handler) => _scope.On(_owner, Name, eventName, false, (message, json) => { handler(message, json); return null; });
    public int On<T>(string eventName, Action<NetworkMessage, T> handler) => OnJson(eventName, (message, json) => handler(message, NetworkJson.Read<T>(json)));
    public int HandleJson(string eventName, Func<NetworkMessage, string, string?> handler) => _scope.On(_owner, Name, eventName, true, handler);
    public int Handle<TRequest, TResponse>(string eventName, Func<NetworkMessage, TRequest, TResponse> handler) => HandleJson(eventName, (message, json) => NetworkJson.Write(handler(message, NetworkJson.Read<TRequest>(json))));
    public void Off(int id) => _scope.Off(_owner, id);
    public void EmitJson(long peer, string eventName, string json) => _scope.Emit(peer, Name, eventName, json);
    public void Emit<T>(long peer, string eventName, T data) => EmitJson(peer, eventName, NetworkJson.Write(data));
    public void RequestJson(long peer, string eventName, string json, Action<string?, string?> completed, int timeoutMs = 30000) => _scope.Request(_owner, peer, Name, eventName, json, timeoutMs, completed);
}
