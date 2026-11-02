# API reference

Everything below is reachable from `ctx.host.api` inside any handler. Types are in
`scripting/sdk/api/` — the same names you see in your editor.

## Hooks

| Hook | Payload | When |
|---|---|---|
| `onLoad(ctx)` | — | once, after dependencies load |
| `onUnload(ctx)` | — | before the plugin is dropped |
| `onServerTick(ctx, world)` | `WorldSnapshot` | ~1 Hz while the server runs |
| `onChat(ctx, payload)` | `{ channel, senderId, text }` | a chat message reaches the server |
| `onPlayerJoin(ctx, payload)` | `{ uid, name }` | a peer joined |
| `onPlayerLeave(ctx, payload)` | `{ uid, name }` | a peer left |
| `onDamage(ctx, payload)` | `DamagePayload` | a hit was applied (read-only) |

```ts
interface DamagePayload {
    target: string;              // who was hit
    targetIsPlayer: boolean;
    attacker: string;            // "" for environmental damage
    attackerIsPlayer: boolean;
    damage: number;              // applied after resistances
    targetHealth: number;
    targetMaxHealth: number;
    hitType: string;             // "Slash" | "Pierce" | "Blunt" | "Fire" | …
}
```

`onDamage` fires **after** the hit landed, so you observe combat; you cannot cancel or scale a hit
from it yet.

`channel` is one of `normal`, `shout`, `whisper`, `ping`. `senderId` is the peer uid — compare it
with `players.list()[i].uid`; there is no separate player id.

## `env`

```ts
env.side(): "server" | "client"     // fixed at startup
env.dedicated(): boolean            // headless dedicated server
env.isServer(): boolean             // dedicated OR hosting a world (live)
env.isClient(): boolean             // not headless — a hosting player counts
env.singlePlayer(): boolean
env.peers(): number
```

Use `isServer()` for authority decisions and `dedicated()` only when you specifically mean
"headless". `side()` is what selected your `server/` or `client/` bundle at load time.

## `world`

```ts
world.snapshot(): WorldSnapshot
world.name(): string | null
world.peers(): number
world.day(): number
world.dayFraction(): number      // 0..1 through the current day
world.timeOfDay(): "day" | "night" | "unknown"
```

```ts
interface WorldSnapshot {
    world: string | null;
    peers: number;
    day: number;
    dayFraction: number;
    timeOfDay: "day" | "night" | "unknown";
}
```

Values are read live on each call. Until the world finishes loading they read as `null` / `0` /
`unknown` — that is normal during startup, not an error.

## `players`

```ts
players.list(): PlayerInfo[]
players.count(): number
players.names(): string[]
players.hostname(uid: number): string | null
players.isAdmin(uid: number): boolean
players.steamId(uid: number): string | null      // SteamID64 digits, stable across sessions
players.platformId(uid: number): string | null   // raw "Steam_7656…" from the peer socket
```

```ts
interface PlayerInfo {
    uid: number;
    name: string;
    server: boolean;
    admin: boolean;
    steamId: string | null;     // stable identity for persistence (name is not)
    platformId: string | null;
    characterId: string;            // "" until the player has spawned
    position: { x: number; y: number; z: number };
}
```

`isAdmin` reflects the server's own admin list, so it stays correct without you maintaining a
list of names.

## `chat`

```ts
chat.send(text: string): void                 // every connected peer
chat.sendTo(uid: number, text: string): void  // one peer
chat.available(): boolean                     // false before networking is up
```

Server messages go out as normal chat addressed to everyone. Clients apply their own text
filters and permissions, so treat delivery as **best effort**; do not use chat as a control
channel that must be acknowledged. Sending in the middle of `onChat` is fine.

## `zdo`

```ts
zdo.count(): number
zdo.available(): boolean
```

`count()` is the number of live world objects (props, creatures, structures). Useful for
sizing work or spotting runaway spawns. `available()` is false if the server build hides the
table; check it before treating the number as authoritative.

## `fs` (sandboxed)

Every path resolves inside `ValheimJS/data/<your-plugin>/`. Escaping it throws.

```ts
fs.root(): string
fs.exists(path: string): boolean
fs.read(path: string): string | null      // null when missing
fs.write(path: string, content: string): void   // creates parent folders
fs.append(path: string, content: string): void
fs.list(path: string): string[]           // names, not full paths
fs.mkdir(path: string): void
fs.delete(path: string): void             // file, or folder recursively
fs.join(a: string, b: string): string
```

```ts
const file = "state/seen.json";
const previous = JSON.parse(fs.read(file) ?? "{}");
previous[player.uid] = player.name;
fs.write(file, JSON.stringify(previous, null, 2));
```

## `net` / `Networking`

Messaging between the server process and connected clients. Payloads are JSON, so any shape
works; the channel name is namespaced and both sides must listen before anything arrives.

```ts
Networking.handle("channel", (message) => { ... });   // listen + callback (idempotent)
Networking.on("channel", handler);                    // alias
Networking.off("channel", handler);

Networking.send("channel", data);                     // every peer
Networking.sendTo(uid, "channel", data);              // one peer
Networking.emit("channel", data, uid?);               // send / sendTo

Networking.channels();                                // channels this process listens on
```

```ts
// plugins/netping/server/index.ts
Networking.handle("hello", (message) => {
    log(`client ${message.sender} says ${JSON.stringify(message.data)}`);
});

setInterval(() => {
    Networking.send("server-time", { day: ctx.host.api.world.day() });
}, 30_000);
```

```ts
// plugins/netping/client/index.ts
Networking.handle("server-time", (message) => {
    log(`server says ${JSON.stringify(message.data)}`);
    Networking.send("hello", { from: "client" });
});
```

Notes:

- `message.sender` is the peer uid on the server; `0` when it came from the server.
- Sending is fire-and-forget. There is no delivery receipt, ordering guarantee across channels, or
  retry — design for idempotence.
- The low-level form is `ctx.host.api.net.*` (`send`, `sendTo`, `listen`, `channels`, `available`,
  `side`), which takes a JSON **string**. Use `Networking` unless you need that control.
- Listeners registered during `onLoad` are queued and attached once the game's network layer
  exists — you do not need to delay your calls.

## `ctx.db` (per-plugin database)

Every plugin gets its own document database. One JSON file per document, written atomically;
collections are folders under `<plugin data>/db/`. Nothing is shared between plugins.

```ts
interface Quest { id: string; title: string; done: boolean }

const quests = ctx.db.use<Quest>("quests");   // same as ctx.db.collection("quests")

quests.save({ id: "intro", title: "Wake up", done: false });  // insert or replace
quests.insert({ id: "hv", title: "Hugin visits", done: false }); // throws if id exists
quests.get("intro");                       // Quest | null
quests.has("intro");                       // boolean
quests.update("intro", { done: true });    // shallow merge, keeps id; null if missing
quests.upsert(id, () => ({ ...init }), (doc) => ({ visits: doc.visits + 1 }));  // read-modify-write
quests.all();                              // Quest[]
quests.keys();                             // string[]
quests.count();
quests.find((q) => !q.done);               // predicate runs in the script
quests.first((q) => q.done);
quests.updateWhere((q) => !q.done, () => ({ done: true }));
quests.remove("intro");
quests.clear();
```

Shorthands: `ctx.db.get(coll, id)`, `ctx.db.set(coll, doc)`, `ctx.db.all(coll)`,
`ctx.db.count(coll)`, `ctx.db.collections()`.

Rules and costs:

| Rule | Why |
|---|---|
| Every document needs `id: string \| number` | it is the filename; ids are opaque, escaped, and never parsed |
| Use stable ids — `steamId`, prefab name, quest slug | object references and timestamps make lookups useless |
| `all()`/`find()` read the whole collection from memory | cheap per call, but avoid it in `onServerTick` |
| `save()` writes one small file | durable immediately; batch instead of saving every tick |
| Data lives per plugin, per machine | client-side plugins get client-local data — write player state on the server |

Player profiles keyed by identity:

```ts
onPlayerJoin = serverOnly((ctx, player) => {
    const steamId = ctx.host.api.players.steamId(player.uid) ?? String(player.uid);
    const profiles = ctx.db.use<Profile>("profiles");
    const profile = profiles.upsert(steamId, () => ({ id: steamId, visits: 0 }), (doc) => ({ visits: doc.visits + 1 }));
});
```

## Host helpers

```ts
ctx.host.log(level, message)     // "info" | "warn" | "error"
ctx.host.config(key)             // string | null
ctx.host.storage.get(key)        // string | null
ctx.host.storage.set(key, value)
ctx.host.now()                   // epoch ms
ctx.host.info()                  // { name, version, api, dir }
```

`log` writes to both the shared server log and `ValheimJS/logs/<name>.log`.
`storage` is a small key/value store kept next to your plugin; prefer `fs` for anything bigger.

## Not available yet

These are common requests that the current API cannot express. Do not work around them by
guessing:

- **Creating or modifying world objects** (spawning, item grants, structure edits). Read-only
  counts only.
- **Client↔server RPC** for custom UI or client mods. Side-aware code (`server/`, `client/`,
  `serverOnly`, `clientOnly`) works, but the two processes cannot call each other directly yet.
- **Intercepting or cancelling network RPCs.**
- **Cancelling or scaling damage.** `onDamage` observes hits after they apply; there is no
  interception point that can change or veto them yet.
- **Death events.** Derive them from `onDamage` (`targetHealth <= 0`) at your own risk: the
  server may apply several hits in one frame.
- **Per-player persistent identity across sessions** beyond the uid the server reports for the
  current session.

If a plugin needs one of these, the right move is to say so — the API surface grows by adding a
binding, not by reaching around it.
