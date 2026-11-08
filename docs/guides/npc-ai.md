---
title: NPCs and AI
description: Scripted NPCs, the perception store, intents, durable memory and YAML behaviour trees.
---

# NPCs and AI

ValheimJS simulates NPCs on the server even with no players nearby. The host owns movement,
navigation, ZDO writes and perception; your plugin decides what the NPC knows and what it tries
to do next.

The pieces fit together in one loop:

```mermaid
flowchart LR
    T["NPC tick (tickMs)"] --> P["Perception scan<br/>characters + world objects"]
    P --> S["Per-NPC store<br/>recognized + remembered"]
    S --> V["NpcView / npc.sensed"]
    V --> AI["AI tick<br/>ctx.state.senses"]
    AI --> A["action / condition"]
    A --> I["Intent<br/>moveTo / wait / lookAt / face / stop"]
    I --> T
    S --> SIG["signals predicates"]
    I --> H["onIntent"]
    P --> H2["onSensed"]
```

## 1. Register and spawn

`npcBase()` returns the native base class. Call it while a plugin context is active, which means
inside `setup`, not at module scope:

```ts
import { authorityHandler, definePlugin, log, npc, npcBase, players } from "../../../sdk/index";

function makeWanderer() {
    const Base = npcBase();
    return class Wanderer extends Base {
        onTick() { this.idle(); }
    };
}

export default definePlugin({
    version: "1.0.0",
    setup() {
        npc.register(makeWanderer(), {
            kind: "wanderer",
            prefab: "Dverger",
            tickMs: 500,
            faction: "Players"
        });
    },
    onServerTick: authorityHandler((ctx) => {
        if (ctx.state.spawned || !npc.available()) return;
        const player = players().online()[0] ?? players().local();
        if (!player) return;

        const entity = npc.spawn("wanderer", {
            position: { x: player.x + 3, y: player.y, z: player.z },
            name: "Wanderer"
        });
        if (entity) {
            ctx.state.spawned = true;
            log(`spawned ${entity.id}`);
        }
    })
});
```

`kind` is the plugin-facing name (`^[a-z0-9_-]{1,32}$`). The prefab is cloned once as
`vjs_npc_<kind>`; `faction` sets the clone's `Character.Faction` (`"Players"` makes a companion
fight the same enemies players do). `tickMs` is clamped to 100..5000 and defaults to 500.

## 2. What an NPC perceives

Each NPC owns a store, refreshed on its tick. Entries are not just a snapshot: they stay after
the entity leaves range so the NPC can remember a lost target, a home, a chest or a player it
met before.

```ts
interface NpcSensed {
    id: string; name: string; kind: string; prefab: string | null; faction: string;
    pos: { x: number; y: number; z: number };   // last known world position
    rel: { x: number; y: number; z: number };   // relative to the NPC
    distance: number; bearing: number;          // bearing is signed from the NPC facing
    visible: boolean; inRange: boolean; enemy: boolean;
    isPlayer: boolean; isNpc: boolean; isTamed: boolean; isDead: boolean;
    level: number; isObject: boolean;
    health: number; maxHealth: number;
    firstSeen: number; lastSeen: number;        // seconds; lastSeen -1 when never visible
    seenCount: number;
}
```

`NpcView` is the self + store view your hooks receive:

```ts
interface NpcView {
    id: string; kind: string; x: number; y: number; z: number;
    health: number; maxHealth: number; isAlive: boolean;
    faction: string; isTamed: boolean; level: number;
    home: { x: number; y: number; z: number };
    intent: NpcIntent | null;
    environment: { day: number; isDay: boolean; biome: string; weather: string | null } | null;
    nearby: NpcSensed[];      // current characters
    remembered: NpcSensed[];  // lost contacts, last-known positions
    objects: NpcSensed[];     // containers, doors, beds, stations, items...
}
```

Tune the scan per kind:

```ts
npc.register(makeWanderer(), {
    kind: "wanderer",
    prefab: "Dverger",
    tickMs: 500,
    perception: {
        radius: 24,          // character scan radius
        max: 12,             // entries in view.nearby
        viewRange: 24,       // sight range
        viewAngle: 90,       // half-angle; 90 = front hemisphere
        lineOfSight: true,   // raycast against terrain/pieces
        includePlayers: true,
        includeCreatures: true,
        includeTamed: true,
        includeNpcs: true,
        includeDead: false,
        objectRadius: 12,    // enables the world-object scan
        objectMax: 8
    }
});
```

`enemy` uses the game's faction matrix (`BaseAI.IsEnemy`), including tamed and group overrides.
World objects are the same record shape with `isObject: true` and `kind` set to `container`,
`door`, `bed`, `smelter`, `station`, `item` or `portal`.

## 3. Query the store

Do not scan `view.nearby` manually when a filter is enough:

```ts
npc.sensed(this, { radius: 12, dir: { x: 0, z: 1 }, angle: 45, kinds: ["player"] });
npc.sensed(this, { enemy: true, visible: true });
npc.sensed(this, { kinds: ["container"] });                  // world objects when objectRadius > 0
npc.sensed(this, { remembered: true, max: 64 });             // includes lost contacts
```

`dir` defaults to the NPC facing and `angle` is a half-angle. `kinds` accepts `player`, `npc`,
`creature`, `tamed`, `enemy`, `dead`, or an exact kind/prefab/object class. Results are sorted by
distance and capped by `max` (default 32).

Recognition data lives in the entry (`firstSeen`, `lastSeen`, `seenCount`). Facts that are not
derived from the game, such as ownership or interaction history, belong in memory (next section),
not in the transient store.

## 4. Drive the NPC

Intents are asynchronous. `moveTo`, `wait`, `lookAt` and `face` return immediately and report
progress through `view.intent`:

```ts
interface NpcIntent {
    kind: "idle" | "stop" | "moveTo" | "wait" | "lookAt" | "face";
    status: "idle" | "running" | "arrived" | "failed";
    reason: string | null;
    target: { x: number; y: number; z: number } | null;
    targetId: string | null;
}
```

```ts
onSpawn() {
    this.moveTo(120, 8, -40, false);   // status becomes "running"
}
onTick(view: NpcView) {
    if (view.intent?.status === "arrived") this.idle();
}
onIntent(view: NpcView, intent: NpcIntent) {
    if (intent.status === "failed") log(`failed: ${intent.reason}`);
}
```

`onIntent` fires once per settled intent. `onSensed` fires once for each entity or object newly
added to the store.

## 5. Memory: what the NPC owns and remembers

The store is host-side recognition data. Persistent plugin data goes in one JSON object per NPC:

```ts
const memory = npc.memory(this);
const player = npc.sensed(this, { players: true })[0];
if (player) memory.owner = player.id;             // ZDO id; resolve accounts separately
memory.interactions = {
    ...(memory.interactions as Record<string, number> ?? {}),
    [chestId]: Date.now()
};
npc.setMemory(this, memory);
```

`npc.memory` is stored in the NPC ZDO (`vjs.memory`, 16 KiB JSON limit), so it survives restarts and
relevance changes. Use it for ownership, interaction history, quest state and arbitrary facts.
Use `state(key)`/`setState(key, value)` for small string keys; use `npc.memory` for structured data.

## 6. Signals observe the same store

Signals sample on the host pump. Because perception is cached on the NPC tick, predicates can
query it cheaply:

```ts
import { signals } from "../../../sdk/index";

const observer = signals({ intervalMs: 250 });
observer.observe("hunter-threat", () =>
    npc.handles("hunter").some((entity) =>
        npc.sensed(entity, { enemy: true, visible: true, radius: 24 }).length > 0));
observer.on("hunter-threat:Enter", () => log("a hunter sees an enemy"));
```

For one NPC, keep the entity in plugin state and use `npc.sensed(entity, ...)`. Signals are
plugin-wide; if you need per-entity edges, include the entity id in the condition name or maintain
a dictionary in `ctx.state`.

## 7. Behaviour trees bridge into the NPC

`ai` compiles YAML once and runs one instance per entity. When `instantiate` receives a `this`
with a string `id` (a `ValheimBaseNPC`), the NPC's perception is injected as
`ctx.state.senses` before every tick. YAML can reference it (`@[senses.nearby.0.id]`); JS
callbacks can read it directly.

A tree:

```yaml
id: hunter
state:
  chasing: false
root:
  type: selector
  children:
    - condition: threat
      true:
        action: chase
    - action: wander
```

The matching JS actions live in the plugin. `chase` is responsible for its own long-running
intent: issue `moveTo`, return `running` while it is active, and return success only once the
NPC has arrived.

## 8. Full plugin: a companion hunter

```ts
import {
    ai,
    authorityHandler,
    definePlugin,
    log,
    npc,
    npcBase,
    players,
    signals,
    type AiActionOutcome,
    type NpcIntent,
    type NpcSensed,
    type NpcView
} from "../../../sdk/index";

const HUNTER_TREE = `
id: hunter
state:
  chasing: false
root:
  type: selector
  children:
    - condition: threat
      true:
        action: chase
    - action: wander
`;

function makeHunter(tree: string, tickSeconds: number) {
    const Base = npcBase();
    return class Hunter extends Base {
        private brain = "";

        onSpawn(_view: NpcView) { this.ensureBrain(tree, tickSeconds); }
        onRehydrate(_view: NpcView) { this.ensureBrain(tree, tickSeconds); }
        onSensed(entry: NpcSensed) {
            if (!entry.isPlayer) return;
            const memory = npc.memory(this);
            memory.lastPlayer = { id: entry.id, name: entry.name, at: Date.now() };
            npc.setMemory(this, memory);
        }
        onIntent(_view: NpcView, intent: NpcIntent) {
            if (intent.status === "failed") log(`hunter ${this.id}: ${intent.reason}`);
        }
        onTick(_view: NpcView) {
            if (!this.brain) this.ensureBrain(tree, tickSeconds);
            const result = ai.tick(this.brain, tickSeconds);
            if (result.status === "failure") log(`hunter ${this.id}: tree failed`);
        }
        onDespawn() { ai.release(this.brain); this.brain = ""; }

        private ensureBrain(tree: string, tickSeconds: number) {
            if (this.brain) return;
            this.brain = ai.instantiate(tree, {
                this: this,
                condition: (name, ctx): boolean => {
                    if (name !== "threat") return false;
                    const senses = ctx.state.senses as NpcView | undefined;
                    return !!senses && senses.nearby.some((entry) => entry.enemy && entry.visible);
                },
                action: (name, ctx): AiActionOutcome => {
                    if (name === "wander") {
                        return npc.perception(this)?.intent?.kind === "idle"
                            ? "success"
                            : this.idle() ? "success" : "failure";
                    }
                    if (name !== "chase") return "success";

                    const senses = ctx.state.senses as NpcView | undefined;
                    const target = senses?.nearby.find((entry) => entry.enemy && entry.visible);
                    if (!target) { ctx.state.chasing = false; return "failure"; }

                    const intent = npc.perception(this)?.intent;
                    if (ctx.state.chasing) {
                        if (intent?.status === "running") return "running";
                        ctx.state.chasing = false;
                        return intent?.status === "arrived" ? "success" : "failure";
                    }

                    if (this.moveTo(target.pos.x, target.pos.y, target.pos.z, true)) {
                        ctx.state.chasing = true;
                        return "running";
                    }
                    return "failure";
                }
            });
        }
    };
}

export default definePlugin({
    version: "1.0.0",
    setup() {
        const tickMs = 500;
        const tree = ai.compile(HUNTER_TREE, { source: "hunter.yaml" });
        npc.register(makeHunter(tree, tickMs / 1000), {
            kind: "hunter",
            prefab: "Dverger",
            tickMs,
            faction: "Players",
            perception: {
                radius: 24, max: 12, viewRange: 24, viewAngle: 90,
                lineOfSight: true, objectRadius: 12, objectMax: 8
            }
        });

        const observer = signals({ intervalMs: 250 });
        observer.observe("hunter-threat", () =>
            npc.handles("hunter").some((entity) =>
                npc.sensed(entity, { enemy: true, visible: true, radius: 24 }).length > 0));
        observer.on("hunter-threat:Enter", () => log("a hunter sees an enemy"));
    },
    onServerTick: authorityHandler((ctx) => {
        if (ctx.state.spawned || !npc.available()) return;
        const player = players().online()[0] ?? players().local();
        if (!player) return;

        const entity = npc.spawn("hunter", {
            position: { x: player.x + 3, y: player.y, z: player.z },
            name: "Hunter"
        });
        if (entity) {
            ctx.state.spawned = true;
            log(`spawned ${entity.id}`);
        }
    })
});
```

The tree and the NPC have separate lifetimes: `ai` owns the compiled definition and instance,
`npc` owns movement, ZDO persistence and the perception store. `ai.release` on despawn prevents
the brain from leaking after the entity is removed.

## Authority, cadence and limits

- **Server only.** Spawning, intents, perception and memory writes are authority-side. Clients
  receive the replicated pose and can read `npc.perception`/`npc.handles` for presentation.
- **Cadence.** `tickMs` is clamped to 100..5000; the host pump is the floor. The AI tick interval
  is whatever your plugin passes to `ai.tick`; keep it aligned with `tickMs / 1000`.
- **Store retention.** Recognized entries survive 5 minutes out of range, then are pruned.
  `npc.memory` is durable until you change or clear it.
- **Limits.** 32 live NPCs per kind per plugin; 16 KiB memory JSON; perception filters cap results
  (`max`, default 32).
- **Not in this surface.** Combat, interaction and inventory-driving intents. Add those as native
  intents first, then expose them through `ValheimBaseNPC`; do not simulate them in JS by teleporting
  or writing health directly.

## Debugging

```ts
log(JSON.stringify(npc.perception(this), null, 2));
log(npc.sensed(this, { remembered: true, max: 64 }));
log(ai.status(this.brain));
```

Use `task probe:ai` for the binding and perception-model contract, and `task probe:behaviour` for
the YAML engine.
