---
title: ValheimJS guides
---

# ValheimJS guides

Write server and client logic for Valheim in TypeScript. A plugin is a folder with a bundled
script and a manifest; the host loads it, builds its context and calls your hooks.

| Guide | Read it when |
|---|---|
| [Getting started](getting-started.md) | First plugin, build, deploy, see it run |
| [Plugin anatomy](plugin-anatomy.md) | Manifest fields, class vs object form, hooks, state, deps |
| [API reference](api-reference.md) | Every namespace, function and hook you can call |
| [NPCs and AI](npc-ai.md) | Scripted NPCs, perception, inventories, native plans, utility policy, cooking/fire and behaviour trees |
| [Declarative UI](ui.md) | YAML panels, components, imports, bindings and events |
| [Signals](api-reference.md#signals) | Observe state transitions and compose predicates |
| [Recipes](recipes.md) | Spawning, status files, persistence, RPC, keyboard |
| [Process branches](process-branches.md) | Shared/server/client entries, startup gates and player hosts |
| [Chat commands](chat-commands.md) | Private commands, aliases, leading options and async validation |
| [Accounts](accounts.md) | Stable player identity, small shared records, roles and permissions |
| [Management](management.md) | Private native commands for users, roles and permissions |
| [Troubleshooting](troubleshooting.md) | Nothing loads, plugin disabled, wrong values |
| [Publishing](publishing.md) | Handing a plugin to other server owners |

## The 30-second version

```
ValheimJS/
  plugins/<name>/     shipped plugins only; empty by default
  testing/<name>/     development/test plugins; bundled separately and never packaged
  data/<name>/        sandboxed files + db
  config/<name>/      config.ini read at runtime
  logs/<name>.log     your log lines
```

```ts
// scripting/plugins/greeter/shared/main.ts
import { definePlugin, log, world } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",

    setup(ctx) {
        log(`ready; data at ${ctx.paths.data}`);
    },

    onServerTick(ctx, snapshot) {
        if (ctx.state.day === snapshot.day) return;   // cheap guard
        ctx.state.day = snapshot.day;
        log(`day ${snapshot.day} begins (${world().zdoCount()} objects)`);
    }
});
```

```bash
cd scripting
bun run typecheck
bun run build.ts greeter --deploy "<server>/ValheimJS/plugins"
```

## Ground rules

- Everything runs on the game's main thread. Hooks must be **fast** — no loops over thousands of
  entries, no blocking work in `onServerTick`.
- `onServerTick` fires about once a second; chat/player/damage hooks fire on the event only.
- A synchronous hook failure disables that plugin; command/request failures have their own
  lifecycle. See [Plugin anatomy](plugin-anatomy.md#lifecycle).
- A plugin can only touch its own `data/` folder and its own database.
- The API version is `1`. A plugin declaring another `apiVersion` is refused at load.
- The game API is grouped: `game`, `world`, `players`, `npc`, `ai`, `planning`, `ui`, `draw`,
  `signals`, `net`, `sync`, `ChatCommands`, `AccountService`, `PermissionService`, `input`, `fs`,
  `assets`, `db`. Types are generated from the C# bindings, so your editor always matches the
  running host.
