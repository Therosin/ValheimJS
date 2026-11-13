import {
    authorityHandler,
    definePlugin,
    log,
    npc,
    npcBase,
    type NpcIntent,
    type NpcPos,
    type NpcView
} from "../../../sdk/index";
import { VillagerController } from "../lib/controller";

const NAMES = ["Astrid", "Bjorn", "Freya", "Leif", "Runa", "Sigrid", "Torsten", "Ylva"];

/** `home=x,y,z` keeps the villager alive on a dedicated server with no players. */
function configuredHome(config: (key: string) => string | null): NpcPos | null {
    const raw = config("home");
    if (!raw) return null;
    const parts = raw.split(",").map(part => Number(part.trim()));
    if (parts.length !== 3 || parts.some(part => !Number.isFinite(part))) return null;
    return { x: parts[0], y: parts[1], z: parts[2] };
}

function makeVillager() {
    const Base = npcBase();
    return class Villager extends Base {
        private controller: VillagerController | null = null;
        private lastSave = 0;

        onSpawn(view: NpcView) {
            this.attach(view);
        }

        onRehydrate(view: NpcView) {
            this.attach(view);
        }

        onTick(view: NpcView) {
            if (!this.controller) this.attach(view);
            const controller = this.controller;
            if (!controller) return;

            controller.tick(view);
            if (controller.isDirty && Date.now() - this.lastSave >= 5000) {
                npc.setMemory(this, controller.snapshotMemory());
                controller.clearDirty();
                this.lastSave = Date.now();
            }
        }

        onIntent(_view: NpcView, intent: NpcIntent) {
            if (intent.status === "failed") {
                log(`[${this.name}] ${intent.kind} failed: ${intent.reason ?? "unknown"}`);
            }
        }

        private attach(view: NpcView) {
            const controller = new VillagerController({
                actor: this,
                home: view.home,
                memory: npc.memory(this),
                log: message => log(`[${this.name}] ${message}`)
            });
            this.controller = controller;
            this.lastSave = Date.now();
            npc.setMemory(this, controller.snapshotMemory());
            const home = controller.homePosition;
            log(
                `[${this.name}] started at ${Math.round(view.x)}, ${Math.round(view.z)}; ` +
                    `home ${Math.round(home.x)}, ${Math.round(home.z)}${controller.isSheltered ? " (shelter)" : ""}`
            );
        }
    };
}

export default definePlugin({
    version: "1.0.0",

    setup(ctx) {
        if (!("npc" in ctx.host.api)) return;
        npc.register(makeVillager(), {
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
        if (npc.handles("villager").length > 0) {
            ctx.state.spawned = true;
            return;
        }

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
