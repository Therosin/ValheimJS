# Troubleshooting

Start here: **`ValheimJS/logs/<your-plugin>.log`**. Every `log()` line lands there, plus
anything the server says about your plugin. The shared server log has the same lines with a
`[your-plugin]` prefix.

## My plugin does not load

Check, in order:

1. The folder is `ValheimJS/plugins/<name>/` and contains **both** `plugin.js` and
   `manifest.ini`.
2. `name` in the manifest matches the folder name (they are used interchangeably in logs).
3. `apiVersion=1` — anything else is refused with
   `needs apiVersion X, host provides 1`.
4. `entry=plugin.js` points at a file that exists.
5. The server log says `loaded N plugin(s)`. If N is lower than expected, the missing ones were
   skipped — and the reason is logged right above that line.

## The plugin loaded, then went silent

That means a handler threw and the plugin was disabled on purpose:

```
[error] hook 'onChat' threw: TypeError: … — plugin disabled
```

Fix the code, rebuild, redeploy, reload. One failing plugin never takes the server or other
plugins with it, but it will not receive further events until it is reloaded.

## My values are null or zero

Before the world finishes loading, `world.name()`, `world.day()` and friends read as
`null` / `0` / `unknown`. `onLoad` runs before that. Read them inside `onServerTick` or a timer
instead of caching them at load time.

## `chat.send` appears to do nothing

Server messages are ordinary chat sent to every peer. Clients apply their own text filters and
permission settings, so a client can suppress them. If a client sees nothing, test with a plain
message from a player and compare — it is a client-side filter, not a scripting failure.

## "escapes this plugin's data sandbox"

`fs` is confined to `ValheimJS/data/<your-plugin>/`. `../` or an absolute path outside it
throws. Write to a relative path:

```ts
fs.write("state/players.json", json);   // → data/<plugin>/state/players.json
```

## Slow server or rubber-banding

`onServerTick` fires about once a second and runs on the main thread, as does every hook and
timer. Usual causes:

- heavy work inside `onServerTick` (JSON.stringify of large objects, loops over hundreds of items)
- `setInterval` with a very small period
- `fs` writes on every tick instead of on change

Fix: cache, compare a cheap value first (`if (ctx.state.day === world.day) return;`), write files
at most every few seconds.

## Two plugins fight over the same file

Each plugin has its own sandbox, so they cannot overwrite each other's data. If you *want* to
share, one plugin should own the file and export a function; the other calls it through
`ctx.dep`.

## Networking: I listen, but nothing arrives

- Both sides must call `Networking.handle(channel, …)` — a sender alone does not open a receiver.
- The other process must be running the same channel name. Channel names are case-insensitive but
  must match otherwise.
- `Networking.send` from a client goes to **every** peer including the server; `sendTo(uid, …)`
  needs the peer uid from `players.list()` and is only meaningful on the server side.
- Messages sent before the world is loaded are dropped. Listeners attach automatically once the
  network layer exists; sends do not queue.

## Reload does not pick up my change

`Dev/WatchForChanges` must be `true`, and the file must change in a watched location
(`ValheimJS/plugins/**`). Otherwise restart the server. Note that a reload clears all
plugin state — persist important values before you rely on them.

## Version mismatch between plugins

```
'greeter' requires audit@^1.0.0, found 0.9.0
```

Bump the dependency's manifest `version`, or relax your `depends` range. Ranges compare the
numeric part only.

## Building fails

- `cannot find module '../../sdk/index'` — run `bun run build.ts` from `scripting/`, not from a
  plugin folder.
- TypeScript errors from `bun run typecheck` — fix them; the bundler happily emits code that
  fails at runtime otherwise.
- A plugin builds but immediately says `plugin bundle did not export an object` — your entry file
  must `export default definePlugin({...})`.

## Nothing in `logs/`

The folder is created on first plugin load. If it is missing, no plugin ever loaded successfully —
go back to "My plugin does not load".
