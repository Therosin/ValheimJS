---
title: Getting started
---

# Getting started

You need a Valheim process with the ValheimJS mod installed (`ValheimJS/` next to the game
executable), plus [Bun](https://bun.sh) for building. No game code knowledge and no C# compiling.

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

        ctx.state.ticks = Number(ctx.state.ticks ?? 0) + 1;
        if (ctx.state.ticks % 60 === 0) {
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

Bundling flattens your imports into one plain script the host evaluates. SDK and codec modules can
be imported by path or URL; Bun resolves them at build time.

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

Saving anything under `ValheimJS/plugins/` reloads every plugin within a second or so. Leave it off
on a public server: a reload drops all plugin state.

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
