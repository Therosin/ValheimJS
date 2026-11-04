---
title: Getting started
---

# Getting started

You need ValheimJS installed under BepInEx, plus [Bun](https://bun.sh) for building scripts.
`ValheimJS/` next to the game executable is the default scripting/data root; the runtime DLLs
belong under `BepInEx/plugins/ValheimJS/`. Writing a JS plugin does not require compiling C#.

## 1. Create a plugin

```bash
cd scripting
bun install                 # once
mkdir -p plugins/greeter/shared
```

`scripting/plugins/greeter/manifest.ini`

```ini
name=greeter
version=1.0.0
apiVersion=1
enabled=true
```

`scripting/plugins/greeter/shared/main.ts`

```ts
import { definePlugin, game, log } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",

    setup(ctx) {
        log(`greeter started; my data folder is ${ctx.paths.data}`);
    },

    onServerTick(ctx, snapshot) {
        if (ctx.state.ticks === undefined) {
            log(`world "${snapshot.world ?? "(loading)"}", day ${snapshot.day}`);
        }

        const ticks = Number(ctx.state.ticks ?? 0) + 1;
        ctx.state.ticks = ticks;
        if (ticks % 60 === 0) {
            log(`${game().peers()} peer(s) online`);
        }
    }
});
```

`definePlugin` only produces the descriptor the host expects; it does not run your code.

## 2. Check it and build it

```bash
bun run typecheck                    # catches typos before the host sees them
bun run build.ts greeter             # → scripting/dist/greeter/{shared.js,manifest.ini}
```

Bundling flattens imports into a plain script for each entry. Use local source modules or bundled
packages; there is no module loader, Bun runtime, Node filesystem API or browser DOM in the game.

## 3. Deploy it

```bash
bun run build.ts greeter --deploy "<server>/ValheimJS/plugins"
```

or copy `scripting/dist/greeter/` into `<server>/ValheimJS/plugins/greeter/`. The folder must
contain `shared.js` and `manifest.ini`.

## 4. Restart and look

Restart the process (or enable live reload below), then check:

- the console for `loaded N plugin(s)` and your `[greeter] …` lines
- `ValheimJS/logs/greeter.log` — your own `log()` lines
- `ValheimJS/data/greeter/` — your writable folder

## Live reload while developing

The mod's settings file path is printed in the log as `settings file: …`. Add:

```ini
[Dev]
WatchForChanges = true
```

Changing bundles under `ValheimJS/plugins/` requests a reload of every plugin. Reload drops
in-memory state, timers, command registrations and pending plugin requests. Files, documents,
accounts, policy and published metadata persist. Keep this development setting off when a
reload would interrupt players' work.

## Where things live

| What | Where |
|---|---|
| Your sources | `scripting/plugins/<name>/shared/main.ts` |
| Built bundle | `scripting/dist/<name>/shared.js` |
| Installed plugin | `<root>/ValheimJS/plugins/<name>/` |
| Your files at runtime | `<root>/ValheimJS/data/<name>/` |
| Your database | `<root>/ValheimJS/data/<name>/db/` |
| Your config | `<root>/ValheimJS/config/<name>/config.ini` |
| Your logs | `<root>/ValheimJS/logs/<name>.log` |

Next: [Process branches](process-branches.md) for shared/client/server structure, or [Plugin anatomy](plugin-anatomy.md) for hooks and context.

For an interactive first plugin, the bundled `commands` example registers `!hello` and
`!demo:hello`. Both peers need ValheimJS. Try `!hello name="Test Player" welcome home`, then
`!help hello name`. Commands and their replies are private. See [Chat commands](chat-commands.md).
