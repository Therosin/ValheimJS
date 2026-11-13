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
| [`players`](#players) | local player, lookups and the online roster |
| [`nav`](#nav) | navigation volume stats and debug overlay |
| [`draw`](#draw) | world-space debug/gameplay primitives |
| [`net`](#net) | routed RPC channels |
| [`sync`](#sync) | explicit transient binary frames |
| [`ChatCommands`](#chatcommands) | private command registry and replies |
| [`AccountService`](accounts.md) | account queries and publisher-owned shared data |
| [`PermissionService`](accounts.md#roles-and-permissions) | live role and permission queries |
| [`input`](#input) | keyboard bindings and phase callbacks |
| [`npc`](#npc) | scripted NPCs, perception store, intents and memory |
| [`signals`](#signals) | host-sampled observed conditions |
| [`ai`](#ai) | YAML behaviour trees for scripted AI |
| [`ui`](#ui) | client-side YAML panels with bound values and events |
| [`fs`](#fs) | sandboxed files |
| [`db`](#db) | per-plugin document collections |

End-to-end NPC examples live in [NPCs and AI](npc-ai.md).

Availability is declared per binding. `input` and `ui` are client-only; everything else installs
on both sides unless a method says otherwise. Availability is decided at startup — use
`game.isServer()` for live authority checks.

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
game.peerList(): PeerInfo[]        // connected peers, names and ids
```

```ts
interface PeerInfo {
    uid: number;        // routed peer id for Rpc targets
    name: string;
    playerId: string;   // text to avoid 64-bit precision loss
    server: boolean;
    ready: boolean;
}
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
world.idOf(handle: string): string | null               // stable ZDO id for a spawn handle
world.charactersNear(x, y, z, radius, limit): WorldCharacter[]
world.containersNear(x, y, z, radius, limit): WorldContainer[]
```

`spawn` instantiates an **existing** networked prefab and returns a session handle. It requires
live world authority: on a client process (or before the session exists) it logs a warning and
returns `null`. The game replicates the ZDO, so clients see it without extra work. `setData`
writes to that ZDO: it is replicated with the object and saved with the world.

Handles belong to the plugin that spawned them. `destroy`, `alive`, `getData` and `setData`
ignore another plugin's handle with a warning, so a plugin cannot read, overwrite or delete
objects it did not create. Handles are session-scoped and dropped on plugin unload — persist your
own ids if you need them across restarts.

`zdoCount()` is the number of live world objects. `zdoAvailable()` is false if the server build
hides the table; check it before treating the number as authoritative. Reads (`zdoCount`,
`prefabs`, `exists`) work on clients; only mutations are authority-gated.

`idOf(handle)` returns the ZDO id (`user:counter`) for a spawn handle — the durable reference to
persist when you need to find the object again after a restart.

`charactersNear` and `containersNear` are **read-only proximity scans**, nearest first, `limit 0`
returns everything. Characters include stable ids, prefab, name, position, distance, health, and
`isPlayer`/`isDead`/`isTamed`; containers include id, prefab, position, slot count and item count.
They scan live instances, so treat them as occasional queries (discovery, diagnostics, AI
groundwork), not per-tick work. Mutating contents or stats is not exposed here yet.

```ts
interface WorldCharacter {
    id: string | null; prefab: string | null; name: string;
    x: number; y: number; z: number; distance: number;
    health: number; maxHealth: number;
    isPlayer: boolean; isDead: boolean; isTamed: boolean;
}

interface WorldContainer {
    id: string | null; prefab: string | null;
    x: number; y: number; z: number; distance: number;
    slots: number; items: number;
}
```

## `players`

```ts
players.local(): ValheimPlayer | null        // client (and player host) only
players.get(id: string): ValheimPlayer | null   // routed uid or platform id
players.online(): ValheimPlayer[]            // dedicated server only
```

A `ValheimPlayer` is read-only player state, resolved from the replicated player ZDO:

```ts
interface ValheimPlayer {
    readonly uid: string;          // routed peer id, session-scoped (RPC targets, fromPeer)
    readonly platformId: string | null;   // SteamID64 digits (or raw platform id); null when unknown
    readonly name: string;         // display only, never a key
    readonly isLocal: boolean;     // true only on the process owning that local player
    readonly x: number; readonly y: number; readonly z: number;
    readonly health: number; readonly maxHealth: number;
    readonly isAlive: boolean;
}
```

`online()` is the authority-owned roster: connected peers with a spawned character, plus the host's
local player. Players still loading have no character ZDO and are omitted until they spawn; the
method does not exist on clients. `get` resolves one known player on either side and returns
`null` for unknown/offline ids. Keep `platformId` for durable identity and resolve the account with
`AccountService.fromPeer(uid)`; `name` and `uid` are not durable.

## `npc`

```ts
npc.available(): boolean
npc.register(ctor, { kind, prefab, tickMs?, faction?, perception? }): void
npc.spawn(kind, { position: { x, y, z }, name? }): ValheimBaseNPC | null
npc.despawn(entity): boolean
npc.handles(kind): ValheimBaseNPC[]
npc.perception(entity): NpcView | null
npc.sensed(entity, filter?): NpcSensed[]        // store query; remembered: true includes lost contacts
npc.memory(entity): Record<string, unknown>     // durable plugin-owned memory
npc.setMemory(entity, value): boolean
npcBase(): new (handle: string) => ValheimBaseNPC
```

`ValheimBaseNPC` is a native class (generated declarations):

```ts
readonly id: string; kind: string; name: string;
readonly x, y, z: number; readonly health, maxHealth: number; readonly isAlive: boolean;
moveTo(x, y, z, run: boolean): boolean;   // async intent; status in view.intent
idle(): boolean;                          // wander near home
stop(): boolean;                          // stand still
wait(seconds: number): boolean;
lookAt(x, y, z): boolean;
face(entityId: string): boolean;
state(key): string | null;
setState(key, value): boolean;
removeState(key): boolean;
stateKeys(): string[];
```

Registration is per kind (kind matches `^[a-z0-9_-]{1,32}$`; the vanilla prefab is cloned as
`vjs_npc_<kind>`). `faction` selects the clone's stance (`"Players"` for friendly companions);
`perception` tunes the sensor. Call `npcBase()` only while a plugin context is active, so use a
factory from `setup`:

```ts
function makeWanderer() {
    const Base = npcBase();
    return class Wanderer extends Base {
        onSpawn(view: NpcView) {}
        onRehydrate(view: NpcView) {}
        onSensed(entry: NpcSensed) {}
        onIntent(view: NpcView, intent: NpcIntent) {}
        onTick(view: NpcView) { this.moveTo(5, view.y, 0, false); }
        onDespawn() {}
    };
}

export default definePlugin({
    setup() {
        npc.register(makeWanderer(), {
            kind: "wanderer",
            prefab: "Dverger",
            tickMs: 500,
            faction: "Players",
            perception: {
                radius: 24, max: 12, viewRange: 24, viewAngle: 90,
                lineOfSight: true, objectRadius: 12
            }
        });
    }
});
```

### Perception store

Every NPC keeps a store of recognized entities, updated on its tick. Entries persist after the
entity leaves range so behaviours can remember last-known positions and recognition counts:

```ts
interface NpcSensed {
    id: string; name: string; kind: string; prefab: string | null; faction: string;
    pos: { x: number; y: number; z: number };   // last known
    rel: { x: number; y: number; z: number };   // relative to the NPC
    distance: number; bearing: number;          // bearing is signed from facing
    visible: boolean; inRange: boolean; enemy: boolean;
    isPlayer: boolean; isNpc: boolean; isTamed: boolean; isDead: boolean;
    level: number; isObject: boolean;
    health: number; maxHealth: number;
    firstSeen: number; lastSeen: number;        // seconds; lastSeen -1 when never visible
    seenCount: number;
}
```

`NpcView` adds self data, intent and environment:

```ts
interface NpcView {
    id: string; kind: string; x: number; y: number; z: number;
    health: number; maxHealth: number; isAlive: boolean;
    faction: string; isTamed: boolean; level: number;
    home: { x: number; y: number; z: number };
    intent: NpcIntent | null;
    environment: { day: number; isDay: boolean; biome: string; weather: string | null } | null;
    nearby: NpcSensed[];      // current characters
    remembered: NpcSensed[];  // out-of-range contacts
    objects: NpcSensed[];     // in-range world objects
}
```

Query the store instead of scanning arrays yourself:

```ts
npc.sensed(this, { radius: 12, dir: { x: 0, z: 1 }, angle: 45, kinds: ["player", "creature"] });
npc.sensed(this, { enemy: true, visible: true });
npc.sensed(this, { remembered: true, max: 64 });
```

`dir` defaults to the NPC facing and `angle` is a half-angle. `kinds` accepts `player`, `npc`,
`creature`, `tamed`, `enemy`, `dead`, or an exact kind/prefab/object class. World objects
(`container`, `door`, `bed`, `smelter`, `station`, `item`, `portal`) enter the same store when
`perception.objectRadius > 0`; they appear in `view.objects` and in `npc.sensed` results
(`isObject: true`).

### Durable memory

`npc.memory(entity)` is a plugin-owned JSON object stored in the NPC ZDO (`vjs.memory`, 16 KiB
limit). It survives restarts and is the place for ownership, interaction history and arbitrary
recognition data:

```ts
const memory = npc.memory(this);
const player = npc.sensed(this, { players: true })[0];
if (player) {
    memory.lastPlayer = { id: player.id, name: player.name, at: Date.now() };
    npc.setMemory(this, memory);
}
```

### Intents and hooks

Intents run on the server and report state through `view.intent`:
`{ kind, status: "idle" | "running" | "arrived" | "failed", reason, target, targetId }`.
`onIntent` fires once when an intent settles; AI actions can return `running` while the intent is
still active (`npc.perception(this)?.intent?.status === "running"`). `onSensed` fires once for each
new entity or object added to the store.

Signals sample the same cached store, so predicates are cheap:

```ts
const observer = signals({ intervalMs: 250 });
observer.observe("hunter-threat", () =>
    npc.handles("hunter").some((entity) =>
        npc.sensed(entity, { enemy: true, visible: true, radius: 24 }).length > 0));
observer.on("hunter-threat:Enter", () => log("a hunter has a visible enemy"));
```

Rules:
- **Server authority.** `spawn`/`despawn`, intents, perception and memory writes run on the server;
  clients are viewers. `onTick`/`onSensed`/`onIntent` only run on the server.
- **Persistence.** Entities are ZDOs with `vjs.sim`, `vjs.plugin`, `vjs.kind`, `vjs.name`,
  `vjs.home.*`, `vjs.intent.*` and `vjs.memory`; `setState` writes `vjs.s.<key>`.
- **Cadence.** `tickMs` defaults to 500 and is clamped to 100..5000; the host pump sets the floor.
- **Relations.** `enemy` mirrors the game's faction matrix (`BaseAI.IsEnemy`), including tamed and
  group overrides. `faction` at registration chooses the clone's stance.
- **Limits.** 32 live instances per kind per plugin; perception stores retain memories for 5
  minutes. Combat, inventory and dialogue are not part of this surface.

## `ai`

```ts
ai.compile(yaml, { source?, imports?, warn? }): string
ai.instantiate(tree, { this?, state?, exec?, action?, condition?, random?, senses?, maxJumpsPerTick? }): string
ai.tick(instance, deltaTime?): { status: "success" | "failure" | "running" | "cancelled"; output: unknown }
ai.state(instance): Record<string, unknown>
ai.set(instance, key, value): void
ai.status(instance): "success" | "failure" | "running" | "cancelled"
ai.cancel(instance): void
ai.reset(instance, resetState?): void
ai.release(instance): boolean
ai.unload(tree): boolean
```

YAML behaviour trees with `sequence`, `selector`, `random`, `action`, `condition`, `goto` and
`use`, `execBefore`/`exec`/`execAfter`, `@[state]` references and `out` assignment. The engine
never ticks itself: call `ai.tick` from `onServerTick` or an NPC `onTick`. Compile once and
instantiate per entity; instances keep independent state.

```ts
const tree = ai.compile(yaml);
const entity = npc.handles("wanderer")[0];
const brain = ai.instantiate(tree, {
    this: entity,
    action: (name, c) => name === "find_target"
        ? { status: "success", output: "Dverger" }
        : (entity.moveTo(5, entity.y, 0, true) ? "running" : "failure"),
    exec: (code, c) => { /* the host has no YAML script interpreter */ },
    random: () => Math.random()
});
ai.tick(brain, 0.5);   // "running" until move_to returns success
```

Rules:
- **In-memory imports.** `imports:` in YAML is keyed by alias; `ai.compile` resolves aliases from
  `options.imports` (`{ wander: yamlText }`). The JS bridge never reads YAML from disk.
- **Callback context.** `ctx.args` is a resolved copy; `ctx.state` is copied into JS and written
  back after every `action`/`condition`/`exec` call. `ctx.this` is the value passed to
  `instantiate`. Mutating `ctx.state.x` in a callback is visible to later nodes and ticks.
- **Actions.** Return `"success"`, `"failure"`, `"running"` or `{ status, output }`; `output` is
  assigned to `out` on success only. `undefined`/`null` means success. `condition` returns boolean.
- **`exec` snippets.** The engine passes snippet text to `exec`; there is no embedded interpreter.
- **Goto scope and limits.** `goto` targets nodes in the same YAML file; imported trees keep their
  own id scope. Jumps are limited per tick (`maxJumpsPerTick`, default 64).
- **Scoping and limits.** Trees and instances are plugin-scoped ids (`tree1`, `brain1`). At most
  32 trees, 64 instances and 32 imports per plugin; YAML is capped at 512 KiB.
- **NPC integration.** When `this` has a string `id` (a `ValheimBaseNPC`), the binding injects the
  NPC perception store into `ctx.state.senses` before every tick
  (`senses.nearby`, `senses.remembered`, `senses.objects`, `senses.environment`, `senses.intent`).
  YAML can use `@[senses.nearby.0.id]`; JS callbacks use `ctx.state.senses`. Set `senses: false`
  to opt out; `senses` is reserved while enabled.

A complete per-NPC tree (see [NPCs and AI](npc-ai.md) for the full plugin):

```ts
const tree = ai.compile(yaml);

function makeHunter() {
    const Base = npcBase();
    return class Hunter extends Base {
        brain = "";
        onSpawn() {
            this.brain = ai.instantiate(tree, {
                this: this,
                condition: (name, c) => {
                    const senses = c.state.senses as NpcView;
                    return name === "threat" && senses.nearby.some(e => e.enemy && e.visible);
                },
                action: (name, c) => {
                    if (name !== "chase") return "success";
                    const senses = c.state.senses as NpcView;
                    const target = senses.nearby.find(e => e.enemy && e.visible);
                    if (!target) return "failure";
                    return this.moveTo(target.pos.x, target.pos.y, target.pos.z, true) ? "running" : "failure";
                }
            });
        }
        onRehydrate() { if (!this.brain) this.onSpawn(); }
        onTick() { if (this.brain) ai.tick(this.brain, 0.5); }
    };
}
```

## `ui`

```ts
ui.define(yaml, { source?, imports? }): string
ui.open(document, { mount?, modal?, get?, set?, onEvent? }): string
ui.close(panel): void
ui.invalidate(panel): void
```

Client-only declarative panels. One YAML document has a root `panel` and optional reusable
`components`; imports supply other documents by alias. `get`/`set` resolve `{ bind: "key" }`
values and `onEvent` receives scoped node paths and event names. See [Declarative UI](ui.md) for
the full syntax, panel kinds and limits.

## `nav`

```ts
nav.enable(enabled: boolean): void   // copy the debug overlay plugin to see it in-world
nav.zones(): number                  // loaded navigation zones
nav.cells(): number                  // loaded navigation cells
```

The volume is lazily built around simulated objects and around the local player when the overlay
is enabled; walkable cells are sparse green crosses, stairs yellow crosses/vertical ticks and
blocked cells red crosses, all depth-tested against world geometry. The optional `scripting/testing-optional/nav-debug` plugin (built with `task dev:nav`) enables it
on clients; it is not part of the default test bundle.

## `draw`

```ts
draw.line(a, b, { color?: string; duration?: number }): void
draw.polyline(points, { color?, duration? }): void
draw.box(center, size, { color?, duration? }): void
draw.circle(center, radius, { color?, duration?, segments? }): void
draw.mesh(vertices, indices, { color?, duration?, filled? }): void
draw.clear(): void
```

World-space lines and meshes. The same call works on both sides: server draw calls broadcast to
connected clients and player-host servers draw locally; client calls draw only in that client's
world. Colors are HTML strings (`#rrggbb`/`#rrggbbaa`), duration is seconds, and every primitive
is depth-tested and excluded from the depth buffer so it cannot make world geometry translucent.

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

Channel names are **process-wide, not plugin-namespaced**: any plugin on a peer can subscribe to
or emit on `my_mod.shop`, and `meta.sender` identifies the peer, never the plugin that sent it. Use
a name unique to your plugin and treat incoming data as untrusted input; there is no plugin-level
sender attribution to check against.

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
reads are masked when their key was claimed. While a declarative UI text field is focused, input
callbacks are skipped and `Player.TakeInput` is suppressed. This does not suppress direct
low-level key reads or every other UI path. Bindings and handlers are removed on reload and on
plugin unload.

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

## `signals`

`signals` observes named boolean conditions. The host re-samples them on an interval and turns
values into edges: `name:Enter` on false→true, `name:Exit` on true→false, and the bare `name` on
later true samples. It is a reactive observation layer, not a state machine — state machines are
built by layering predicates and reacting to these events.

```ts
import { all, signals, stable } from "../../../sdk/index";

const s = signals({ intervalMs: 250 });           // default 250 ms

s.observe("night", () => game().timeOfDay() === "night");
s.observe("threat", () => ctx.state.nearest?.distance < 12);
s.observe("danger", stable(all(() => s.is("night"), () => s.is("threat")), 2, 4));

s.on("danger:Enter", () => ctx.state.guard = true);   // rising edge
s.on("danger",       () => ctx.state.ticks = 1);      // every later true sample
s.on("danger:Exit",  () => ctx.state.guard = false);  // falling edge
s.once("danger:Enter", () => log("first alert"));

s.is("danger");        // last sampled value, no evaluation
s.changed("danger");   // changed on the last sample; reading does not clear it
s.snapshot();          // { night: boolean, threat: boolean, danger: boolean }
s.remove("danger"); s.reset("danger"); s.clear(); s.dispose();
```

Predicate combinators in `predicates` (all sample-based, evaluated once per observer interval):

| Helper | Effect |
|---|---|
| `all(...predicates)` / `any(...predicates)` | logical combination; `all()` is true, `any()` false |
| `not(pred)` / `never(pred)` | inversion |
| `stable(pred, rise?, fall?)` | flips only after consecutive samples; hysteresis for noisy inputs |
| `delay(pred, samples)` | true after consecutive true samples; false resets the count |
| `cooldown(pred, samples)` | at most one true per window |
| `once(pred)` | true only on the first true sample, then false forever |

Rules:

- **Sampled, not event-driven.** A pulse shorter than the interval is invisible; discrete events
  stay on hooks (`onDamage`, `onChat`, `onPlayerJoin`). Signals are for sustained state.
- **Host cadence, no catch-up.** The host accumulates elapsed time and evaluates at most once per
  tick; a long stall does not replay dozens of samples. Periods are milliseconds.
- **Errors are isolated.** A throwing predicate keeps the last value and logs once until it
  succeeds; a throwing handler is logged and the remaining handlers still run. Unlike a hook
  failure, this does not disable the plugin.
- **Plugin-scoped.** Observers, conditions and subscriptions are removed on unload or
  `dispose()`. Define inputs before derived conditions, or compose the raw predicates.
- **Layering.** Drive state transitions from `:Enter`/`:Exit` and keep the resulting state in a
  normal variable, `ctx.state`, or durable entity state. A promise returned from a handler is not
  awaited.

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
