---
title: C# RPC bus
---

# C# RPC bus

`ValheimJS.Utils.RpcBus` multiplexes typed C# messages over one routed RPC. Both peers must run a compatible ValheimJS version and use matching channel, event and payload types. Names are case-sensitive. The plugin installs the bus as the network starts; call its methods on the Unity thread. Handlers run on the routed RPC dispatch thread. The returned task can be awaited; do not access Unity objects from a continuation unless it is on Unity's main thread.

```csharp
using ValheimJS.Utils;

// Retain the registration and dispose it when its owner is destroyed.
IDisposable registration = RpcBus.Channel("my_mod.controller")
    .On<Ping, Pong>("ping", (ctx, ping) => new Pong { Value = ping.Value });

Pong reply = await RpcBus.Send<Ping, Pong>(peerId, "my_mod.controller", "ping", new Ping { Value = 42 });
RpcBus.Notify(peerId, "my_mod.controller", "notice", new Notice { Text = "hello" });
```

Alternatively annotate an instance and retain `RpcBus.Register(instance)` until it should stop receiving messages:

```csharp
[RpcChannel("my_mod.controller")]
class Controller
{
    [RpcEvent("ping")]
    private Pong Ping(RpcContext context, Ping message) => new Pong { Value = message.Value };

    [RpcEvent("notice")]
    private void Notice(Notice message) { /* handle notification */ }
}
```

Handlers are synchronous and accept one payload, optionally preceded by `RpcContext`. A non-void handler answers requests; a void handler receives notifications. Multiple notification listeners are allowed, but only one responding handler may register for a channel/event. Dispose registrations to remove them. Requests require a specific peer, default to a 30-second timeout, and accept a timeout and cancellation token. Notifications can target a peer or `ZRoutedRpc.Everybody`; they have no delivery confirmation. A timeout does not undo work already performed remotely.

Payloads use `DataContractJsonSerializer`: use compatible serializable DTOs on both ends. Payloads are limited to 256 KiB and 1,024 requests may be pending. Missing handlers and handler failures produce `RpcRemoteException`; local cancellation, timeout, and network shutdown fail the task separately. Late and mismatched responses are ignored. `RpcContext.Sender` is a routed peer ID; handlers must enforce their own authorization and validate message contents.

## JavaScript binding

The `net` binding (`NetBindings`) registers on the same bus but uses the raw-byte paths
(`RpcBus.Add`, `RpcBus.SendRaw`, `RpcBus.NotifyRaw`) with UTF-8 JSON, so JS peers and C# peers can
interoperate by using a `string` payload DTO on the C# side. Incoming handlers run synchronously on
the routed call; outgoing request completion is marshalled to the game thread through the host
queue before the JS callback runs.
