import type { NpcPos, NpcSensed, NpcView } from "../../../sdk/api/types";
import {
    createActionHandler,
    createConditionHandler,
    findShelter as findShelterIn,
    hasFoodSource as hasFoodSourceIn,
    threat as threatIn,
    type VillagerActionHost,
    type VillagerActor,
    type VillagerBoard,
    type VillagerNeeds
} from "./actions";
import { TICK_SECONDS, type BehaviorActionContext, type BehaviorOutcome, type BehaviorRuntime } from "./behaviour";

export type { VillagerActor, VillagerBoard, VillagerNeeds } from "./actions";

export interface VillagerRuntimeOptions {
    actor: VillagerActor;
    runtime: BehaviorRuntime;
    home: NpcPos;
    memory?: Record<string, unknown>;
    needs?: Partial<VillagerNeeds>;
    board?: VillagerBoard;
    random?: () => number;
    debug?: (message: string) => void;
    log?: (message: string) => void;
}

const NOOP_BOARD: VillagerBoard = {
    set: () => {},
    emit: () => {}
};

const clamp01 = (value: number): number => Math.max(0, Math.min(1, value));
const distance = (a: NpcPos, b: NpcPos): number => Math.hypot(a.x - b.x, a.z - b.z);
const numberOr = (value: unknown, fallback: number): number =>
    typeof value === "number" && Number.isFinite(value) ? value : fallback;
const isPosition = (value: unknown): value is NpcPos => {
    if (!value || typeof value !== "object") return false;
    const pos = value as Partial<NpcPos>;
    return Number.isFinite(pos.x) && Number.isFinite(pos.y) && Number.isFinite(pos.z);
};

/**
 * Life-demo runtime: durable villager facts, needs, blackboard publication and YAML tree
 * lifecycle. It contains no activity selection; UtilityAI owns that.
 */
export class VillagerRuntime implements VillagerActionHost {
    readonly actor: VillagerActor;
    readonly memory: Record<string, unknown>;
    readonly visited: Set<string>;
    readonly blocked: Map<string, number>;
    readonly needs: VillagerNeeds;

    home: NpcPos;
    sheltered: boolean;
    homesick: number;
    activeTarget: NpcSensed | null = null;
    activeDestination: NpcPos | null = null;

    private readonly runtime: BehaviorRuntime;
    private readonly randomSource: () => number;
    private readonly debug?: (message: string) => void;
    readonly log?: (message: string) => void;
    readonly board: VillagerBoard;
    private readonly actionHandler: (name: string, ctx: BehaviorActionContext) => BehaviorOutcome;
    private readonly conditionHandler: (name: string, ctx: BehaviorActionContext) => boolean;

    private instance: string | null = null;
    private activity: string | null = null;
    private latestView: NpcView | null = null;
    private lastTick = 0;
    private lastThreatId: string | null = null;
    private dirty = false;

    constructor(options: VillagerRuntimeOptions) {
        this.actor = options.actor;
        this.runtime = options.runtime;
        this.randomSource = options.random ?? Math.random;
        this.debug = options.debug;
        this.log = options.log;
        this.board = options.board ?? NOOP_BOARD;
        this.memory = options.memory ?? {};
        this.home = isPosition(this.memory.home) ? this.memory.home : options.home;
        this.sheltered = typeof this.memory.shelter === "string";
        this.homesick = clamp01(numberOr(this.memory.homesick, 0));
        this.needs = {
            energy: clamp01(numberOr(options.needs?.energy, numberOr(this.memory.energy, 0.75))),
            social: clamp01(numberOr(options.needs?.social, numberOr(this.memory.social, 0.4))),
            curiosity: clamp01(numberOr(options.needs?.curiosity, numberOr(this.memory.curiosity, 0.55)))
        };
        this.visited = new Set(
            Array.isArray(this.memory.visited)
                ? this.memory.visited.filter((entry): entry is string => typeof entry === "string")
                : []
        );
        this.blocked = new Map(
            this.memory.blocked && typeof this.memory.blocked === "object"
                ? Object.entries(this.memory.blocked as Record<string, unknown>).filter(
                      (entry): entry is [string, number] => typeof entry[1] === "number"
                  )
                : []
        );

        this.actionHandler = createActionHandler(this);
        this.conditionHandler = createConditionHandler(this);
        this.publish("home", this.home);
        this.publish("sheltered", this.sheltered);
        this.publish("activity", "idle");
        this.publishNeeds();
    }

    get id(): string {
        return this.actor.id ?? "unknown";
    }

    get random(): () => number {
        return this.randomSource;
    }

    get isDirty(): boolean {
        return this.dirty;
    }

    get currentActivity(): string | null {
        return this.activity;
    }

    get homePosition(): NpcPos {
        return this.home;
    }

    get isSheltered(): boolean {
        return this.sheltered;
    }

    tick(view: NpcView, now = Date.now()): void {
        this.latestView = view;
        const elapsed = this.lastTick > 0 ? Math.min(5, (now - this.lastTick) / 1000) : 0;
        this.lastTick = now;
        this.updateNeeds(view, elapsed);
        this.publishNeeds();
        this.publishThreat(threatIn(view));
    }

    snapshotMemory(): Record<string, unknown> {
        return {
            ...this.memory,
            home: this.home,
            ...(this.sheltered ? { shelter: this.memory.shelter } : {}),
            energy: this.needs.energy,
            social: this.needs.social,
            curiosity: this.needs.curiosity,
            homesick: this.homesick,
            visited: [...this.visited].slice(-64),
            blocked: Object.fromEntries([...this.blocked].slice(-64))
        };
    }

    clearDirty(): void {
        this.dirty = false;
    }

    publish(key: string, value: unknown): void {
        this.board.set(key, value);
    }

    markDirty(): void {
        this.dirty = true;
    }

    setHome(home: NpcPos, shelterId: string): void {
        this.home = home;
        this.sheltered = true;
        this.memory.home = home;
        this.memory.shelter = shelterId;
        this.publish("home", this.home);
        this.publish("sheltered", true);
        this.dirty = true;
    }

    distanceFromHome(view: NpcView): number {
        return distance(view, this.home);
    }

    hasFood(): boolean {
        const inventory = this.actor.getInventory();
        return inventory.items.some(item => item.type === "food" && inventory.canConsume(item));
    }

    hasFoodSource(view: NpcView): boolean {
        return hasFoodSourceIn(view);
    }

    nearbyPlayer(view: NpcView): NpcSensed | null {
        return nearest(view.nearby, entry => entry.isPlayer && entry.visible && !entry.isDead && entry.distance <= 18);
    }

    unknownObject(view: NpcView): NpcSensed | null {
        const now = Date.now();
        return nearest(
            view.objects,
            entry =>
                entry.visible &&
                !entry.isDead &&
                !this.visited.has(entry.id) &&
                (this.blocked.get(entry.id) ?? 0) <= now &&
                entry.distance <= 14
        );
    }

    threat(view: NpcView): NpcSensed | null {
        return threatIn(view);
    }

    findShelter(view: NpcView): NpcSensed | null {
        return findShelterIn(this, view);
    }

    startActivity(activity: string, treeId: string, extraState: Record<string, unknown> = {}): void {
        this.release();
        this.activity = activity;
        this.activeTarget = null;
        this.activeDestination = null;
        this.publish("activity", activity);
        this.board.emit("activity:start", { activity });
        this.debug?.(
            `[activity] start ${activity} tree=${treeId} needs=${formatNeeds(this.needs)} ` +
                `home=${Math.round(this.home.x)},${Math.round(this.home.z)}`
        );

        const state: Record<string, unknown> = {
            home: this.home,
            target: null,
            destination: null,
            run: false,
            moveIssued: false,
            waitIssued: false,
            ...extraState
        };
        this.instance = this.runtime.instantiate(treeId, {
            this: this.actor,
            state,
            action: this.actionHandler,
            condition: this.conditionHandler
        });
    }

    tickActivity(deltaTime = TICK_SECONDS): BehaviorOutcome | "cancelled" {
        if (this.instance === null) {
            return "failure";
        }

        const status = this.runtime.tick(this.instance, deltaTime);
        if (status !== "running") {
            this.runtime.release(this.instance);
            this.instance = null;
        }
        return status;
    }

    satisfy(effects: Readonly<Record<string, number>>): void {
        this.needs.energy = clamp01(this.needs.energy + (effects.energy ?? 0));
        this.needs.social = clamp01(this.needs.social + (effects.social ?? 0));
        this.needs.curiosity = clamp01(this.needs.curiosity + (effects.curiosity ?? 0));
        this.publishNeeds();
        this.dirty = true;
        this.debug?.(`[satisfy] ${JSON.stringify(effects)} -> needs=${formatNeeds(this.needs)}`);
    }

    endActivity(reason: "success" | "failure" | "preempted"): void {
        this.release();
        const activity = this.activity;
        this.activity = null;
        this.activeTarget = null;
        this.activeDestination = null;
        this.publish("target", null);

        if (this.latestView !== null) {
            const away = distance(this.latestView, this.home);
            this.homesick = clamp01(
                this.homesick + (away > 6 ? 0.05 : -0.03)
            );
            this.dirty = true;
        }

        if (activity !== null) {
            this.board.emit(reason === "success" ? "activity:complete" : "activity:failed", { activity });
            this.debug?.(
                `[activity] end ${activity} ${reason} needs=${formatNeeds(this.needs)} ` +
                    `homesick=${this.homesick.toFixed(2)} stamina=${Math.round(this.actor.getStats().stamina)}`
            );
        }
    }

    dispose(): void {
        this.release();
        this.activity = null;
        this.publish("activity", "idle");
        this.board.emit("despawn");
    }

    private release(): void {
        if (this.instance !== null) {
            this.runtime.release(this.instance);
            this.instance = null;
        }
    }

    private updateNeeds(view: NpcView, elapsed: number): void {
        if (elapsed <= 0) return;
        const moving = view.action?.kind === "moveTo" && view.action.status === "running";
        const resting = this.activity === "rest" && this.instance !== null;
        const playerNearby = view.nearby.some(entry => entry.isPlayer && !entry.isDead && entry.distance <= 12);

        this.needs.energy = clamp01(
            this.needs.energy + (resting ? 0.04 : moving ? -0.006 : -0.0015) * elapsed
        );
        this.needs.social = clamp01(this.needs.social + (playerNearby ? 0.012 : 0.004) * elapsed);
        this.needs.curiosity = clamp01(this.needs.curiosity + 0.005 * elapsed);
    }

    private publishNeeds(): void {
        this.publish("needs", {
            energy: this.needs.energy,
            social: this.needs.social,
            curiosity: this.needs.curiosity
        });
    }

    private publishThreat(threat: NpcSensed | null): void {
        const id = threat?.id ?? null;
        if (id === this.lastThreatId) return;
        this.lastThreatId = id;
        this.publish("threat", threat ? targetSummary(threat) : null);
        if (threat) this.board.emit("threat:seen", targetSummary(threat));
    }
}

function nearest(entries: NpcSensed[], predicate: (entry: NpcSensed) => boolean): NpcSensed | null {
    let best: NpcSensed | null = null;
    for (const entry of entries) {
        if (!predicate(entry)) continue;
        if (!best || entry.distance < best.distance) best = entry;
    }
    return best;
}

function formatNeeds(needs: VillagerNeeds): string {
    return `e=${needs.energy.toFixed(2)} s=${needs.social.toFixed(2)} c=${needs.curiosity.toFixed(2)}`;
}

function targetSummary(target: NpcSensed): Record<string, unknown> {
    return {
        id: target.id,
        name: target.name,
        kind: target.kind,
        distance: target.distance,
        pos: target.pos
    };
}
