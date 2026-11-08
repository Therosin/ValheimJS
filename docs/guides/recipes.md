---
title: Recipes
---

# Recipes

Copy these entries into `scripting/testing/<name>/shared/main.ts` for development, or `scripting/plugins/<name>/shared/main.ts` for a shipped plugin.
Each example imports the SDK from that location; deploy matching entries to the processes that need them.

## Spawn an object and remember its data

```ts
import { authorityHandler, definePlugin, log, world } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",

    onServerTick: authorityHandler(ctx => {
        if (ctx.state.handle !== undefined) return;           // once per load
        if (!world().exists("WoodPile")) return;              // prefabs arrive with the world

        const handle = world().spawn("WoodPile", 120, 8, -40);
        if (!handle) return;

        world().setData(handle, "owner", ctx.name);
        world().setData(handle, "stock", "12");
        ctx.state.handle = handle;
        log(`spawned ${handle}`);
    })
});
```

ZDO data is replicated with the object and saved with the world, so late joiners and restarts both
converge. Handles are session-scoped; store the ZDO id yourself if you need it after a restart.

## Status file other tools can read

```ts
import { database, definePlugin, fs, game, world } from "../../../sdk/index";

const FILE = "status.json";

function publish(): void {
    const snapshot = game().snapshot();
    const payload = {
        ...snapshot,
        objects: world().zdoCount(),
        collections: database().collections(),
        updated: Date.now()
    };

    fs().write(FILE, JSON.stringify(payload, null, 2));
}

export default definePlugin({
    version: "1.0.0",
    setup() {
        publish();
        setInterval(publish, 30_000);
    }
});
```

`ValheimJS/data/<plugin>/status.json` is then readable by a website, a monitor or a backup script.

## Persist progress with the database

```ts
import { AccountService, authorityHandler, database, definePlugin } from "../../../sdk/index";

interface Counter {
    id: string;
    value: number;
}

export default definePlugin({
    version: "1.0.0",

    onPlayerJoin: authorityHandler((ctx, player) => {
        const account = AccountService.fromPeer(String(player.uid));
        if (!account) return;
        const seen = database().use<Counter>("seen");
        const key = account.id;
        const entry = seen.upsert(
            key,
            () => ({ id: key, value: 1 }),
            (doc) => ({ value: doc.value + 1 })
        );

        ctx.state.seen = `${player.name}: ${entry.value}`;
    })
});
```

The peer `uid` is only a connection lookup. The account id persists across reconnects and name
changes. Detailed progress stays in this plugin's database; publish only small shared links
through [AccountService](accounts.md) when other plugins need to discover your records.

## Broadcast state changes without per-tick spam

```ts
import { authorityHandler, definePlugin, log, Rpc } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    setup() {
        Rpc.channel("my_mod.market").on<{ stock: number }>("stock", (data, meta) => {
            log(`peer ${meta.sender} reports ${data.stock} items`);
        });
    },
    onServerTick: authorityHandler((ctx, snapshot) => {
        if (ctx.state.day === snapshot.day) return;
        ctx.state.day = snapshot.day;

        Rpc.channel("my_mod.market").emit("stock", { stock: 50 });   // once, on change
    })
});
```

Never broadcast on every tick: `onServerTick` is for decisions, and one event per change is enough.

## Keyboard toggle (client)

Put this entry in `client/main.ts`; the input binding is unavailable on a dedicated process.

```ts
import { definePlugin, input, log } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    setup() {
        input.bind("toggle-panel", "F8");
        input.on("toggle-panel", "press", () => {
            log("toggled");
            return true;                 // claim the key for this frame
        });
    },
    teardown() {
        input.clear();                   // removes only this plugin's entries
    }
});
```

## Request/reply across processes

```ts
import { definePlugin, game, Rpc } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",

    setup() {
        Rpc.channel("my_mod.shop").handle<{ amount: number }, { ok: boolean }>("buy", (data) => {
            if (!game().isServer() || data.amount <= 0) return { ok: false };
            return { ok: true };          // must be synchronous
        });
    },

    onServerTick(ctx) {
        const peer = Rpc.serverPeer();
        if (!peer || !Rpc.available() || ctx.state.requestStarted) return;
        ctx.state.requestStarted = true;
        void Rpc.channel("my_mod.shop").request<{ amount: number }, { ok: boolean }>(
            peer, "buy", { amount: 5 }, 5000
        ).then(reply => {
            if (!reply.ok) ctx.host.log("warn", "purchase rejected");
        }).catch(error => ctx.host.log("warn", String(error)));
    }
});
```

A timeout rejects the promise but does not undo remote work. Joining peers may not have registered
their handlers yet; use an application-level readiness handshake where join-time delivery matters.
This illustrates transport, not a bank transaction. Real responders must validate the sender,
check policy and perform their authoritative operation before reporting success.

## Config-driven behaviour

```ini
# ValheimJS/config/greeter/config.ini
enabled=true
welcome=Welcome!
```

```ts
import { definePlugin, context, log } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    onPlayerJoin(ctx, player) {
        if (context().host.config("enabled") !== "true") return;
        log(`${context().host.config("welcome") ?? "Welcome"} ${player.name}`);
    }
});
```

Read config inside handlers so `config.ini` edits apply to the next call; cached values require refresh.
