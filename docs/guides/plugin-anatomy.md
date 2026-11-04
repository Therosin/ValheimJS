---
title: Plugin anatomy
---

# Plugin anatomy

A plugin is a folder in `ValheimJS/plugins/<name>/` with a manifest and optional shared/server/client entries:

```
manifest.ini    what it is, what it needs
shared.js       common component (optional)
server.js       dedicated-server component (optional)
client.js       client component (optional)
```

## manifest.ini

```ini
name=greeter          # required, unique; also the folder name and log name
version=1.0.0         # required, semver
apiVersion=1          # required, must match the host
enabled=true          # optional, default true
depends=core@^1.0.0   # optional, comma-separated name@range
```

`depends` accepts `*`, an exact version (`1.2.3`), `>=1.2.3`, or `^1.2.3` (same major). Missing,
incompatible, or circular dependencies fail that plugin with a log line — nothing else is
affected. Dependencies load first, so their `exports` exist by the time your `setup` runs.

## Two plugin forms, one lifecycle

### Object form

```ts
import { definePlugin, log } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    exports: { greet: (name: string) => `hello ${name}` },

    setup(ctx) {
        log(`ready in ${ctx.paths.data}`);
    },

    onServerTick(ctx, snapshot) {
        // runs ~1 Hz
    },

    teardown(ctx) {
        // best effort; persist as you go
    }
});
```

The host discovers every function property that is not lifecycle and calls it as
`(ctx, payload)`. Each component runs synchronous `setup`/`teardown` (or `onLoad`/`onUnload`) once. Shared initializes first; the side component tears down first. Both use the same context.

### Class form

Extend `JsPlugin`. The host constructs the class, attaches `this.ctx` and calls `setup`. Methods
are discovered as hooks and invoked as `(payload)` with `this` bound to the instance.

```ts
import { definePlugin, JsPlugin, log, type ServerTickPayload } from "../../../sdk/index";

class Greeter extends JsPlugin {
    version = "1.0.0";
    private seen = new Set<number>();

    setup(): void {
        log(`ready in ${this.ctx.paths.data}`);
    }

    onPlayerJoin(_payload: { uid: number; name: string }): void {
        this.seen.add(_payload.uid);
    }

    onServerTick(_snapshot: ServerTickPayload): void {
        this.ctx.state.online = this.seen.size;
    }
}

export default definePlugin(Greeter);
```

The host owns instantiation and gives entry evaluation an active context. There is no hidden event bus, and `ctx` is
always the right plugin. There is no `this.on/emit`; keep local state in fields and use
`exports` for other plugins.

## Lifecycle

| Hook | Payload | When |
|---|---|---|
| `setup(ctx)` / `onLoad(ctx)` | — | once, after dependencies load |
| `teardown(ctx)` / `onUnload(ctx)` | — | before the plugin is dropped |
| `onServerTick(ctx, snapshot)` | `GameSnapshot` | ~1 Hz |
| `onChat(ctx, payload)` | `{ channel, senderId, text }` | a chat message reaches the server |
| `onPlayerJoin` / `onPlayerLeave` | `{ uid, name }` | a peer appears/disappears |
| `onPlayerSpawned` / `onPlayerDespawned` | `{ uid, name }` | a character enters/leaves the world |
| `onDamage(ctx, payload)` | `DamagePayload` | a hit was applied (read-only) |

Rules that matter:

- **Main thread.** Everything runs in the game's frame. Keep handlers short; push work into
  `setInterval` only when it is cheap.
- **One bad hook disables the plugin.** A throwing handler is logged with the hook name and the
  plugin stops receiving events. The server and other plugins carry on.
- **Unload is best effort.** Persist anything important as you go.
- **No `await` in responders.** `net` request handlers must return synchronously; timers and
  notifications may use `async` freely because the host pumps continuations on the next tick.

## `ctx`

| Field | Meaning |
|---|---|
| `ctx.name` | Plugin name from the manifest |
| `ctx.dir` | Absolute path of your plugin folder |
| `ctx.paths` | `root`, `plugins`, `plugin`, `data`, `config`, `logs`, `logFile` |
| `ctx.state` | Scratch object, alive while the plugin is loaded |
| `ctx.exports` | What other plugins may call through `ctx.dep(name)` |
| `ctx.dep(name)` | Another plugin's `exports`; throws if it is not loaded |
| `ctx.host.api` | Game API — see [API reference](api-reference.md) |
| `ctx.host.log(level, msg)` | `info` / `warn` / `error` |
| `ctx.host.config(key)` | Value from `config/<name>/config.ini`, else the manifest |
| `ctx.host.storage.get/set` | Small key/value strings persisted in your plugin folder |
| `ctx.host.now()` | Epoch milliseconds |
| `ctx.host.info()` | `{ name, version, api, dir }` |

`ctx.state` disappears on reload. Use `db`/`fs` for anything that must survive.

## Server and client

Shared always runs when present, followed by the selected process branch. A player hosting a
world uses the client branch; live authority is separate. See [Process branches](process-branches.md)
for the source/output layout, startup configuration, lifecycle ordering, shared state and gates.

```ts
game().side()          // immutable "server" or "client"
game().dedicated()     // server-process branch
game().isClient()      // client-process branch, including player hosts
game().sessionReady()  // ZNet exists; not a world/player-ready guarantee
game().isServer()      // confirmed live authority; false before a session
```

## Sharing code between plugins

```ts
// plugins/core/shared/main.ts
export default definePlugin({
    version: "1.0.0",
    exports: {
        formatPlayer(name: string, uid: number) {
            return `${name} (#${uid})`;
        }
    }
});
```

```ts
// plugins/greeter/shared/main.ts  (manifest: depends=core@^1.0.0)
const core = context().dep<{ formatPlayer(name: string, uid: number): string }>("core");
```

`exports` are live objects in one shared engine, so calls are direct and synchronous.

## Configuration

```ini
# ValheimJS/config/greeter/config.ini
welcome=Welcome to the server!
```

```ts
const welcome = context().host.config("welcome") ?? "Welcome!";
```

Config is read on demand, so edits apply after a reload. Values from `config.ini` win over the
manifest; both are plain strings.

## Timers

```ts
const id = setInterval(() => { /* every 30 s */ }, 30_000);
setTimeout(() => { /* once */ }, 5_000);
clearInterval(id);
```

Timers are host-driven and belong to the plugin that created them; they are dropped on unload.
Precision is tens of milliseconds.

## Versioning your plugin

Bump `version` in the manifest and in `definePlugin` together. Other plugins depend on the
manifest value; the `definePlugin` value is informational for `ctx.host.info()`.
