---
title: API reference
---

# API reference

`ctx.host.api` is built by the C# binding classes. The TypeScript shapes are **generated** from
those classes (`scripting/sdk/api/generated.ts`), so this page describes intent while your editor
has the exact signatures.

| Namespace | Purpose |
|---|---|
| [`game`](#game) | process side, world identity, live snapshot |
| [`world`](#world) | object counts, spawning, per-object data |
| [`net`](#net) | routed RPC channels |
| [`sync`](#sync) | explicit transient binary frames |
| [`ChatCommands`](#chatcommands) | private command registry and replies |
| [`AccountService`](accounts.md) | account queries and publisher-owned shared data |
| [`PermissionService`](accounts.md#roles-and-permissions) | live role and permission queries |
| [`input`](#input) | keyboard bindings and phase callbacks |
| [`fs`](#fs) | sandboxed files |
| [`db`](#db) | per-plugin document collections |

Availability is declared per binding. `input` is client-only; everything else installs on both
sides unless a method says otherwise. Availability is decided at startup — use `game.isServer()`
for live authority checks.

## `game`

```ts
game.side(): "server" | "client"   // fixed at startup
game.dedicated(): boolean          // server-process branch
game.isServer(): boolean           // confirmed live authority; false before session
game.isClient(): boolean           // client-process branch, including player hosts
game.sessionReady(): boolean      // ZNet exists; not world/player readiness
game.singlePlayer(): boolean
game.peers(): number
game.instance(): string            // stable per process start
game.worldName(): string | null
game.day(): number
game.dayFraction(): number
game.timeOfDay(): "day" | "night" | "unknown"
game.snapshot(): GameSnapshot
```

```ts
interface GameSnapshot {
    world: string | null;
    peers: number;
    day: number;
    dayFraction: number;
    timeOfDay: "day" | "night" | "unknown";
}
```

Values are read live. Until the world loads they are `null` / `0` / `"unknown"`; that is normal
startup, not an error.

## `world`

```ts
world.zdoCount(): number
world.zdoAvailable(): boolean
world.spawn(prefab: string, x: number, y: number, z: number): string | null
world.destroy(handle: string): void
world.alive(handle: string): boolean
world.prefabs(query: string, limit: number): string[]   // limit 0 = all
world.exists(prefab: string): boolean
world.getData(handle: string, key: string): string | null
world.setData(handle: string, key: string, value: string): void
```

`spawn` instantiates an **existing** networked prefab and returns a session handle. The game
replicates the ZDO, so clients see it without extra work. `setData` writes to that ZDO: it is
replicated with the object and saved with the world. Handles are session-scoped — persist your own
ids if you need them across restarts.

`zdoCount()` is the number of live world objects. `zdoAvailable()` is false if the server build
hides the table; check it before treating the number as authoritative.

## `net`

```ts
// Low-level
net.available(): boolean
net.everybody(): number
net.serverPeer(): number
net.on(channel, event, response, handler): number   // subscription id
net.off(id): void
net.emit(target, channel, event, dataJson): void
net.request(target, channel, event, dataJson, timeoutMs, callback): void

// Preferred: channel facade from the SDK
Rpc.available(): boolean
Rpc.serverPeer(): number
const channel = Rpc.channel("my_mod.shop");
channel.on<T>(event, (data, meta) => void): () => void
channel.handle<T, R>(event, (data, meta) => R): () => void
channel.emit<T>(event, data, target?: number): void
channel.request<T, R>(target, event, data, timeoutMs?): Promise<R>
```

A channel is a logical namespace, not a separate socket. Registration, dispatch, request
completion and cleanup are host-owned; the facade only encodes JSON and returns unsubscribe
functions. Handlers receive `(data, meta)` where `meta` is `{ sender, channel, event }`.

Responders run synchronously on the same routed call and must return a value. Notifications are
fire-and-forget. The timeout is milliseconds (default 30 000); a missing responder, handler
failure, disconnect or timeout rejects the promise. Payloads are JSON and limited to 256 KiB. The
sender is a routed peer id, not proof of authorisation — check permission yourself.

See the [networking guide](../networking.md).

## `sync`

```ts
import { Sync } from "../../../sdk/index";
Sync.on(stream, (entities, meta) => void): () => void
Sync.send(stream, entities, target?: number): void
```

`SyncEntity` has a u16 `id` and optional f32 `x`, `y`, `z`, `rotY` and u8 `flags`. The native
`Core/NetworkSync` service owns framing, decoding, validation and subscription cleanup. Explicit
sends use raw bytes over RpcBus; omitted `target` broadcasts. There is no automatic cadence,
late-join replay or authority policy; validate the sender. Prefer ZDOs for world replication.

The optional `scripting/lib/sync.ts` helpers delegate `encodeFrame`, `decodeFrame`, `diffEntity`
and `quantise` to the host. Base64 encoding is for inspection/storage; `Sync.send` sends bytes.
See [Networking](../networking.md#explicit-binary-sync).

## `ChatCommands`

Use the SDK's `ChatCommand` base class and `ChatCommands.registerCommand(instance)` in plugin
setup. The native registry owns routes, invocation lifetimes and unload cleanup. Registration
returns an id; `ChatCommands.unregisterCommand(id)` removes it and cancels its invocations.
`ChatCommands.list()` returns registered metadata. `roles` requires any listed role; `permissions`
requires every listed permission, checked through the native PermissionService.
Optional `help` is text or a synchronous `(query: string) => string` callback. Players request it
with `!help command [query]`; `!help` lists accessible commands. See the
[command help guide](chat-commands.md#help-text-and-contextual-help).

Commands use `!name` or `!category:name`; replies and errors stay private. Async validation and
execution are supported. See [Chat commands](chat-commands.md) for parsing rules and examples.

## `AccountService` and `PermissionService`

Both bindings install on either process branch; their queries and writes require a live session
with server authority. Connected clients do not receive replicas. Use these SDK facades:

```ts
import { AccountService, PermissionService } from "../../../sdk/index";

AccountService.available(): boolean
AccountService.get(id): Account | null
AccountService.findByPlatform(platformId): Account | null
AccountService.fromPeer(peerId: string): Account | null
AccountService.getData<T>(id): T | null
AccountService.readPublishedData<T>(id, publisher): T | null
AccountService.setData(id, object): void
AccountService.deleteData(id): void
PermissionService.available(): boolean
PermissionService.hasRole(id, role): boolean
PermissionService.hasPermission(id, permission): boolean
```

`getData` reads your publisher namespace; `readPublishedData` can read another publisher's
small shared record. Writes/deletes always use the calling plugin's namespace. The low-level
`ctx.host.api.AccountService.setData` takes JSON text; the SDK facade serializes the object.
Keep detailed state in your own database and publish lookup links when other plugins need it.

Profile and policy editing are native management operations, not JS binding methods. Default
roles are `admin` (`*`), `staff` and `player`; new access records use `player`. See
[Accounts](accounts.md) and [Management](management.md).

## `input`


Client-only. Actions are namespaced by the calling plugin, so two plugins can both use `"toggle"`
without colliding.

```ts
input.bind(action: string, key: string): void        // "F5", "LeftShift", …
input.unbind(action: string): void
input.on(action, "press" | "hold" | "release", handler): void
input.off(action, phase): void
input.state(action): KeyState | null
input.rawState(key): KeyState | null
input.handledAny(): boolean
input.clear(): void
```

```ts
interface KeyState {
    isPressed: boolean;
    wasPressedThisFrame: boolean;
    wasReleasedThisFrame: boolean;
}
```

Return `true` from a handler to consume the input for that frame; Valheim's named `ZInput` button
reads are masked when their key was claimed. This does not suppress direct low-level key reads or
every UI path, and does not ignore text fields automatically. Bindings and handlers are removed on
reload and on plugin unload.

## `fs`

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

## `db`

One document store per plugin: `<data>/<plugin>/db/<collection>/<id>.json`, written atomically.
The low-level namespace is available; the `Db` facade is usually nicer.

```ts
const quests = database().use<Quest>("quests");

quests.save({ id: "intro", title: "Wake up", done: false });
quests.insert({ id: "hv", title: "Hugin visits", done: false });  // throws if id exists
quests.get("intro");
quests.has("intro");
quests.update("intro", { done: true });
quests.upsert(id, () => ({ ...init }), (doc) => ({ visits: doc.visits + 1 }));
quests.all();
quests.keys();
quests.count();
quests.find((quest) => !quest.done);            // predicate runs in the script
quests.first((quest) => quest.done);
quests.updateWhere((quest) => !quest.done, () => ({ done: true }));
quests.remove("intro");
quests.clear();
```

Rules and costs:

| Rule | Why |
|---|---|
| Every document needs `id: string \| number` | it is the filename; ids are escaped, never parsed |
| Use stable ids — `SteamID64`, prefab name, quest slug | object references and timestamps make lookups useless |
| `all()`/`find()` are cheap per call but read the whole collection | avoid them in `onServerTick` |
| `save()` writes one small file | durable immediately; batch instead of saving every tick |
| Data is per plugin, per machine | write authoritative state on the server |

`find` and `updateWhere` loop in the host; only your predicate and patch run in the script. A patch
must return the whole document (the facade merges for you).

## Host helpers

```ts
ctx.host.log(level, message)     // "info" | "warn" | "error"
ctx.host.config(key)             // string | null
ctx.host.storage.get(key)        // string | null
ctx.host.storage.set(key, value)
ctx.host.now()                   // epoch ms
ctx.host.info()                  // { name, version, api, dir }
```

`log` writes to the shared log and `ValheimJS/logs/<name>.log`.

## SDK helpers

```ts
context(): PluginContext          // active entry/setup/hook/host-callback context
log / warn / error(message)
use(name, (exports, ctx) => …)
serverOnly(callback) / clientOnly(callback) / authorityOnly(callback) // immediate
serverHandler(handler) / clientHandler(handler) / authorityHandler(handler) // event wrappers
```

The immediate gates return the callback result or undefined. They never defer execution.
See [Process branches](process-branches.md) for player-host semantics and examples.

## Identity

Use [AccountService](accounts.md) to resolve verified platform identities to stable account ids.
`fromPeer(String(uid))` maps a live connection to its account; the event peer `uid` is not stable
across sessions. C# game adapters use `ValheimJS.Core.PlayerIdentity`. Player stats/inventory APIs
remain future work; profile and policy editing are host-owned.

## Not available yet

Do not work around these by guessing:

- player stats, inventory and teleporting APIs
- JS profile/policy editing (use the native management commands)
- item catalogue access and item grants
- general chat output outside command replies
- intercepting or cancelling damage
- creating new prefabs, items or assets from script

The API grows by adding a `[JsBinder]` class, regenerating declarations and documenting it — not by
reaching around the bridge.
