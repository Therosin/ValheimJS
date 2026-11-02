# Plugin anatomy

A plugin is a folder in `ValheimJS/plugins/<name>/` with two files:

```
manifest.ini    what it is, what it needs
plugin.js       your bundled script
```

## manifest.ini

```ini
name=greeter          # required, unique; also the folder name and log name
version=1.0.0         # required, semver
apiVersion=1          # required, must match the server (currently 1)
entry=plugin.js       # optional, default plugin.js
enabled=true          # optional, default true
depends=core@^1.0.0   # optional, comma-separated name@range
```

`depends` accepts `*`, an exact version (`1.2.3`), `>=1.2.3`, or `^1.2.3` (same major).
Missing, incompatible, or circular dependencies fail that plugin with a log line — nothing else
is affected. Dependencies load first, so their `exports` exist by the time your `onLoad` runs.

## Class form (recommended)

```ts
import { definePlugin, serverOnly, type PluginContext, type PlayerPayload } from "../../sdk/index";

export default definePlugin(
    class Greeter {
        description = "welcomes players";
        version = "1.1.0";
        exports = { greet: (name: string) => `hello ${name}` };

        setup(ctx: PluginContext) {
            log(`ready; data at ${ctx.paths.data}`);
        }

        onPlayerJoin = serverOnly((ctx: PluginContext, player: PlayerPayload) => {
            ctx.host.api.chat.send(`Welcome ${player.name}!`);
        });
    }
);
```

The instance receives `ctx`, `state`, `host` and a local event bus before `setup` runs:

```ts
setup(ctx: PluginContext) {
    this.on("stockChanged", (item, count) => log(`${item}: ${count}`));
    this.emit("stockChanged", "Coins", 500);
}
```

- `setup(ctx)` runs on load, after dependencies; `teardown()` on unload.
- `this.on/off/once/emit` is a per-plugin bus — local only. Cross-plugin calls go through
  `exports` + `use()`; cross-process through `Networking`.
- Hooks declared as methods (`onServerTick`, `onChat`, `onPlayerJoin`, `onPlayerSpawned`,
  `onPlayerLeave`, `onDamage`, `onNet`) are forwarded automatically.
- The object form (`definePlugin({ onLoad, onChat, … })`) still works; both are supported.

Consuming another plugin's exports:

```ts
import { use } from "../../sdk/index";

use<{ greet(name: string): string }>("greeter", (greeter, ctx) => {
    log(greeter.greet(ctx.name));
});
```

## Lifecycle

```ts
import { definePlugin } from "../../sdk/index";

export default definePlugin({
    version: "1.0.0",

    onLoad(ctx) {},        // once, after every dependency is loaded
    onUnload(ctx) {},      // before the plugin is dropped (reload, shutdown)

    onServerTick(ctx, world) {},            // ~1 Hz heartbeat
    onChat(ctx, payload) {},                // a chat message arrived
    onPlayerJoin(ctx, player) {},           // a peer joined
    onPlayerLeave(ctx, player) {}           // a peer left
});
```

Rules that matter:

- **Main thread.** Everything you do runs in the server's frame. Keep handlers short; push work
  into `setInterval` only when it is cheap.
- **One bad hook disables the plugin.** If a handler throws, the error is logged with the hook
  name and the plugin stops receiving events. The server and other plugins carry on.
- **Unload is best effort.** Persist anything important as you go, not in `onUnload`.
- **No async.** The engine is synchronous: no `await`, no promises, no `import()`. Bundling
  resolves your imports at build time, so you still write normal ES modules.

## `ctx`

Every handler receives the plugin context as its first argument.

| Field | Meaning |
|---|---|
| `ctx.name` | Plugin name from the manifest |
| `ctx.dir` | Absolute path of your plugin folder |
| `ctx.paths` | `root`, `plugins`, `plugin`, `data`, `config`, `logs`, `logFile` |
| `ctx.state` | Your scratch object, alive while the plugin is loaded |
| `ctx.exports` | What other plugins may call through `ctx.dep(name)` |
| `ctx.dep(name)` | Another plugin's `exports`; throws if it is not loaded |
| `ctx.host.api` | Game API — see [API reference](api-reference.md) |
| `ctx.host.log(level, msg)` | `info` / `warn` / `error` |
| `ctx.host.config(key)` | Value from `config/<name>/config.ini`, else the manifest |
| `ctx.host.storage.get/set` | Key/value strings persisted in your plugin folder |
| `ctx.host.now()` | Epoch milliseconds |
| `ctx.host.info()` | `{ name, version, api, dir }` |

`ctx.state` disappears on reload. Use `ctx.host.storage` for anything that must survive.

## Server and client

The same plugin runs on both processes. Which one you are in is available at runtime:

```ts
ctx.host.api.env.side()        // "server" on a dedicated process, "client" otherwise
ctx.host.api.env.dedicated()   // true only for a headless dedicated server
ctx.host.api.env.isServer()    // true on a dedicated server OR a player hosting a world
ctx.host.api.env.isClient()    // true whenever there is a local player/UI
```

`side()` is fixed at startup; `isServer()` is live, because a player can start hosting later.

### Side folders

If your plugin needs different code per process, put it in `server/` or `client/`:

```
plugins/session/
  manifest.ini
  server/index.ts     → bundled to server/plugin.js, loaded by a dedicated process
  client/index.ts     → bundled to client/plugin.js, loaded by anything with a player/UI
  index.ts            → shared fallback
```

The manifest stays the same — `entry=plugin.js` is resolved inside the matching folder. A shared
`index.ts` is used when the side folder for that process is absent.

### One plugin, both sides

Shared code is usually simpler. Wrap the parts that must not run twice:

```ts
import { definePlugin, serverOnly, clientOnly } from "../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    onPlayerJoin: serverOnly((ctx, player) => {
        ctx.host.api.chat.send(`Welcome ${player.name}!`);   // authority only
    }),
    onServerTick: clientOnly((ctx, world) => {
        // client-side reaction, e.g. local announcements
    })
});
```

`serverOnly` runs when `env.isServer()` is true; `clientOnly` runs whenever the process is not
headless — a hosting player satisfies both, which is usually what you want (their client sees
the effect and their server applies it once).

### Talking between the two

`Networking.send(channel, data)` / `Networking.handle(channel, handler)` carry JSON between the
processes — see [API reference](api-reference.md#net--networking). Sending is fire-and-forget, so
make handlers idempotent.

### Keep state on the authoritative side

On a client the world data is a local view, and `onDamage` fires for hits the client predicts.
Any persistent state (files, counters, permissions) belongs behind `serverOnly` so it is written
once, by the process that decides the outcome.

## Sharing code between plugins

```ts
// plugins/core/index.ts
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
// plugins/greeter/index.ts  (manifest: depends=core@^1.0.0)
const core = ctx.dep<{ formatPlayer(name: string, uid: number): string }>("core");
log(core.formatPlayer(player.name, player.uid));
```

`exports` are live objects in one shared engine, so calls are direct and synchronous. Keep them
small and stable — other plugins compile against them.

## Configuration

```ini
# ValheimJS/config/greeter/config.ini
welcome=Welcome to the server!
```

```ts
const welcome = ctx.host.config("welcome") ?? "Welcome!";
```

Config is read on demand, so edits apply after a reload. Values from `config.ini` win over the
manifest; both are plain strings.

## Timers

```ts
const id = setInterval(() => { /* every 30 s */ }, 30_000);
setTimeout(() => { /* once */ }, 5_000);
clearInterval(id);
```

Timers are driven by the server tick, not real threads. Precision is tens of milliseconds, and
a timer belongs to the plugin that created it.

## Versioning your plugin

Bump `version` in the manifest and in `definePlugin` together. Other plugins depend on the
manifest value; the `definePlugin` value is informational for `ctx.host.info()`.
