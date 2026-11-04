---
title: Troubleshooting
---

# Troubleshooting

Start with **`ValheimJS/logs/<your-plugin>.log`**. Every `log()` line lands there, plus anything
the host says about your plugin. The shared log has the same lines with a `[your-plugin]` prefix.

## My plugin does not load

Check, in order:

1. The folder is `ValheimJS/plugins/<name>/` and contains `manifest.ini` and at least one of `shared.js`, `server.js`, or `client.js`.
2. `name` in the manifest matches the folder name (they are used interchangeably in logs).
3. `apiVersion=1` — anything else is refused with `needs apiVersion X, host provides 1`.
4. The plugin has a shared or matching process entry. An opposite-side-only plugin is skipped.
5. The server log says `loaded N plugin(s)`. If N is lower than expected, the missing ones were
   skipped and the reason is logged right above that line.

## "did not export a descriptor"

Your entry file must `export default definePlugin({...})` or `definePlugin(MyClass)`. A v1 bundle
sets `globalThis.__vjs_plugin` to a plugin object; v2 expects the descriptor. Rebuild with the v2
SDK — the old runtime is not maintained.

## The plugin loaded, then went silent

A handler threw and the plugin was disabled on purpose:

```
[error] hook 'onServerTick' threw: TypeError: … — plugin disabled
```

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
`null` / `0` / `"unknown"`. `setup` runs before that. Read them inside `onServerTick` or a timer.

## `world.spawn` returns null

`spawn` only accepts prefabs that already exist **and** carry a `ZNetView`. A local-only prefab is
destroyed instead of leaking. Check `world.prefabs("", 0)` / `world.exists(name)` first, and look
for `unknown prefab` or `has no ZNetView` in the log.

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

- `cannot find module '.../sdk/index'` — run `bun run build.ts` from `scripting/`.
- TypeScript errors from `bun run typecheck` — fix them; Bun emits code that fails at runtime
  otherwise.
- `setInterval` returns `Timeout` in the editor — `bun-types` sees Node-style globals. The host
  returns a number; use `ReturnType<typeof setInterval>` if you store it.

## Nothing in `logs/`

The folder is created on first plugin load. If it is missing, no plugin ever loaded successfully —
go back to "My plugin does not load".
