import {
    ai,
    authorityHandler,
    definePlugin,
    log,
    npc,
    npcBase,
    type BoardMembership,
    type BoardScope,
    type NpcAction,
    type NpcDamageEvent,
    type NpcPos,
    type NpcSensed,
    type NpcStats,
    type NpcView,
    type UtilityAI,
    type UtilityStatsStore
} from "../../../sdk/index";
import type { Inventory } from "../../../sdk/api/generated";
import { compileBehaviours } from "../lib/behaviours";
import type { BehaviorOutcome, BehaviorRuntime } from "../lib/behaviour";
import { VillagerRuntime } from "../lib/villager";
import {
    createVillagerAI,
    profileFor,
    type VillagerActivity
} from "../definitions";

const runtime: BehaviorRuntime = {
    instantiate: (treeId, options) => ai.instantiate(treeId, options),
    tick: (instanceId, deltaTime) => ai.tick(instanceId, deltaTime).status as BehaviorOutcome | "cancelled",
    release: instanceId => {
        ai.release(instanceId);
    }
};

/** `home=x,y,z` keeps the villagers alive on a dedicated server with no players. */
function configuredHome(config: (key: string) => string | null): NpcPos | null {
    const raw = config("home");
    if (!raw) return null;
    const parts = raw.split(",").map(part => Number(part.trim()));
    if (parts.length !== 3 || parts.some(part => !Number.isFinite(part))) return null;
    return { x: parts[0], y: parts[1], z: parts[2] };
}

const VILLAGERS = [
    { name: "Bob", offset: { x: -2, z: 0 } },
    { name: "Astrid", offset: { x: 2, z: 0 } }
] as const;

const VILLAGER_NAMES = new Set<string>(VILLAGERS.map(entry => entry.name));

function formatNeeds(needs: { energy: number; social: number; curiosity: number }): string {
    return `e=${needs.energy.toFixed(2)} s=${needs.social.toFixed(2)} c=${needs.curiosity.toFixed(2)}`;
}

function isPosition(value: unknown): value is NpcPos {
    if (!value || typeof value !== "object") return false;
    const pos = value as Partial<NpcPos>;
    return Number.isFinite(pos.x) && Number.isFinite(pos.y) && Number.isFinite(pos.z);
}

function distanceTo(left: NpcPos, right: NpcPos): number {
    return Math.hypot(left.x - right.x, left.z - right.z);
}

/** Move persisted villagers when the configured home points somewhere else. */
function migrateVillagers(
    entities: ReturnType<typeof npc.handles>,
    home: NpcPos
): number {
    let moved = 0;
    for (const entity of entities) {
        if (!VILLAGER_NAMES.has(entity.name)) continue;
        const view = npc.perception(entity);
        const memory = npc.memory(entity);
        const knownHome = isPosition(memory.home)
            ? memory.home
            : view?.home ?? { x: entity.x, y: entity.y, z: entity.z };
        if (distanceTo(knownHome, home) <= 8) continue;

        const profile = VILLAGERS.find(candidate => candidate.name === entity.name);
        if (!profile) continue;
        const position: NpcPos = {
            x: home.x + profile.offset.x,
            y: home.y,
            z: home.z + profile.offset.z
        };
        const spawned = npc.spawn("villager", { position, name: entity.name });
        if (!spawned) {
            log(`[${entity.name}] move failed: spawn refused at ${Math.round(home.x)}, ${Math.round(home.z)}`);
            continue;
        }

        const nextMemory: Record<string, unknown> = {
            ...memory,
            home: position
        };
        delete nextMemory.shelter;
        npc.setMemory(spawned, nextMemory);
        npc.despawn(entity);
        moved++;
        log(
            `moved ${entity.name} from ${Math.round(knownHome.x)}, ${Math.round(knownHome.z)} ` +
                `to ${Math.round(position.x)}, ${Math.round(position.z)}`
        );
    }
    return moved;
}

function makeVillager(trees: Record<VillagerActivity, string>, debug: boolean) {
    const Base = npcBase();
    return class Villager extends Base {
        private readonly debugEnabled = debug;
        private runtime: VillagerRuntime | null = null;
        private utility: UtilityAI | null = null;
        private lastSave = 0;
        private board: BoardScope | null = null;
        private membership: BoardMembership | null = null;
        private boardLinks: Array<() => void> = [];
        private memberId: string | null = null;

        getStats(): NpcStats {
            const stats = npc.stats(this);
            if (stats) return stats;
            return {
                health: this.health,
                maxHealth: this.maxHealth,
                healthRatio: this.maxHealth > 0 ? this.health / this.maxHealth : 0,
                stamina: 0,
                maxStamina: 100,
                staminaRatio: 0,
                eitr: 0,
                maxEitr: 0,
                eitrRatio: 0,
                food: [],
                statusEffects: [],
                hungry: false
            };
        }

        getInventory(): Inventory {
            return npc.inventory(this);
        }

        crouch(crouched: boolean) {
            return npc.crouch(this, crouched);
        }

        onAction(_view: NpcView, action: NpcAction) {
            if (action.status !== "failed" && action.status !== "cancelled") return;
            const target = action.target
                ? `${Math.round(action.target.x)},${Math.round(action.target.z)}`
                : "-";
            log(
                `[${this.name}] ${action.kind} ${action.status}: ${action.reason ?? "unknown"} ` +
                    `target=${target} id=${action.targetId ?? "-"}`
            );
        }

        onDamaged(hit: NpcDamageEvent) {
            this.utility?.record("damaged", hit, npc.perception(this) ?? undefined);
        }

        onHeard(entry: NpcSensed) {
            this.utility?.record("heard", entry, npc.perception(this) ?? undefined);
        }

        onDespawn() {
            this.utility?.dispose();
            this.utility = null;
            this.runtime?.dispose();
            this.runtime = null;
            this.closeBoard();
        }

        onSpawn(view: NpcView) {
            if (!this.runtime) this.attach(view);
        }

        onRehydrate(view: NpcView) {
            if (!this.runtime) this.attach(view);
        }

        onTick(view: NpcView) {
            if (!this.runtime || !this.utility) this.attach(view);
            const runtime = this.runtime;
            const utility = this.utility;
            if (!runtime || !utility) return;

            runtime.tick(view);
            utility.tick(view);
            if (runtime.isDirty && Date.now() - this.lastSave >= 5000) {
                npc.setMemory(this, runtime.snapshotMemory());
                runtime.clearDirty();
                this.lastSave = Date.now();
                if (this.debugEnabled) {
                    log(
                        `[${this.name}] [memory] save activity=${runtime.currentActivity ?? "idle"} ` +
                            `needs=${formatNeeds(runtime.needs)} visited=${runtime.visited.size}`
                    );
                }
            }
        }

        private attach(view: NpcView) {
            this.utility?.dispose();
            this.runtime?.dispose();
            this.closeBoard();

            const local = ai.board.local(`npc:${view.id}`);
            const village = ai.board.join("village:life-demo");
            const offThreat = local.onSignal("threat:seen", (_name, data) => {
                if (!data || typeof data !== "object") return;
                village.emit("threat:seen", { sourceId: view.id, threat: data });
            });
            const offHeard = village.onSignal("threat:seen", (_name, data) => {
                if (!data || typeof data !== "object") return;
                if ("sourceId" in data && data.sourceId === view.id) return;
                local.emit("heard:threat", data);
            });
            const offCore = npc.boardOnChange(this, "", (key, value) => local.set(key, value));
            this.boardLinks = [
                () => offThreat.dispose(),
                () => offHeard.dispose(),
                () => npc.boardOff(offCore)
            ];
            this.board = local;
            this.membership = village;
            this.memberId = view.id;
            village.emit("villager:online", { id: view.id, name: this.name });

            const profile = profileFor(this.name);
            const villager = new VillagerRuntime({
                actor: this,
                runtime,
                home: view.home,
                memory: npc.memory(this),
                needs: profile.needs,
                board: local,
                debug: this.debugEnabled ? message => log(`[${this.name}] ${message}`) : undefined,
                log: message => log(`[${this.name}] ${message}`)
            });
            this.runtime = villager;
            this.utility = createVillagerAI({
                context: villager,
                name: this.name,
                trees,
                store: this.store(),
                debug: this.debugEnabled,
                log: message => log(`[${this.name}] ${message}`)
            });
            this.lastSave = Date.now();
            npc.setMemory(this, villager.snapshotMemory());

            const home = villager.homePosition;
            log(
                `[${this.name}] started at ${Math.round(view.x)}, ${Math.round(view.z)}; ` +
                    `home ${Math.round(home.x)}, ${Math.round(home.z)}${villager.isSheltered ? " (shelter)" : ""}`
            );
        }

        private store(): UtilityStatsStore {
            return {
                load: () => {
                    const value = npc.memory(this).utility;
                    return value && typeof value === "object" ? (value as Partial<Record<string, number>>) : undefined;
                },
                save: stats => {
                    const memory = npc.memory(this);
                    memory.utility = stats;
                    npc.setMemory(this, memory);
                    if (this.runtime) this.runtime.memory.utility = stats;
                    if (this.debugEnabled) log(`[${this.name}] [memory] utility saved`);
                }
            };
        }

        private closeBoard() {
            const membership = this.membership;
            if (membership) {
                if (this.memberId) {
                    membership.emit("villager:offline", { id: this.memberId, name: this.name });
                }
                membership.dispose();
            }
            this.membership = null;
            for (const off of this.boardLinks) off();
            this.boardLinks = [];
            this.board?.release();
            this.board = null;
            this.memberId = null;
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

        const debug = ctx.host.config("debug") === "true";
        npc.register(makeVillager(compileBehaviours(), debug), {
            kind: "villager",
            body: "human",
            tickMs: 500,
            faction: "Players",
            items: ["Club", "Raspberry", "Raspberry"],
            perception: {
                radius: 30,
                max: 16,
                viewRange: 30,
                viewAngle: 120,
                lineOfSight: true,
                includePlayers: true,
                includeCreatures: true,
                includeTamed: true,
                includeNpcs: true,
                includeDead: false,
                objectRadius: 48,
                objectMax: 48
            }
        });
    },

    onServerTick: authorityHandler(ctx => {
        if (!npc.available()) return;

        const home = configuredHome(key => ctx.host.config(key));
        const existing = npc.handles("villager");
        if (home !== null && migrateVillagers(existing, home) > 0) return;

        const kept = new Map<string, (typeof existing)[number]>();
        for (const entity of existing) {
            if (VILLAGER_NAMES.has(entity.name) && !kept.has(entity.name)) {
                kept.set(entity.name, entity);
            } else {
                npc.despawn(entity);
            }
        }

        if (kept.size === VILLAGERS.length) {
            ctx.state.spawned = true;
            return;
        }

        // Let WorldManager's 10 s rehydrate pass adopt older NPCs before spawning new ones.
        const spawnAt = typeof ctx.state.spawnAt === "number" ? ctx.state.spawnAt : 0;
        if (spawnAt === 0) ctx.state.spawnAt = Date.now() + 12_000;
        if (spawnAt === 0 || Date.now() < spawnAt) return;

        let base: NpcPos | null = home;
        if (!base) {
            const api = ctx.host.api;
            const online = "players" in api && "online" in api.players ? api.players.online() : [];
            const local = "players" in api && "local" in api.players ? api.players.local() : null;
            const player = online[0] ?? local;
            if (!player) {
                if (!ctx.state.waiting) {
                    ctx.state.waiting = true;
                    log("waiting for a player or home=x,y,z before spawning Bob and Astrid");
                }
                return;
            }
            base = { x: player.x, y: player.y, z: player.z };
        }

        for (const villager of VILLAGERS) {
            if (kept.has(villager.name)) continue;
            const position = {
                x: base.x + villager.offset.x,
                y: base.y,
                z: base.z + villager.offset.z
            };
            const entity = npc.spawn("villager", { position, name: villager.name });
            if (entity) {
                log(
                    `spawned ${villager.name} ${entity.id} at ` +
                        `${Math.round(position.x)}, ${Math.round(position.z)}`
                );
            }
        }

        ctx.state.spawned = kept.size === VILLAGERS.length || npc.handles("villager").length >= VILLAGERS.length;
    })
});
