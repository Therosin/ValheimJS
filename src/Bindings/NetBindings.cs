using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

/// <summary>
/// Routed RPC for JavaScript. Registration, dispatch, request completion and cleanup live here;
/// the SDK only shapes a channel facade over these calls.
/// <para>
/// Incoming events are delivered synchronously (a routed RPC request expects its reply on the
/// same call), so responders must return a value rather than a promise. Outgoing request
/// completion is marshalled through the host's game-thread queue.
/// </para>
/// </summary>
[JsBinder("net", Phase = JsBindPhase.Gameplay, Order = 150,
    Description = "Named RPC channels between peers: notifications and requests.")]
internal sealed class NetBindings : JsBindings
{
    private sealed class Subscription
    {
        public string Plugin = "";
        public IDisposable Handler = null!;
    }

    private readonly Dictionary<int, Subscription> _subscriptions = new();
    private readonly Dictionary<string, (string Plugin, JsCallback Callback)> _requests = new(StringComparer.Ordinal);
    private int _nextId = 1;

    public NetBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding(Description = "True when the routed RPC bus is available.")]
    public bool Available() => ZRoutedRpc.instance != null;

    [JsBinding(Description = "Target id that broadcasts to every peer.")]
    public long Everybody() => ZRoutedRpc.Everybody;

    [JsBinding(Description = "The server's peer id when known, or 0.")]
    public long ServerPeer() => ServerPeerId();

    [JsBinding(Scoped = true,
        Description = "Subscribes to a channel event. response=true makes the handler answer requests. Returns a subscription id.")]
    public int On(string plugin, string channel, string eventName, bool response, JsCallback handler)
    {
        var subscription = new Subscription { Plugin = plugin };
        subscription.Handler = Utils.RpcBus.Add(channel, eventName, response, (context, payload) =>
        {
            var meta = handler.Parse("{" +
                                     $"\"sender\":{context.Sender}," +
                                     $"\"channel\":{ValheimJS.Utils.Json.Quote(context.Channel)}," +
                                     $"\"event\":{ValheimJS.Utils.Json.Quote(context.Event)}" +
                                     "}");
            var data = handler.Parse(Encoding.UTF8.GetString(payload));
            var result = handler.Invoke(data, meta);

            if (!response)
            {
                return Array.Empty<byte>();
            }

            if (result is Jint.Native.Object.ObjectInstance instance && instance.Get("then") is Jint.Native.Function.Function)
            {
                throw new InvalidOperationException("RPC responders must return synchronously.");
            }

            return Encoding.UTF8.GetBytes(handler.Stringify(result));
        });

        var id = _nextId++;
        subscription.Plugin = plugin;
        _subscriptions[id] = subscription;
        return id;
    }

    [JsBinding(Scoped = true, Description = "Removes a subscription created by on().")]
    public void Off(string plugin, int id)
    {
        if (!_subscriptions.TryGetValue(id, out var subscription) || subscription.Plugin != plugin)
        {
            return;
        }

        _subscriptions.Remove(id);
        subscription.Handler.Dispose();
    }

    [JsBinding(Description = "Sends a JSON notification to one peer or everybody.")]
    public void Emit(long target, string channel, string eventName, string dataJson) =>
        Utils.RpcBus.NotifyRaw(target, channel, eventName, Encoding.UTF8.GetBytes(dataJson));

    [JsBinding(Scoped = true,
        Description = "Sends a request; callback(error, dataJson) runs on the game thread when the reply arrives.")]
    public void Request(string plugin, long target, string channel, string eventName, string dataJson, int timeoutMs, JsCallback callback)
    {
        if (timeoutMs < 1 || timeoutMs > 300000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeoutMs must be between 1 and 300000.");
        }

        var id = Guid.NewGuid().ToString("N");
        _requests[id] = (plugin, callback);
        var task = Utils.RpcBus.SendRaw(target, channel, eventName, Encoding.UTF8.GetBytes(dataJson), TimeSpan.FromMilliseconds(timeoutMs));
        task.ContinueWith(
            completed => Context.Post(() => Complete(plugin, id, completed)),
            TaskContinuationOptions.ExecuteSynchronously);
    }

    public override void Release(string plugin)
    {
        foreach (var id in new List<int>(_subscriptions.Keys))
        {
            if (_subscriptions[id].Plugin != plugin)
            {
                continue;
            }

            _subscriptions[id].Handler.Dispose();
            _subscriptions.Remove(id);
        }

        foreach (var id in new List<string>(_requests.Keys))
        {
            if (_requests[id].Plugin == plugin)
            {
                _requests.Remove(id);
            }
        }
    }

    public override void Dispose()
    {
        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Handler.Dispose();
        }

        _subscriptions.Clear();
        _requests.Clear();
    }

    internal static long ServerPeerId()
    {
        var peers = ZNet.instance?.GetPeers();
        if (peers != null)
        {
            foreach (var peer in peers)
            {
                if (peer != null && peer.m_server)
                {
                    return peer.m_uid;
                }
            }
        }

        return 0;
    }

    private void Complete(string plugin, string id, Task<byte[]> completed)
    {
        if (!_requests.TryGetValue(id, out var request))
        {
            return;
        }

        _requests.Remove(id);
        var callback = request.Callback;

        try
        {
            if (completed.IsCanceled)
            {
                callback.Invoke("Request cancelled", null);
            }
            else if (completed.IsFaulted)
            {
                callback.Invoke(completed.Exception?.GetBaseException().Message ?? "Request failed", null);
            }
            else
            {
                callback.Invoke(null, Encoding.UTF8.GetString(completed.Result));
            }
        }
        catch (Exception exception)
        {
            LogError($"[{plugin}] request callback failed: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
