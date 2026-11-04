---
title: Recipes
---

# Recipes

Copy, rename, adapt. Every example compiles against `../../../sdk/index` as-is.

## Spawn an object and remember its data

```ts
import { definePlugin, log, world } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",

    onServerTick(ctx) {
        if (ctx.state.handle !== undefined) return;           // once per load
        if (!world().exists("WoodPile")) return;              // prefabs arrive with the world

        const handle = world().spawn("WoodPile", 120, 8, -40);
        if (!handle) return;

        world().setData(handle, "owner", ctx.name);
        world().setData(handle, "stock", "12");
        ctx.state.handle = handle;
        log(`spawned ${handle}`);
    }
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
import { database, definePlugin } from "../../../sdk/index";

interface Counter {
    id: string;
    value: number;
}

export default definePlugin({
    version: "1.0.0",

    onPlayerJoin(ctx, player) {
        const seen = database().use<Counter>("seen");
        const key = String(player.uid);                       // session-scoped, see note below
        const entry = seen.upsert(
            key,
            () => ({ id: key, value: 0 }),
            (doc) => ({ value: doc.value + 1 })
        );

        ctx.state.seen = `${player.name}: ${entry.value + 1}`;
    }
});
```

The session `uid` is not stable across restarts. When the player binding arrives, key on the
platform identity instead; until then treat this as session state.

## Broadcast state changes without per-tick spam

```ts
import { definePlugin, log, Rpc } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    setup() {
        Rpc.channel("my_mod.market").on<{ stock: number }>("stock", (data, meta) => {
            log(`peer ${meta.sender} reports ${data.stock} items`);
        });
    },
    onServerTick(ctx, snapshot) {
        if (ctx.state.day === snapshot.day) return;
        ctx.state.day = snapshot.day;

        Rpc.channel("my_mod.market").emit("stock", { stock: 50 });   // once, on change
    }
});
```

Never broadcast on every tick: `onServerTick` is for decisions, and one event per change is enough.

## Keyboard toggle (client)

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
import { definePlugin, Rpc } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",

    setup(ctx) {
        Rpc.channel("my_mod.shop").handle<{ amount: number }, { ok: boolean }>("buy", (data) => {
            if (data.amount <= 0) return { ok: false };
            return { ok: true };          // must be synchronous
        });

        ctx.state.channel = "my_mod.shop";
    },

    async onServerTick(ctx) {
        const peer = Rpc.serverPeer();
        if (!peer || !Rpc.available()) return;

        const reply = await Rpc.channel("my_mod.shop").request<{ amount: number }, { ok: boolean }>(
            peer, "buy", { amount: 5 }, 5000
        );

        if (!reply.ok) ctx.host.log("warn", "purchase rejected");
    }
});
```

A timeout rejects the promise but does not undo remote work. Joining peers may not have registered
their handlers yet; use an application-level readiness handshake where join-time delivery matters.

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

Read config inside handlers so edits apply after a reload.
