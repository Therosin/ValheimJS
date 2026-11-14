import {
    ai,
    authorityHandler,
    definePlugin,
    log,
    npc,
    npcBase,
    type NpcIntent,
    type NpcPos,
    type NpcView
} from "../../../sdk/index";
import { compileBehaviours } from "../lib/behaviours";
import {
    VillagerBrain,
    type BehaviorOutcome,
    type BehaviorRuntime,
    type VillagerActivity
} from "../lib/brain";

const NAMES = ["Astrid", "Bjorn", "Freya", "Leif", "Runa", "Sigrid", "Torsten", "Ylva"];

const runtime: BehaviorRuntime = {
    instantiate: (treeId, options) => ai.instantiate(treeId, options),
    tick: (instanceId, deltaTime) => ai.tick(instanceId, deltaTime).status as BehaviorOutcome | "cancelled",
    release: instanceId => {
        ai.release(instanceId);
    }
};

/** `home=x,y,z` keeps the villager alive on a dedicated server with no players. */
function configuredHome(config: (key: string) => string | null): NpcPos | null {
    const raw = config("home");
    if (!raw) return null;
    const parts = raw.split(",").map(part => Number(part.trim()));
    if (parts.length !== 3 || parts.some(part => !Number.isFinite(part))) return null;
    return { x: parts[0], y: parts[1], z: parts[2] };
}

function makeVillager(trees: Record<VillagerActivity, string>) {
    const Base = npcBase();
    return class Villager extends Base {
        private brain: VillagerBrain | null = null;
        private lastSave = 0;

        onSpawn(view: NpcView) {
            if (!this.brain) this.attach(view);
        }

        onRehydrate(view: NpcView) {
            if (!this.brain) this.attach(view);
        }

        onTick(view: NpcView) {
            if (!this.brain) this.attach(view);
            const brain = this.brain;
            if (!brain) return;

            brain.tick(view);
            if (brain.isDirty && Date.now() - this.lastSave >= 5000) {
                npc.setMemory(this, brain.snapshotMemory());
                brain.clearDirty();
                this.lastSave = Date.now();
            }
        }

        onDespawn() {
            this.brain?.dispose();
            this.brain = null;
        }

        onIntent(_view: NpcView, intent: NpcIntent) {
            if (intent.status !== "failed") return;
            const target = intent.target
                ? `${Math.round(intent.target.x)},${Math.round(intent.target.z)}`
                : "-";
            log(
                `[${this.name}] ${intent.kind} failed: ${intent.reason ?? "unknown"} ` +
                    `target=${target} id=${intent.targetId ?? "-"}`
            );
        }

        private attach(view: NpcView) {
            this.brain?.dispose();
            const brain = new VillagerBrain({
                actor: this,
                runtime,
                behaviors: trees,
                home: view.home,
                memory: npc.memory(this),
                log: message => log(`[${this.name}] ${message}`)
            });
            this.brain = brain;
            this.lastSave = Date.now();
            npc.setMemory(this, brain.snapshotMemory());
            const home = brain.homePosition;
            log(
                `[${this.name}] started at ${Math.round(view.x)}, ${Math.round(view.z)}; ` +
                    `home ${Math.round(home.x)}, ${Math.round(home.z)}${brain.isSheltered ? " (shelter)" : ""}`
            );
        }
    };
}

export default definePlugin({
    version: "1.0.0",

    setup(ctx) {
        if (!("npc" in ctx.host.api) || !("assets" in ctx.host.api)) return;
        if (ctx.host.config("debug") === "true") {
            ai.debug(true);
            npc.debug(true);
            log("debug tracing enabled for ai/npc");
        }
        npc.register(makeVillager(compileBehaviours()), {
            kind: "villager",
            prefab: "Dverger",
            tickMs: 500,
            faction: "Players",
            perception: {
                radius: 24,
                max: 12,
                viewRange: 24,
                viewAngle: 120,
                lineOfSight: true,
                includePlayers: true,
                includeCreatures: true,
                includeTamed: true,
                includeNpcs: true,
                includeDead: false,
                objectRadius: 14,
                objectMax: 10
            }
        });
    },

    onServerTick: authorityHandler(ctx => {
        if (!npc.available()) return;
        const existing = npc.handles("villager");
        if (existing.length > 0) {
            ctx.state.spawned = true;
            for (const duplicate of existing.slice(1)) npc.despawn(duplicate);
            return;
        }

        // Let WorldManager's 10 s rehydrate pass adopt older NPCs before spawning a new one.
        const spawnAt = typeof ctx.state.spawnAt === "number" ? ctx.state.spawnAt : 0;
        if (spawnAt === 0) ctx.state.spawnAt = Date.now() + 12_000;
        if (spawnAt === 0 || Date.now() < spawnAt) return;

        const home = configuredHome(key => ctx.host.config(key));
        let position: NpcPos | null = home;
        if (!position) {
            const api = ctx.host.api;
            const online = "players" in api && "online" in api.players ? api.players.online() : [];
            const local = "players" in api && "local" in api.players ? api.players.local() : null;
            const player = online[0] ?? local;
            if (!player) {
                if (!ctx.state.waiting) {
                    ctx.state.waiting = true;
                    log("waiting for a player or home=x,y,z before spawning the villager");
                }
                return;
            }

            const angle = Math.random() * Math.PI * 2;
            const radius = 4 + Math.random() * 4;
            position = {
                x: player.x + Math.cos(angle) * radius,
                y: player.y,
                z: player.z + Math.sin(angle) * radius
            };
        }

        const name = NAMES[Math.floor(Math.random() * NAMES.length)];
        const entity = npc.spawn("villager", { position, name });
        if (!entity) return;
        ctx.state.spawned = true;
        log(`spawned ${name} ${entity.id} at ${Math.round(position.x)}, ${Math.round(position.z)}`);
    })
});
