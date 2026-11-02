# Recipes

Copy, rename, adapt. Every example compiles against `../../sdk/index` as-is.

## Welcome + leave notice

```ts
import { definePlugin, log } from "../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    onPlayerJoin(ctx, player) {
        ctx.host.api.chat.send(`Welcome, ${player.name}!`);
        log(`${player.name} joined`);
    },
    onPlayerLeave(ctx, player) {
        ctx.host.api.chat.send(`${player.name} left the server.`);
    }
});
```

## Chat commands

`defineCommands` gives you `!command` parsing, `!help`, and admin checks.

```ts
import { definePlugin, defineCommands } from "../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    ...defineCommands("!", {
        ping: {
            description: "check that the scripting layer is alive",
            run: () => "pong"
        },
        online: {
            description: "who is connected",
            run: (ctx) => ctx.host.api.players.names().join(", ") || "nobody"
        },
        day: {
            description: "current in-game day",
            run: (ctx) => `day ${ctx.host.api.world.day()}`
        },
        greet: {
            description: "broadcast a greeting",
            admin: true,
            run: (ctx, args) => `Greetings, ${args.join(" ") || "everyone"}!`
        }
    })
});
```

Returning a string sends it back to all peers. `admin: true` checks the server's own admin list
and replies `permission denied` to anyone else.

## Status file other tools can read

```ts
import { definePlugin } from "../../sdk/index";

const FILE = "status.json";

export default definePlugin({
    version: "1.0.0",
    onLoad(ctx) {
        const publish = () => {
            const world = ctx.host.api.world.snapshot();
            ctx.host.api.fs.write(FILE, JSON.stringify({
                ...world,
                players: ctx.host.api.players.list(),
                objects: ctx.host.api.zdo.count(),
                updated: Date.now()
            }, null, 2));
        };

        publish();
        setInterval(publish, 30_000);
    }
});
```

`ValheimJS/data/status/status.json` is then readable by a website, a monitor, or a backup
script — no server access needed.

## Keep a per-player profile

Key on `steamId`, not on the name or the uid: names change and uids are per-connection, while the
SteamID is stable across sessions (it is also the only identifier you can match against an
external list).

```ts
interface Profile {
    id: string;
    name: string;
    visits: number;
    lastSeen: number;
}

const profiles = (ctx: PluginContext) => ctx.db.use<Profile>("profiles");

export default definePlugin({
    version: "1.0.0",

    onPlayerJoin(ctx, player) {
        const key = ctx.host.api.players.steamId(player.uid) ?? String(player.uid);
        const profile = profiles(ctx).upsert(
            key,
            () => ({ id: key, name: player.name, visits: 0, lastSeen: Date.now() }),
            (doc) => ({ name: player.name, visits: doc.visits + 1, lastSeen: Date.now() })
        );

        ctx.host.api.chat.send(
            profile.visits === 0
                ? `Welcome to the server, ${player.name}!`
                : `Welcome back, ${player.name}! Visit #${profile.visits + 1}`
        );
    }
});
```

Data lands in `<plugin data>/db/profiles/<steamId>.json`, one document per player, written
atomically. Non-Steam platforms return `null` from `players.steamId`, which is why the fallback
to the uid is there — treat those players as unpersistable rather than guessing.

## Daily announcement without a scheduler

```ts
import { definePlugin } from "../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    onServerTick(ctx, world) {
        const day = world.day;
        if (ctx.state.lastDay === day) return;      // cheap early exit every tick
        ctx.state.lastDay = day;

        ctx.host.api.chat.send(`Day ${day} begins. Back up your base!`);
    }
});
```

`onServerTick` runs about once a second, so guard it with a cheap comparison like this instead of
doing real work every tick.

## Talk to another plugin

Export small, pure helpers. `exports` are plain objects shared inside the server, so the consumer
calls them directly.

```ts
// plugins/audit/index.ts
import { definePlugin } from "../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    exports: {
        line(kind: string, text: string): string {
            return `${new Date().toISOString()} ${kind} ${text}`;
        }
    }
});
```

```ts
// plugins/greeter/index.ts        manifest: depends=audit@^1.0.0
import { definePlugin } from "../../sdk/index";

type AuditApi = { line(kind: string, text: string): string };

export default definePlugin({
    version: "1.0.0",
    onPlayerJoin(ctx, player) {
        const audit = ctx.dep<AuditApi>("audit");
        ctx.host.api.fs.append("audit.log", audit.line("join", player.name) + "\n");
    }
});
```

The `depends` line guarantees `audit` is loaded first; without it `ctx.dep("audit")` throws.

## Announce PvP hits

```ts
import { definePlugin } from "../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    onDamage(ctx, hit) {
        if (!hit.attackerIsPlayer || !hit.targetIsPlayer || hit.damage < 1) return;
        ctx.host.api.chat.send(
            `${hit.attacker} hit ${hit.target} for ${hit.damage.toFixed(0)} ` +
            `(${hit.targetHealth.toFixed(0)}/${hit.targetMaxHealth.toFixed(0)} hp)`
        );
    }
});
```

`onDamage` runs for every hit in the world, including creatures. Filter on the `*IsPlayer` flags
before doing anything expensive — this handler can fire many times per second in a fight.

## Server asks clients for their state

```ts
// server/
import { definePlugin, Networking } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    onLoad(ctx) {
        Networking.handle("client-hello", (message) => {
            ctx.host.api.fs.write(
                `clients/${message.sender}.json`,
                JSON.stringify(message.data, null, 2)
            );
        });

        setInterval(() => Networking.send("who-is-there", { at: Date.now() }), 60_000);
    }
});
```

```ts
// client/
import { definePlugin, Networking } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    onLoad(ctx) {
        Networking.handle("who-is-there", () => {
            Networking.send("client-hello", {
                side: ctx.host.api.env.side(),
                peers: ctx.host.api.env.peers()
            });
        });
    }
});
```

Both sides live in one plugin folder (`server/index.ts`, `client/index.ts`) so they share the
manifest and version.

## Config-driven behaviour

```ini
# ValheimJS/config/greeter/config.ini
welcome=Welcome!
broadcast_joins=true
```

```ts
export default definePlugin({
    version: "1.0.0",
    onPlayerJoin(ctx, player) {
        if (ctx.host.config("broadcast_joins") !== "true") return;
        ctx.host.api.chat.send(`${ctx.host.config("welcome") ?? "Welcome"} ${player.name}`);
    }
});
```

Read config inside handlers, not once in `onLoad`, so edits apply after a reload.
