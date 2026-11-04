---
title: RPC networking
description: Send events and request replies between ValheimJS peers.
---

`Rpc` gives JavaScript plugins named channels and events over Valheim's routed RPC. A channel is a
logical namespace, not a separate socket. Both peers need ValheimJS 2 and matching event and
payload conventions.

Registration, dispatch, request completion and cleanup are host-owned; the JS facade only encodes
JSON and returns unsubscribe functions.

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
