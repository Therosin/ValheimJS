using System;
using ValheimJS.Core;
using ValheimJS.Networking;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

/// <summary>JS conversion and callback adaptation over the native channel/event service.</summary>
[JsBinder("net", Phase = JsBindPhase.Gameplay, Order = 150,
    Description = "Named RPC channels between peers: notifications and requests.")]
internal sealed class NetBindings : JsBindings
{
    private readonly NetworkScope _scope;
    public NetBindings(JsBindingContext context) : base(context) => _scope = context.Networking.OpenScope();

    [JsBinding(Description = "True when the routed RPC bus and world are ready for traffic.")]
    public bool Available() => Context.Networking.Available && Context.WorldReady;
    [JsBinding(Description = "Target id that broadcasts to every peer.")]
    public long Everybody() => Context.Networking.Everybody;
    [JsBinding(Description = "The server's peer id when known, or 0.")]
    public long ServerPeer() => Context.Networking.ServerPeer;

    [JsBinding(Scoped = true, Description = "Subscribes to a channel event. response=true makes the handler answer requests. Returns a subscription id.")]
    public int On(string plugin, string channel, string eventName, bool response, JsCallback handler) =>
        _scope.On(plugin, channel, eventName, response, (message, json) =>
            handler.InvokeJson(response, handler.Parse(json), handler.Parse(NetworkJson.Write(message))));

    [JsBinding(Scoped = true, Description = "Removes a subscription created by on().")]
    public void Off(string plugin, int id) => _scope.Off(plugin, id);
    [JsBinding(Description = "Sends a JSON notification to one peer or everybody.")]
    public void Emit(long target, string channel, string eventName, string dataJson)
    {
        if (!Available()) throw new InvalidOperationException("net.emit: world is not ready");
        _scope.Emit(target, channel, eventName, dataJson);
    }
    [JsBinding(Scoped = true, Description = "Sends a request; callback(error, dataJson) runs on the game thread when the reply arrives.")]
    public void Request(string plugin, long target, string channel, string eventName, string dataJson, int timeoutMs, JsCallback callback)
    {
        if (!Available()) throw new InvalidOperationException("net.request: world is not ready");
        _scope.Request(plugin, target, channel, eventName, dataJson, timeoutMs, (error, data) => callback.Invoke(error, data));
    }
    public override void Release(string plugin) => _scope.Release(plugin);
    public override void Dispose() => _scope.Dispose();
}
