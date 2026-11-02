# Networking: two payloads, one pipe

> Status: **draft**. Reflects the decisions taken in discussion (frames on `ZPackage`, server-origin
> impersonation check, proxy-to-owner player state). Expect edits until we finish the trust model.

Dev-facing. Everything crossing processes goes through the same routed channel layer; what differs
is the **payload format**, chosen by what the message is for.

## 1. Envelope — for messages, events, requests

JSON, one shape for everything:

```json
{
  "v": 1,
  "kind": "event",            // event | request | reply
  "channel": "shop:buy",
  "from": "shop",             // sending plugin
  "ts": 1730000000000,
  "id": 42,                   // request/reply correlation only
  "data": { }                 // any JSON value
}
```

```ts
// receive events + requests
Messaging.handle<BuyRequest>("shop:buy", (envelope) => {
    if (envelope.kind === "request") {
        Messaging.reply<BuyReply>(envelope, { ok: true, balance: 120 });
    }
});

// fire an event
Messaging.send<StockUpdate>("shop:stock", { item: "Coins", amount: 500 });

// ask a question; the answer may never come
Messaging.request<BalanceQuery, BalanceReply>(
    "shop:balance",
    { player: 123 },
    (reply) => log(`balance ${reply.balance}`),
    { timeoutMs: 3_000, onTimeout: () => log("no answer") }
);
```

Or declare the whole wire once:

```ts
const wire = defineWire<{
    "shop:buy": { request: BuyRequest; reply: BuyReply };
    "shop:stock": { event: StockUpdate };
}>();

wire.send("shop:stock", { item: "Coins", amount: 500 });
wire.request("shop:buy", { item: "Coins", amount: 10 }, (reply) => log(reply.balance));
wire.handle("shop:stock", (envelope) => log(JSON.stringify(envelope.data)));
```

### `await` is supported

`request` returns a promise. The host pumps the engine's continuation queue on every tick, so a
reply that arrives later resumes your `async` function on a following tick:

```ts
async onLoad() {
    const reply = await Messaging.request<BalanceQuery, BalanceReply>("shop:balance", { player: 1 });
    log(`balance ${reply.balance}`);
}
```

Two things worth knowing:

- `setTimeout`/`setInterval` are host timers driven by the tick, so `await`ing a timer is fine but
  has tick granularity (~200 ms by default).
- Hooks that return a promise are fire-and-forget: the host does not wait for them. Errors inside
  them are not attributed to the hook — log them yourself.

Properties:

- `data` is JSON — nested objects are fine, but keep it small (< 1 KB target).
- Requests are fire-and-forget on the wire: no acknowledgement, no retry. The 5 s default timeout
  exists so a missing peer cannot leak handlers.
- Envelopes are ordered per channel only as far as the transport keeps them; do not rely on
  cross-channel ordering.
- Unknown/foreign messages are ignored (`v`/`channel` check), so a plugin can never crash on
  someone else's payload.

## 2. Frame — for state sync

Binary, base64-encoded so it travels through the string transport unchanged:

```
[u8 version][u16 count]
per entity: [u16 id][u8 mask][f32 x?][f32 y?][f32 z?][f32 rotY?][u8 flags?]
mask bits: 1 x, 2 y, 4 z, 8 rotY, 16 flags
```

```ts
// server, 5 Hz on a channel nobody else touches
const deltas = entities.map((entity) => diffEntity(previous.get(entity.id)!, entity)).filter(Boolean);
if (deltas.length > 0) {
    Networking.sendFrame("npc:frame", encodeFrame(deltas));
}

// client
Networking.handleFrame("npc:frame", (frame, sender) => {
    for (const entity of decodeFrame(frame)) applyToLocalVisual(entity);
});
```

Measured cost, 2 000 runs, 100 entities with x/y/z:

| | per entity |
|---|---|
| JSON (`Networking.send`) | ~32 B |
| frame + base64 (`sendRaw`) | 15 B raw → 20 chars |

The gap widens with more fields because JSON repeats field names per entity, while a frame carries
them once in a bitmask.

## What the community does (and where we differ)

Source: [Valheim-Modding wiki — RPC Introduction and Example](https://github.com/Valheim-Modding/Wiki/wiki/RPC-Introduction-and-Example).

- Mods register a handler on **both** sides for every method name, including a no-op "mock" on the
  sender. Our `request` does this implicitly (`listen` before send); plain `send` does not, because
  measured behaviour shows the sender can serialise without a local handler — it just cannot
  *receive* on that channel.
- Payloads are `ZPackage`s, which serialise `bool/byte/byte[]/int/long/float/string/Vector3/…`
  directly. We currently send strings, which costs base64 on binary frames — switching the frame
  channel to `ZPackage` is the obvious next optimisation.
- `Everybody` is `0L`; `ZRoutedRpc.instance.GetServerPeerID()` addresses the server.
- **Server announcements are not faked chat**: the server RPCs a client handler that calls
  `Chat.instance.AddString("Server", text, Talker.Type.Shout)`. Our earlier synthetic-`UserInfo`
  approach was wrong and is gone (`ChatAgent` does it the documented way).
- Validation happens on the server: resolve the peer from `sender`, then check `m_adminList` or
  game state before acting. Our player commands follow that shape implicitly by only acting on the
  resolved owning peer.

## Which one

| Need | Use |
|---|---|
| Anything with a name, a type, an intent (`"buy"`, `"quest:accept"`) | envelope |
| Positions/rotations/flags for objects you simulate | frame |
| Per-object persistent data | ZDO data (`spawn.set/get`), not either of these |
| Existence of an entity | a real ZDO (`spawn.create`), not either of these |

## Trust

The transport is open to every peer, so the mod filters what it accepts (decision: fix
impersonation only, for now):

- envelopes carry `role` (`server` | `client`) from the sender,
- a message claiming `role: "server"` must arrive from the peer flagged `m_server` in the peer
  list (`net.serverPeer()`), otherwise it is dropped and logged as spoofed,
- a process ignores messages it sent itself (`origin` == `env.instance()`).

Not yet implemented, and the reason this section is a draft: per-channel direction rules
("only the server may send this"), payload/type identity, and inbound rate limiting. BreakoutNet
is the reference design for those.

## Rules

1. **Never send on the tick.** Decide on the tick, send on change.
2. **Deltas only.** `diffEntity` drops values that moved less than the step (1 cm default).
3. **Cap the rate.** 5 Hz is plenty for NPC walk cycles; 10 Hz is the practical ceiling.
4. **Late joiners need a snapshot.** Envelopes are events; a client that joins later missed them.
   Send full state on `onPlayerJoin` (or use ZDO data, which the game already syncs to joiners).
5. **Validate on receive.** `decodeFrame` throws on a foreign frame by design; wrap handler bodies
   in try/catch or let the host disable the plugin (it does, with a log line).
6. **Prefer the game's replication.** If the thing is a world object, a ZDO is cheaper than any
   custom sync loop, because it is interest-managed and saved.
