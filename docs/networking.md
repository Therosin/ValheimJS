---
title: RPC networking
description: Send events and request replies between ValheimJS peers.
---

`Rpc` gives JavaScript plugins named channels and events over Valheim's routed RPC. A channel is a
logical namespace, not a separate socket. Both peers need ValheimJS and matching event and
payload conventions.

`Core/Networking.cs` owns channels, registration scopes, dispatch adaptation, request completion,
and cleanup over `Utils.RpcBus`. The JS binding only adapts JS values and callbacks; the SDK only
shapes calls. This is a socket-style event API, not a Socket.IO wire protocol or a TCP socket.

## Native services

`Plugin` creates one process-owned `Networking` service and pumps its completion queue on the
Unity thread. Services open their own scopes and dispose them when they stop. JS binding scopes
additionally release registrations and cancel pending transport requests on plugin unload.

```csharp
// network is the shared ValheimJS.Core.Networking instance supplied by the host.
using var scope = network.OpenScope();
var channel = scope.Channel("my_mod.controller");
channel.On<string>("notice", (message, text) => { /* validate message.Sender */ });
channel.Handle<int, int>("double", (message, value) => value * 2);
channel.Emit(peerId, "notice", "hello");
```

`OnJson`, `HandleJson`, `EmitJson` and `RequestJson` accept JSON text for consumers such as
binding adapters. Requests finish through the main-thread pump; a scope's disposal suppresses
late callbacks. `Networking/CommandService.cs` receives private command submissions on
`valheimjs:commands`, resolves the caller from the actual peer and sends replies only to that
caller. The native registry and execution lifecycle live in `Core/ChatCommands.cs`.
See [Chat commands](guides/chat-commands.md).

`Networking/ChatService.cs` is retained but dormant: startup never constructs it. Its existing
code listens for `valheimjs:chat/message`, accepts announcements from the server peer and shows
them as a `Server` chat line with the shout style. It does not parse or execute commands.

## JavaScript events

```ts
import { definePlugin, Rpc } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",

    setup() {
        const shop = Rpc.channel("my_mod.shop");

        // Receive a notification. Keep the returned function to unsubscribe.
        const off = shop.on<{ coins: number }>("stock", (data, meta) => {
            log(`peer ${meta.sender} has ${data.coins} coins`);
        });

        // One synchronous responder per channel/event on each peer.
        shop.handle<{ amount: number }, { ok: boolean }>("buy", (data, meta) => {
            // Check meta.sender's permission and validate data before changing state.
            return { ok: data.amount > 0 };
        });

        shop.emit("stock", { coins: 50 });            // broadcast
        shop.emit("stock", { coins: 50 }, peerId);    // one peer
        void off;
    }
});
```

To ask one peer for a reply:

```ts
const reply = await Rpc.channel("my_mod.shop").request<{ amount: number }, { ok: boolean }>(
    peerId, "buy", { amount: 10 }, 5000
);
```

The timeout is milliseconds and defaults to 30 000. A missing responder, handler failure,
disconnected network or timeout rejects the promise. A timeout does not undo remote work.
Notifications have no delivery confirmation. Handlers can be registered before networking starts;
sending requires `Rpc.available()`.

`Rpc.serverPeer()` returns the server peer id when known, or zero. The sender is a routed peer id,
not proof that the peer is authorised for a given action.

## Rules

- **Responders are synchronous.** The routed call expects its reply on the same call. Return a
  value; a promise is rejected with a clear error.
- **JSON only**, matching types on each side. The payload limit is 256 KiB.
- **Multiple listeners, one responder** per channel/event on each peer.
- **Unsubscribe** by calling the function returned from `on`/`handle`; plugin unload removes every
  registration it owns.

The C# API and transport lifecycle are described in [rpc-bus.md](rpc-bus.md). Prefer Valheim's ZDO
replication for persistent world state and late joiners; bus notifications are transient. Joining
peers may not yet have registered their handlers, so use an application-level readiness handshake
where delivery at join time matters.

## Explicit binary sync

`Core/NetworkSync.cs` owns the compact frame codec, delta calculation and binary stream scopes.
It shares RpcBus's byte transport, bypassing the JSON event payload conversion. `Sync.send` crosses
the JS boundary as JSON once, then transmits native bytes: there is no JSON/base64 frame on the
wire. `encodeFrame`/`decodeFrame` in `scripting/lib/sync.ts` are thin host-backed inspection helpers.

```ts
import { Sync } from "../../../sdk/index";

const off = Sync.on("my_mod.markers", (entities, meta) => {
    // Validate meta.sender before applying transient presentation state.
});
Sync.send("my_mod.markers", [{ id: 7, x: 10, z: 20 }], peerId);
```

Each frame has a version byte, a big-endian u16 entity count, and partial entities with u16 ids,
field masks, optional f32 x/y/z/rotY values and optional u8 flags. Ids and flags must fit their
wire ranges; duplicate ids, invalid masks, non-finite values, truncation and trailing bytes are
rejected. The maximum frame is 256 KiB. Float precision is f32; ids belong to the caller's stream.

This is a compact payload path over the same routed RPC delivery, not an unreliable socket or
a priority transport. Sending is explicit: no automatic per-tick broadcast, state cache,
authority policy, persistence or late-join replay. Use real ZDOs for replicated world objects;
use binary streams sparingly for transient state that the game does not already replicate.
