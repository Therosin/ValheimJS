# ValheimJS guides

Write server logic for Valheim in TypeScript. A plugin is a folder with a bundled script and a
manifest; the server loads it at startup and calls your handlers as things happen.

| Guide | Read it when |
|---|---|
| [Getting started](getting-started.md) | First plugin, build, deploy, see it run |
| [Plugin anatomy](plugin-anatomy.md) | Manifest fields, lifecycle, state, dependencies, errors |
| [API reference](api-reference.md) | Every function and hook you can call |
| [Recipes](recipes.md) | Welcome messages, chat commands, status files, admin-only commands |
| [Troubleshooting](troubleshooting.md) | Nothing loads, plugin disabled, wrong values |
| [Publishing](publishing.md) | Handing a plugin to other server owners |

## The 30-second version

```
ValheimJS/
  plugins/<name>/     plugin.js + manifest.ini     ← your plugin lives here
  data/<name>/        your sandboxed files
  config/<name>/      config.ini you read at runtime
  logs/<name>.log     your log lines
```

```ts
// ValheimJS/plugins/greeter/index.ts
import { definePlugin, log } from "../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    onPlayerJoin(ctx, player) {
        log(`${player.name} joined`);
        ctx.host.api.chat.send(`Welcome ${player.name}!`);
    }
});
```

```bash
cd scripting
bun run typecheck
bun run build.ts greeter --deploy ../ValheimJS/plugins
```

## Ground rules

- Everything runs on the server's main thread. Handlers must be **fast** — no loops over
  thousands of entries, no blocking I/O in `onServerTick`.
- `onServerTick` fires about once a second. `onChat` / `onPlayerJoin` / `onPlayerLeave` fire on
  the event only.
- A plugin that throws is disabled with a log line; the server keeps running and other plugins
  are unaffected.
- Your plugin can only touch its own `data/` folder. Paths outside it throw.
- The API version is `1`. A plugin declaring a different `apiVersion` is refused at load.
- Server and client can talk to each other with `Networking` (JSON messages on named channels).
