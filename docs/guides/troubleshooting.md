---
title: Troubleshooting
---

# Troubleshooting

Start with **`ValheimJS/logs/<your-plugin>.log`** for plugin log() output. Also inspect the BepInEx log: startup, hook failure and binding diagnostics may appear only there.

## My plugin does not load

Check, in order:

1. The folder is `ValheimJS/plugins/<name>/` and contains `manifest.ini` and at least one of `shared.js`, `server.js`, or `client.js`.
2. `name` in the manifest matches the folder name exactly. The loader rejects a mismatch because
   `ctx.dir`, `ctx.paths.plugin`, `data/`, `config/` and `assets` all derive paths from that name.
3. `apiVersion=1` — anything else is refused with `needs apiVersion X, host provides 1`.
4. The plugin has a shared or matching process entry. An opposite-side-only plugin is skipped.
5. The server log says `loaded N plugin(s)`. If N is lower than expected, the missing ones were
   skipped and the reason is logged right above that line.

## "did not export a descriptor"

Your entry file must `export default definePlugin({...})` or `definePlugin(MyClass)`. Rebuild it
so each entry has a default export containing the descriptor expected by API version 1.
An entry without a valid default descriptor is not a valid deployed module.

## The plugin loaded, then went silent

A handler threw and the plugin was disabled on purpose:

```
[error] hook 'onServerTick' threw: TypeError: Cannot read properties of undefined (reading 'x')
    (/…/ValheimJS/plugins/greeter/server.js:14:7) — plugin disabled
    at onServerTick (...)
```

The location is mapped through the linked `shared.js.map`/`server.js.map`/`client.js.map` that
`build.ts` emits, so it points at the original TypeScript file when the source map is shipped
next to the bundle.

Fix, rebuild, redeploy, reload. One failing plugin never takes the server or other plugins with it,
but it will not receive events until reloaded.

## "binding catalog is invalid"

The C# binding classes failed validation at startup, so **no** game API installed. The log lists
each problem, for example:

```
[JsBinder:world] ValheimJS.Bindings.WorldBindings.Spawn: unused
[JsBinder:x] …: exported name 'x.spawn' is already declared by …
[JsBinder:input] …: scoped bindings need a first 'string plugin' parameter.
```

Fix the binding class and rebuild the mod. Plugin authors never see this unless they are developing
the host.

## Declarations are out of date

```
Generated declarations drifted from .../generated.ts
```

Run `task declarations` after changing a `[JsBinder]`/`[JsBinding]`, and commit the result. The
check exists so released types cannot disagree with the runtime.

## My values are null or zero

Before the world finishes loading, `game.worldName()`, `game.day()` and friends read as
`null` / `0` / `"unknown"`. `setup` runs before that. Read them inside `onServerTick` or a timer;
on clients and player hosts the heartbeat waits for the local player to finish loading.

## `world.spawn` returns null

`spawn` requires live world authority and only accepts prefabs that already exist **and** carry a
`ZNetView`. On a client, or before a session exists, it returns `null` and logs
`spawn refused: this process does not have live world authority`. A local-only prefab is destroyed
instead of leaking. Check `game().isServer()`, `world.prefabs("", 0)` / `world.exists(name)` first,
and look for `unknown prefab`, `has no ZNetView` or `without ZDO ownership` in the log.

`world.destroy`, `world.setData`, `world.getData` and `world.alive` only accept handles this plugin
received from `spawn`. A handle from another plugin logs `handle belongs to another plugin` and is
ignored; a handle from before a reload is unknown and silently a no-op.

## "escapes this plugin's data sandbox"

`fs` is confined to `ValheimJS/data/<your-plugin>/`. `../` or an absolute path outside it throws.
Write relative paths:

```ts
fs().write("state/players.json", json);   // → data/<plugin>/state/players.json
```

## Slow server or rubber-banding

`onServerTick` fires about once a second and runs on the main thread, as does every hook and timer.
Usual causes:

- heavy work inside `onServerTick` (large `JSON.stringify`, loops over hundreds of items)
- `setInterval` with a very small period
- `fs` writes every tick instead of on change
- broadcasting an RPC on every tick

Fix: cache, compare a cheap value first (`if (ctx.state.day === snapshot.day) return;`), write files
at most every few seconds, and send one event per change.

## Networking: I listen, but nothing arrives

- Confirm both peers run ValheimJS and register the matching channel/event; names are
  case-sensitive.
- `Rpc.available()` must be true before sending; messages are not queued during startup.
- Requests need a specific peer and a synchronous responder. Notifications are fire-and-forget.
- A receiver may not be ready immediately after joining; add a readiness handshake.
- The sender is a routed peer id, not an authorisation token.

## Commands, help or accounts are denied

- Native management requires the caller's verified platform identity to be in Valheim's admin
  list, or the account permission `valheimjs.manage`. See [Management](management.md).
- Valheim admin recovery does not imply membership in every plugin role. A plugin command with
  `roles: ["staff"]` still requires `staff`; include `admin` explicitly if it should be allowed.
- Role requirements use any listed role; permission requirements use every listed permission.
  Explicit account denials override grants. Aliases and help targets use the same access gate.
- Account/policy APIs require live server authority, including when used from a client-process
  player host. Setup can run before a session exists; a client connected to another host cannot
  query that host's store locally.
- `!help command [query]` accepts static text or a synchronous help callback. A promise or
  non-string result is rejected; inspect the shared log for the detailed callback error.

## Reload does not pick up my change

`Dev/WatchForChanges` must be `true`, and the file must change under `ValheimJS/plugins/**`.
Otherwise restart. A reload drops all plugin state and timer registrations.

## Version mismatch between plugins

```
'greeter' requires audit@^1.0.0, found 0.9.0
```

Bump the dependency's manifest `version`, or relax your `depends` range. Ranges compare the numeric
part only.

## Building fails

- `cannot find module '.../sdk/index'` — repository examples use relative imports into
  `scripting/sdk/`; check the example's location and import path. Running the bundler does not
  install a standalone SDK package.
- TypeScript errors from `bun run typecheck` — fix them; Bun emits code that fails at runtime
  otherwise.
- `setInterval` returns `Timeout` in the editor — `bun-types` sees Node-style globals. The host
  returns a number; use `ReturnType<typeof setInterval>` if you store it.

## Nothing in `logs/`

The host creates the logs directory while loading plugins, but a per-plugin file may not exist
until the plugin emits a log line. Check BepInEx's shared log for startup and load failures, and
verify the configured `PluginRoot` before concluding that a plugin did not run.
