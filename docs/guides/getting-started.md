# Getting started

You need a Valheim server that has the ValheimJS mod installed (`ValheimJS/` next to the
server executable), plus [Bun](https://bun.sh) for building. That's it — no game code knowledge,
no compiling anything else.

## 1. Create a plugin

```bash
cd scripting
bun install                 # once
mkdir -p plugins/greeter
```

`scripting/plugins/greeter/manifest.ini`

```ini
name=greeter
version=1.0.0
apiVersion=1
entry=plugin.js
enabled=true
```

`scripting/plugins/greeter/index.ts`

```ts
import { definePlugin, log } from "../../sdk/index";

export default definePlugin({
    version: "1.0.0",

    onLoad(ctx) {
        log(`greeter started; my data folder is ${ctx.paths.data}`);
    },

    onPlayerJoin(ctx, player) {
        log(`${player.name} joined (uid ${player.uid})`);
        ctx.host.api.chat.send(`Welcome to the server, ${player.name}!`);
    },

    onPlayerLeave(ctx, player) {
        log(`${player.name} left`);
    }
});
```

`definePlugin` is only there for types — it returns your object unchanged.

## 2. Check it and build it

```bash
bun run typecheck                    # catches typos before the server sees them
bun run build.ts greeter             # → scripting/dist/greeter/{plugin.js,manifest.ini}
```

Bundling flattens your imports into one plain script. That is what the server executes.

## 3. Deploy it

```bash
bun run build.ts greeter --deploy "G:/.../Valheim dedicated server/ValheimJS/plugins"
```

or copy `scripting/dist/greeter/` into `<server folder>/ValheimJS/plugins/greeter/`
yourself. The folder must contain `plugin.js` and `manifest.ini`.

## 4. Restart and look

Restart the server (or enable live reload, below), then check:

- the server console / main log for `loaded 1 plugin(s)` and `[greeter] …`
- `ValheimJS/logs/greeter.log` — your own `log()` lines, always here
- `ValheimJS/data/greeter/` — your writable folder

Join with a client and you should see the welcome message.

## Live reload while developing

The mod's own settings file is created on first launch; its exact path is printed in the server
log as `settings file: …`. Add:

```ini
[Dev]
WatchForChanges = true
```

Saving any file under `ValheimJS/plugins/` then reloads every plugin within a second or so.
Leave it off on a public server: a reload drops all plugin state.

## Where things live

| What | Where |
|---|---|
| Your sources | `scripting/plugins/<name>/index.ts` |
| Built bundle | `scripting/dist/<name>/plugin.js` |
| Installed plugin | `<server>/ValheimJS/plugins/<name>/` |
| Your files at runtime | `<server>/ValheimJS/data/<name>/` |
| Your config | `<server>/ValheimJS/config/<name>/config.ini` |
| Your logs | `<server>/ValheimJS/logs/<name>.log` |

Start with [`plugin-anatomy.md`](plugin-anatomy.md) once something runs.
