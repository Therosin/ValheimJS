---
title: ValheimJS
---

# ValheimJS

Game and Unity references are defined in `Valheim.targets` and resolve from the Valheim
installation's `valheim_Data/Managed` directory. Optional reference groups are controlled by
the `Use...` properties in `ValheimJS.csproj` (or MSBuild properties such as
`-p:UseGUI=false`). Game libraries are enabled by default and can be disabled with
`UseGameLibraries=false`. Feature groups cover GUI, input, networking, world physics and
navigation, content, effects, and UI Toolkit. All except UI Toolkit are enabled by default.
These are compile-time references and are not copied into the plugin output.

Server and client scripting for Valheim. Plugins are written in TypeScript, bundled by Bun into
independent shared/server/client scripts, and executed in-process by [Jint](https://github.com/sebastienros/jint) inside
a BepInEx 5 plugin. The C# side is the authoritative implementation of the JavaScript API.

```
TypeScript plugin ──bun──► <name>/{shared,server,client}.js ──► Jint engine ──► [JsBinder] classes
                                                          │
                                             Harmony capture points (C#)
                                                          │
                                                 Valheim game code
```

Plugin id: `com.svaltek.valheimjs` · JS API version: `1`

**Writing plugins?** Start at [`docs/guides/`](docs/guides/README.md) — it is written for plugin
authors only (TypeScript, no engine internals). This README is for working on the runtime itself.

## Layout

### Mod

| Path | Role |
|---|---|
| `Plugin.cs` | BepInEx entry: config, tick pump, hook registration |
| `src/Scripting/` | Engine, plugin lifecycle, manifests, dependency order, logging |
| `src/Scripting/Binding/` | Binding attributes, discovery, Jint bridge, declaration writer |
| `src/Bindings/` | Game API classes (`[JsBinder]`), one file per namespace |
| `src/Core/` | typed registries, bridge seam, identity helpers |
| `src/Core/Networking.cs` | native channel/event scopes, requests and completion pump |
| `src/Core/NetworkSync.cs` | binary frame streams, codec and deltas |
| `src/Networking/` | networked services (`CommandService`), dormant `ChatService`, RpcBus adapter and DTO conversion |
| `src/Commands/` | command definitions, parser, private help and management commands |
| `src/Services/` | accounts, connection-to-account lookup, roles and permissions |
| `src/Storage/` | document database backend, reused by plugin and host-owned stores |
| `src/Hooks/` | Harmony capture points — register live singletons, emit events |
| `src/Utils/` | RPC bus, JSON helpers, netstandard shims |
| `scripting/sdk/` | TypeScript SDK: generated declarations + thin facades |
| `scripting/lib/` | Optional imported helpers (host-backed sync facade) |
| `scripting/plugins/` | shipped plugin sources (empty by default) |
| `scripting/testing/` | development/test plugin sources; never packaged |
| `scripting/testing-optional/` | optional development-only tools; not packaged |
| `docs/guides/` | modder-facing guides |
| `harness/` | net8 console running the real host code without Unity |

### Runtime data (next to the game executable)

```
ValheimJS/
  plugins/<name>/     manifest.ini + shared.js/server.js/client.js (entries optional)
  data/<name>/        this plugin's fs sandbox and db
  services/accounts/  host-owned identity/profile and publisher metadata
  services/permissions/ host-owned roles, grants, denials and permission catalogue
  config/<name>/      config.ini (read via host.config)
  logs/<name>.log     this plugin's log lines
```

Root is `PluginRoot` in `BepInEx/config/com.svaltek.valheimjs.cfg` (defaults to
`<game root>/ValheimJS`). Paths are exposed to plugins as `ctx.paths`
(`root`, `plugins`, `plugin`, `data`, `config`, `logs`, `logFile`).

## Process-aware startup

Shared code runs on both processes, followed by the selected client/server entry. A player host
uses the client branch. C# startup modules and Harmony patches use [Shared], [ServerOnly], and
[ClientOnly]; bindings are filtered before construction. Read
[Process branches](docs/guides/process-branches.md) for configuration, lifecycle and gate examples.

## Build and deploy

```powershell
task build                                  # plugin + Jint/Acornima
task bundle                                 # → scripting/dist/<name>/
task declarations                           # regenerate SDK types from bindings
task check                                  # typecheck, tests, build, drift, bundle, harness
```

The game must be closed to replace the plugin DLL. Plugin folders can be edited live with
`Dev/WatchForChanges = true`.

## Plugin contract

`scripting/plugins/<name>/manifest.ini`:

```ini
name=hello
version=1.0.0
apiVersion=1
enabled=true
depends=core@^1.0.0 # optional; loaded first, reachable via ctx.dep("core")
```

A bundle only has to hand the host a descriptor. `definePlugin` produces one and the host does the
rest: instantiation, context, dependency exports, hook discovery, timers and teardown.

```ts
// object form
import { definePlugin, log } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    exports: { greet: (name: string) => `hello ${name}` },
    setup(ctx) {
        log(`data lives in ${ctx.paths.data}`);
    },
    onServerTick(ctx, world) {
        log(`day ${world.day}, ${world.peers} peers`);
    }
});
```

```ts
// class form
import { definePlugin, JsPlugin, log } from "../../../sdk/index";

class Greeter extends JsPlugin {
    exports = { greet: (name: string) => `hello ${name}` };
    onChat(_payload: { text: string }) {
        log(`[${this.ctx.name}] ${_payload.text}`);
    }
}

export default definePlugin(Greeter);
```

- Object hooks receive `(ctx, payload)`; class hooks receive `(payload)` with `this.ctx` attached.
- `ctx` = `name`, `dir`, `paths`, `state`, `exports`, `dep(name)`, `host` (`log`, `config`,
  `storage`, `now`, `info`, `api`).
- One Jint engine hosts all plugins (cross-plugin `exports` need shared script state). Isolation is
  per plugin: a throwing handler disables only that plugin.
- Everything runs on the Unity main thread. Hooks must be cheap; timers are host-driven.
- Plugins load in dependency order; `apiVersion` mismatch is a load error, not a crash.

## Hooks

| Hook | Payload | Capture point |
|---|---|---|
| `onServerTick` | `GameSnapshot` (~1 Hz) | `ZNet.Update` postfix, throttled |
| `onChat` | `{ channel, senderId, text }` | `Chat.RPC_ChatMessage` prefix |
| `onPlayerJoin` / `onPlayerLeave` | `{ uid, name }` | `ZNet.SendPlayerList` postfix + peer diff |
| `onPlayerSpawned` / `onPlayerDespawned` | `{ uid, name }` | heartbeat poll over character ZDOs |
| `onPlayerReady` / `onPlayerUnready` | `{ uid, name }` | server-only heartbeat poll over `WorldManager.PeerReady` |
| `onDamage` | `{ target, attacker, damage, targetHealth, targetMaxHealth, hitType, ... }` | `Character.Damage` postfix |

## Bindings (`host.api`)

| Namespace | Class | Purpose |
|---|---|---|
| `game` | `GameBindings` | side, world identity, live snapshot |
| `world` | `WorldBindings` | ZDO counts, spawning, per-object data |
| `players` | `PlayerBindings` | local player, lookups and the authority-owned online roster |
| `npc` | `NpcBindings` | server-simulated NPCs: spawn, perception store, intents, memory, state, inventory, stats |
| `ai` | `AiBindings` | YAML behaviour trees with JS actions/conditions and NPC senses |
| `planning` | `PlanningBindings` | native plans, planner/utility hosts and behaviour-goal execution |
| `ui` | `UiBindings` | client-side YAML panels, bindings and events |
| `draw` | `DrawBindings` | world-space lines/meshes; server calls replicate to clients |
| `net` | `NetBindings` | adapter over `Core/Networking` channels/events |
| `sync` | `SyncBindings` | adapter over `Core/NetworkSync` binary frames |
| `ChatCommands` | `ChatCommandsBindings` | native command registry, account gates and contextual help |
| `AccountService` | `AccountBindings` | authoritative account queries and publisher-owned metadata |
| `PermissionService` | `PermissionBindings` | authoritative role and permission queries |
| `input` | `InputBindings` | keyboard bindings + phase callbacks (client) |
| `fs` | `FileSystemBindings` | sandboxed data files |
| `assets` | `AssetBindings` | read-only files shipped in the plugin folder |
| `db` | `DatabaseBindings` | per-plugin document collections |
| `signals` | `SignalBindings` | host-sampled conditions with enter/exit events |

Adding a namespace is one class:

```csharp
[JsBinder("world", Phase = JsBindPhase.Gameplay, Order = 120,
    Description = "Live world objects.")]
internal sealed class WorldBindings : JsBindings
{
    public WorldBindings(JsBindingContext context) : base(context) { }

    [JsBinding(Description = "Number of live world objects.")]
    public int ZdoCount() => /* … */;

    [JsBinding(Scoped = true, Description = "Reads object data for the calling plugin.")]
    public string? GetData(string plugin, string handle, string key) => /* … */;
}
```

The loader discovers attributed classes, validates every signature, and installs one controlled
Jint bridge: forwarded arguments, plugin scoping, JSON returns and error attribution all happen in
one place. Duplicate exported names, static methods, out/ref parameters, async returns and
unmapped types are rejected with a diagnostic naming the class and member — the API either installs
completely or not at all.

`task declarations` writes `scripting/sdk/api/generated.ts` from the same metadata;
`task declarations:check` fails on drift, so types cannot disagree with the running host.

## Engine guardrails

Plugin code is untrusted, so the engine is bounded rather than trusted (config
`com.svaltek.valheimjs.cfg`, `[Runtime]`):

| Setting | Default | Effect |
|---|---|---|
| `MaxStatements` | 5 000 000 | statement budget per execution; runaway loops are aborted |
| `TimeoutMs` | 2 000 | wall-clock budget per execution |
| `LimitRecursion` | 256 | JS call depth |
| `TickMs` | 200 | JS timer/promise pump interval; network completions pump each frame |

Violations surface as a normal hook error (`plugin disabled`), not a hung server.

Commands await asynchronous validation/execution. Setup, teardown, RPC responders and command
help must be synchronous. Ordinary event-hook promise results are ignored; handle asynchronous
errors in your own code. Timers and promise jobs preserve plugin context; see
[Plugin anatomy](docs/guides/plugin-anatomy.md#lifecycle) for the distinct failure paths.

## Design notes

- The C# side is the implementation; the SDK is generated declarations plus thin facades.
- Patches capture game events. Native services own subsystem policy and lifecycle; scripts add plugin behavior.
- Lifecycle, timers, RPC dispatch and plugin scoping live in the host, not in the SDK.
- `host.api.game.*` is read live per call; at boot `ZNet` is uninitialized, so values are
  null/zero until the world loads.
- Bindings adapt JS calls; game integration also lives in hooks, transport and connection adapters.
  The scripting host and native service cores run independently of Unity in `harness/`.

See the [documentation index](docs/README.md) and [runtime ownership](docs/runtime.md) for the
current source map, verification coverage and limits.

## Troubleshooting

- `HarmonyX … Parameter "x" not found`: patch parameter names must match the target method.
- Plugin rejected: check `apiVersion` in its manifest against the host (currently `1`).
- `binding catalog is invalid`: the log lists the exact class/member; the game API did not install.
- `did not export a descriptor`: call `definePlugin(...)` as the bundle's default export.
- Nothing loads: verify `PluginRoot` and that each plugin folder has `manifest.ini` and a shared or matching-side entry.
