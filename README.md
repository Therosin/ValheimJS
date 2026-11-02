# ValheimJS

Server-side scripting for Valheim. Plugins are written in TypeScript, bundled by Bun into a
single plain script, and executed in-process by [Jint](https://github.com/sebastienros/jint)
inside a BepInEx 5 plugin. Server logic changes without recompiling the mod.

```
TypeScript plugin ──bun──► plugins/<name>/plugin.js ──► Jint engine ──► host API (api.*)
                                                          │
                                            Harmony capture points (C#)
                                                          │
                                                Valheim server code
```

Plugin id: `com.svaltek.valheimjs` · JS API version: `1`

**Writing plugins?** Start at [`docs/guides/`](docs/guides/README.md) — it is written for plugin
authors only (TypeScript, no engine internals). This README is for working on the runtime itself.

## Layout

### Mod

| Path | Role |
|---|---|
| `Plugin.cs` | BepInEx entry: config, tick pump, hook registration |
| `src/Scripting/` | Engine, plugin contexts, manifests, dependency order, logging |
| `src/Networking/` | Mod-owned RPC layer (channels, deferred registration, delivery) |
| `src/Utils/` | JSON emit helpers, netstandard shims |
| `src/Bindings/` | Game API modules (`[JsBinding]`), one file per surface |
| `src/Hooks/` | Harmony capture points — register live singletons, emit events |
| `src/Core/` | Typed registry + patch→JS bridge |
| `scripting/` | TypeScript workspace: `sdk/`, `plugins/`, `build.ts`, `dist/` |
| `docs/guides/` | modder-facing guides (TypeScript only) |
| `docs/hook-map.md` | which game methods are worth patching, mined from working mods |
| `harness/` | net8 console running the same host code without Unity |

### Runtime data (next to the server executable)

```
ValheimJS/
  plugins/<name>/     plugin.js + manifest.ini
  data/<name>/        this plugin's fs sandbox
  config/<name>/      config.ini (read via host.config)
  logs/<name>.log     this plugin's log lines
```

Root is `PluginRoot` in `BepInEx/config/com.svaltek.valheimjs.cfg` (defaults to
`<server root>/ValheimJS`). Paths are exposed to plugins as `ctx.paths`
(`root`, `plugins`, `plugin`, `data`, `config`, `logs`, `logFile`).

## Build and deploy

```powershell
dotnet build -c Release                     # plugin + Jint/Acornima → BepInEx/plugins/ValheimJS

cd scripting
bun install
bun run build.ts                            # → scripting/dist/<name>/
bun run build.ts --deploy "<server>/ValheimJS/plugins"
```

The game must be closed to replace the plugin DLL. Plugin folders can be edited live with
`Dev/WatchForChanges = true`.

## Plugin contract

`scripting/plugins/<name>/manifest.ini`:

```ini
name=serverinfo
version=0.1.0
apiVersion=1        # must match the host, otherwise the plugin is rejected with a clear log line
entry=plugin.js
enabled=true
depends=core@^0.1.0 # optional; loaded first, reachable via ctx.dep("core")
```

```ts
import { definePlugin, log } from "../../sdk/index";

export default definePlugin({
    version: "0.1.0",
    exports: { greet: (name: string) => `hello ${name}` },
    onLoad(ctx) {
        log(`data lives in ${ctx.paths.data}`);
    },
    onServerTick(ctx, world) {
        log(`day ${world.day}, ${world.peers} online`);
    },
    onChat(ctx, payload) {
        log(`[${payload.channel}] ${payload.senderId}: ${payload.text}`);
    }
});
```

- Handlers receive `(ctx, payload)`; `ctx` = `name`, `dir`, `paths`, `state`, `exports`,
  `dep(name)`, `host` (`log`, `config`, `storage`, `now`, `info`, `api`).
- One Jint engine hosts all plugins (cross-plugin `exports` need shared script state).
  Isolation is per plugin: a throwing handler disables only that plugin.
- Everything runs on the Unity main thread. Hooks must be cheap; timers are host-driven.
- Plugins load in dependency order; `apiVersion` mismatch is a load error, not a crash.

## Hooks

| Hook | Payload | Capture point |
|---|---|---|
| `onServerTick` | world snapshot (~1 Hz) | `ZNet.Update` postfix, throttled |
| `onChat` | `{ channel, senderId, text }` | `Chat.RPC_ChatMessage` prefix |
| `onPlayerJoin` / `onPlayerLeave` | `{ uid, name }` | `ZNet.SendPlayerList` postfix + peer diff |
| `onDamage` | `{ target, attacker, damage, hp, hitType }` | `Character.Damage` postfix |

Chat is a prefix on purpose: the server handler defers through an async permission check and
bails out when there is no local player, so a postfix would fire late or never.

## Bindings (`host.api`)

| Module | Functions |
|---|---|
| `fs` | `root, exists, read, write, append, list, mkdir, delete, join` — **scoped** to `data/<plugin>` |
| `world` | `snapshot, name, peers, day, dayFraction, timeOfDay` |
| `players` | `list, count, names, hostname(uid)` |
| `zdo` | `count, available` |
| `chat` | `send, sendTo, available` |
| `env` | `side, dedicated, isServer, isClient, singlePlayer, peers` |

Adding a surface is one file:

```csharp
[JsBinding("World", JsBindPhase.Gameplay, order: 100)]
internal static class WorldBindings
{
    public static void Bind(JsBindingContext context)
    {
        context.Add("world.day", new Func<int>(() => EnvMan.instance.GetDay()));
        context.Add("world.snapshot", new Func<string>(Snapshot), JsReturnKind.Json);
        context.Add("fs.read", new Func<string, string, string?>(Read), scoped: true); // plugin name first
    }
}
```

The loader finds it by attribute, sorts by phase/order, and invokes `Bind`. A failing module is
logged and skipped; the rest of the API still installs. Structured returns declare
`JsReturnKind.Json` and arrive in JS already parsed. Scoped bindings receive the calling
plugin's name, which is how per-plugin sandboxing works.

## Server and client

The same plugin runs on both. `Application.isBatchMode` (dedicated) picks the startup side, which
selects `server/plugin.js` or `client/plugin.js` when a plugin ships side folders; shared
`plugin.js` is the fallback. At runtime, `env.isServer()` / `env.isClient()` are live, and the SDK
wraps handlers with `serverOnly()` / `clientOnly()`.

## Engine guardrails

Plugin code is untrusted, so the engine is bounded rather than trusted (config
`com.svaltek.valheimjs.cfg`, `[Runtime]`):

| Setting | Default | Effect |
|---|---|---|
| `MaxStatements` | 5 000 000 | statement budget per execution; runaway loops are aborted |
| `TimeoutMs` | 2 000 | wall-clock budget per execution |
| `LimitRecursion` | 256 | JS call depth |
| `TickMs` | 200 | timer/heartbeat granularity |

Violations surface as a normal hook error (`plugin disabled`), not a hung server.

`async`/`await` works: the host pumps the engine's continuation queue every tick, so
`await Messaging.request(...)` resumes on a later tick. This needs Jint ≥ 4.17 (the version pinned
here) — 4.4 silently dropped continuations settled from a later script run.

## Design notes

- Patches capture, JS decides: no plugin behavior in C#.
- `host.api.world.*` is read live per call; at boot `ZNet` is uninitialized, so values are
  null/zero until the world loads.
- Binding modules are the only place game types are touched; the host stays game-agnostic and
  is covered by `harness/`.

## Troubleshooting

- `HarmonyX … Parameter "x" not found`: patch parameter names must match the target method.
- Plugin rejected: check `apiVersion` in its manifest against the host (currently `1`).
- Nothing loads: verify `PluginRoot` and that each plugin folder has `manifest.ini` + `plugin.js`.
