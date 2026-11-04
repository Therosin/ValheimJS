using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ValheimJS.Utils;

[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class RpcChannelAttribute : Attribute
{
    public RpcChannelAttribute(string name) => Name = name;
    public string Name { get; }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class RpcEventAttribute : Attribute
{
    public RpcEventAttribute(string name) => Name = name;
    public string Name { get; }
}

/// <summary>The sender is the routed RPC peer ID, not an authenticated player ID.</summary>
public sealed class RpcContext
{
    internal RpcContext(long sender, string channel, string eventName) { Sender = sender; Channel = channel; Event = eventName; }
    public long Sender { get; }
    public string Channel { get; }
    public string Event { get; }
}

public sealed class RpcRemoteException : Exception
{
    public RpcRemoteException(string message) : base(message) { }
}

/// <summary>
/// Multiplexed, JSON-serialized C# RPCs. Call Install on the Unity thread, and dispose
/// registrations with their owner. Handlers run on the game's RPC dispatch thread.
/// Payload types must be supported by DataContractJsonSerializer on both peers.
/// </summary>
public static class RpcBus
{
    private const string RpcName = "ValheimJS:BusEnvelope:v1";
    private const int MaxPayload = 256 * 1024;
    private const int MaxPending = 1024;
    private enum Kind : byte { Request = 1, Response, Notification, Error }
    private sealed class Handler
    {
        internal bool Responds;
        internal Func<RpcContext, byte[], byte[]> Invoke = null!;
    }
    private sealed class Pending
    {
        internal long Peer;
        internal string Channel = "";
        internal string Event = "";
        internal TaskCompletionSource<byte[]> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;
        internal Subscription(Action dispose) => _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
    private static readonly Dictionary<string, List<Handler>> Handlers = new();
    private static readonly Dictionary<string, Pending> Requests = new();
    private static readonly object Gate = new();
    private static ZRoutedRpc? _registered;
    private static bool _installed;

    public static void Install()
    {
        if (!_installed) { ZNet.s_onZNetStart += Refresh; _installed = true; }
        Refresh();
    }

    /// <summary>Registers on a new world RPC instance and fails requests from the old world.</summary>
    public static void Refresh()
    {
        if (!_installed) return;
        var rpc = ZRoutedRpc.instance;
        if (ReferenceEquals(rpc, _registered)) return;
        FailPending("The RPC connection changed.");
        _registered = null;
        if (rpc == null) return;
        rpc.Register<ZPackage>(RpcName, Receive);
        _registered = rpc;
    }

    public static void Shutdown()
    {
        if (_installed) { ZNet.s_onZNetStart -= Refresh; _installed = false; }
        FailPending("The RPC bus was shut down.");
        lock (Gate) Handlers.Clear();
        // The game owns the registration; retain the instance to avoid registering twice.
    }

    public static RpcBusChannel Channel(string name) { ValidateName(name, nameof(name)); return new RpcBusChannel(name); }

    public static async Task<TResponse> Send<TRequest, TResponse>(long targetPeer, string channel, string eventName, TRequest request,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return (TResponse)Deserialize(await SendRaw(targetPeer, channel, eventName, Serialize(request, typeof(TRequest)), timeout, cancellationToken), typeof(TResponse))!;
    }

    internal static async Task<byte[]> SendRaw(long targetPeer, string channel, string eventName, byte[] payload,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ValidateTarget(targetPeer); ValidateName(channel, nameof(channel)); ValidateName(eventName, nameof(eventName));
        var duration = timeout ?? TimeSpan.FromSeconds(30);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length > MaxPayload) throw new InvalidOperationException("RPC payload is too large.");
        var id = Guid.NewGuid().ToString("N");
        var pending = new Pending { Peer = targetPeer, Channel = channel, Event = eventName };
        lock (Gate)
        {
            if (Requests.Count >= MaxPending) throw new InvalidOperationException("Too many pending RPC requests.");
            Requests.Add(id, pending);
        }
        try
        {
            Transmit(targetPeer, Kind.Request, channel, eventName, id, payload);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(duration, timeoutSource.Token);
            var finished = await Task.WhenAny(pending.Completion.Task, delay);
            if (finished != pending.Completion.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"RPC {channel}/{eventName} timed out.");
            }
            timeoutSource.Cancel();
            return await pending.Completion.Task;
        }
        finally { lock (Gate) Requests.Remove(id); }
    }

    public static void Notify<T>(long targetPeer, string channel, string eventName, T message)
    {
        ValidateName(channel, nameof(channel)); ValidateName(eventName, nameof(eventName));
        Transmit(targetPeer, Kind.Notification, channel, eventName, "", Serialize(message, typeof(T)));
    }

    internal static void NotifyRaw(long target, string channel, string eventName, byte[] payload)
    {
        ValidateName(channel, nameof(channel)); ValidateName(eventName, nameof(eventName));
        if (payload.Length > MaxPayload) throw new InvalidOperationException("RPC payload is too large.");
        Transmit(target, Kind.Notification, channel, eventName, "", payload);
    }

    /// <summary>Register annotated instance methods; dispose the result when the instance is no longer active.</summary>
    public static IDisposable Register(object instance)
    {
        if (instance == null) throw new ArgumentNullException(nameof(instance));
        var channel = instance.GetType().GetCustomAttribute<RpcChannelAttribute>(true)?.Name
            ?? throw new ArgumentException("The instance requires RpcChannelAttribute.", nameof(instance));
        ValidateName(channel, nameof(instance));
        var subscriptions = new List<IDisposable>();
        try
        {
            foreach (var method in instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attribute = method.GetCustomAttribute<RpcEventAttribute>();
                if (attribute == null) continue;
                var parameters = method.GetParameters();
                var hasContext = parameters.Length == 2 && parameters[0].ParameterType == typeof(RpcContext);
                if ((!hasContext && parameters.Length != 1) || method.ContainsGenericParameters || typeof(Task).IsAssignableFrom(method.ReturnType))
                    throw new ArgumentException($"Invalid RPC handler signature: {method.Name}.");
                var payloadType = parameters[hasContext ? 1 : 0].ParameterType;
                if (payloadType.IsByRef || method.ReturnType.IsByRef) throw new ArgumentException($"Invalid RPC handler signature: {method.Name}.");
                var responds = method.ReturnType != typeof(void);
                subscriptions.Add(Add(channel, attribute.Name, responds, (context, bytes) =>
                {
                    var value = Deserialize(bytes, payloadType);
                    var result = method.Invoke(instance, hasContext ? new[] { (object)context, value } : new[] { value });
                    return responds ? Serialize(result, method.ReturnType) : Array.Empty<byte>();
                }));
            }
            return new Subscription(() => { foreach (var item in subscriptions) item.Dispose(); });
        }
        catch { foreach (var item in subscriptions) item.Dispose(); throw; }
    }

    internal static IDisposable Add(string channel, string eventName, bool responds, Func<RpcContext, byte[], byte[]> callback)
    {
        ValidateName(channel, nameof(channel)); ValidateName(eventName, nameof(eventName));
        var key = Key(channel, eventName);
        var handler = new Handler { Responds = responds, Invoke = callback };
        lock (Gate)
        {
            if (!Handlers.TryGetValue(key, out var list)) Handlers.Add(key, list = new List<Handler>());
            if (responds && list.Exists(h => h.Responds)) throw new InvalidOperationException("A responding handler is already registered for this channel/event.");
            list.Add(handler);
        }
        return new Subscription(() => { lock (Gate) { if (Handlers.TryGetValue(key, out var list)) { list.Remove(handler); if (list.Count == 0) Handlers.Remove(key); } } });
    }

    internal static byte[] Serialize(object? value, Type type)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(type).WriteObject(stream, value);
        if (stream.Length > MaxPayload) throw new InvalidOperationException("RPC payload is too large.");
        return stream.ToArray();
    }
    internal static object? Deserialize(byte[] bytes, Type type)
    {
        using var stream = new MemoryStream(bytes, false);
        return new DataContractJsonSerializer(type).ReadObject(stream);
    }
    private static string Key(string channel, string eventName) => channel + "\0" + eventName;
    private static void ValidateName(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.IndexOf('\0') >= 0) throw new ArgumentException("RPC names must be nonempty, at most 256 characters, and contain no NUL.", parameter);
    }
    private static void ValidateTarget(long target) { if (target == 0 || target == ZRoutedRpc.Everybody) throw new ArgumentOutOfRangeException(nameof(target), "Requests require one target peer."); }
    private static void Transmit(long target, Kind kind, string channel, string eventName, string id, byte[] payload)
    {
        var rpc = ZRoutedRpc.instance;
        if (rpc == null || !ReferenceEquals(rpc, _registered)) throw new InvalidOperationException("RPC bus is not available; call Install on the Unity thread.");
        var package = new ZPackage();
        package.Write((byte)kind); package.Write(channel); package.Write(eventName); package.Write(id); package.Write(payload);
        rpc.InvokeRoutedRPC(target, RpcName, package);
    }
    private static void Receive(long sender, ZPackage package)
    {
        try
        {
            var kind = (Kind)package.ReadByte(); var channel = package.ReadString(); var eventName = package.ReadString(); var id = package.ReadString(); var payload = package.ReadByteArray();
            ValidateName(channel, nameof(channel)); ValidateName(eventName, nameof(eventName));
            if (payload.Length > MaxPayload || id.Length > 64) throw new InvalidDataException("Invalid RPC envelope.");
            if (kind == Kind.Response || kind == Kind.Error)
            {
                Pending? pending;
                lock (Gate) Requests.TryGetValue(id, out pending);
                if (pending == null || pending.Peer != sender || pending.Channel != channel || pending.Event != eventName) return;
                if (kind == Kind.Error) pending.Completion.TrySetException(new RpcRemoteException(System.Text.Encoding.UTF8.GetString(payload)));
                else pending.Completion.TrySetResult(payload);
                return;
            }
            if (kind != Kind.Request && kind != Kind.Notification) return;
            if (kind == Kind.Request && (id.Length == 0 || sender == 0)) return;
            Handler[] handlers;
            lock (Gate) handlers = Handlers.TryGetValue(Key(channel, eventName), out var list) ? list.ToArray() : Array.Empty<Handler>();
            var context = new RpcContext(sender, channel, eventName);
            if (kind == Kind.Request)
            {
                var handler = Array.Find(handlers, h => h.Responds);
                if (handler == null) { ReplyError(sender, channel, eventName, id, "No responding handler is registered."); return; }
                try { Transmit(sender, Kind.Response, channel, eventName, id, handler.Invoke(context, payload)); }
                catch (Exception exception) { Plugin.Logger.LogWarning("[RpcBus] handler failed: " + exception); ReplyError(sender, channel, eventName, id, "Remote handler failed."); }
            }
            else foreach (var handler in handlers)
            {
                if (handler.Responds) continue;
                try { handler.Invoke(context, payload); } catch (Exception exception) { Plugin.Logger.LogWarning("[RpcBus] notification failed: " + exception); }
            }
        }
        catch (Exception exception) { Plugin.Logger.LogWarning("[RpcBus] invalid envelope: " + exception.Message); }
    }
    private static void ReplyError(long target, string channel, string eventName, string id, string message) => Transmit(target, Kind.Error, channel, eventName, id, System.Text.Encoding.UTF8.GetBytes(message));
    private static void FailPending(string message)
    {
        Pending[] pending;
        lock (Gate) { pending = new Pending[Requests.Count]; Requests.Values.CopyTo(pending, 0); Requests.Clear(); }
        foreach (var request in pending) request.Completion.TrySetException(new InvalidOperationException(message));
    }
}

public sealed class RpcBusChannel
{
    internal RpcBusChannel(string name) => Name = name;
    public string Name { get; }
    public IDisposable On<T>(string eventName, Action<RpcContext, T> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return RpcBus.Add(Name, eventName, false, (context, bytes) => { handler(context, (T)RpcBus.Deserialize(bytes, typeof(T))!); return Array.Empty<byte>(); });
    }
    public IDisposable On<T>(string eventName, Action<T> handler) => On<T>(eventName, (_, value) => handler(value));
    public IDisposable On<TRequest, TResponse>(string eventName, Func<RpcContext, TRequest, TResponse> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return RpcBus.Add(Name, eventName, true, (context, bytes) => RpcBus.Serialize(handler(context, (TRequest)RpcBus.Deserialize(bytes, typeof(TRequest))!), typeof(TResponse)));
    }
    public IDisposable On<TRequest, TResponse>(string eventName, Func<TRequest, TResponse> handler) => On<TRequest, TResponse>(eventName, (_, value) => handler(value));
    public Task<TResponse> Send<TRequest, TResponse>(long target, string eventName, TRequest request, TimeSpan? timeout = null, CancellationToken cancellationToken = default) => RpcBus.Send<TRequest, TResponse>(target, Name, eventName, request, timeout, cancellationToken);
    public void Notify<T>(long target, string eventName, T message) => RpcBus.Notify(target, Name, eventName, message);
}
