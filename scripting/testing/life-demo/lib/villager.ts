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
import type { BehaviorActionContext, BehaviorOutcome } from "./behaviour";
import type { HomeStock } from "../definitions/types";

export type { VillagerActor, VillagerBoard, VillagerNeeds } from "./actions";

export interface VillagerRuntimeOptions {
    actor: VillagerActor;
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
export interface BelongingTarget {
    target: number;
    source: string;
}

const BASE_BELONGING_TARGETS: Readonly<Record<string, BelongingTarget>> = {
    wood: { target: 50, source: "base" },
    stone: { target: 50, source: "base" },
    food: { target: 12, source: "base" },
    flint: { target: 10, source: "base" }
};
const clampSigned = (value: number): number => Math.max(-1, Math.min(1, value));
/** Comfort/deficit learning is intentionally slow; container facts can refresh faster. */
const BELONGINGS_UPDATE_MS = 30_000;

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
    homeStock: HomeStock = { containers: 0, categories: {}, amounts: {}, prefabs: {} };
    belongings: Record<string, number>;
    belongingTargets: Record<string, BelongingTarget>;
    sheltered: boolean;
    homesick: number;
    activeTarget: NpcSensed | null = null;
    activeDestination: NpcPos | null = null;

    private readonly randomSource: () => number;
    private readonly debugSink?: (message: string) => void;
    readonly log?: (message: string) => void;
    readonly board: VillagerBoard;
    private readonly actionHandler: (name: string, ctx: BehaviorActionContext) => BehaviorOutcome;
    private readonly conditionHandler: (name: string, ctx: BehaviorActionContext) => boolean;

    private activity: string | null = null;
    private latestView: NpcView | null = null;
    private lastNeedsTick = 0;
    private lastBelongingsTick = 0;
    private lastThreatId: string | null = null;
    private dirty = false;

    constructor(options: VillagerRuntimeOptions) {
        this.actor = options.actor;
        this.randomSource = options.random ?? Math.random;
        this.debugSink = options.debug;
        this.log = options.log;
        this.board = options.board ?? NOOP_BOARD;
        this.memory = options.memory ?? {};
        this.home = isPosition(this.memory.home) ? this.memory.home : options.home;
        const savedBelongings = this.memory.belongings && typeof this.memory.belongings === "object"
            ? this.memory.belongings as Record<string, unknown>
            : {};
        this.belongings = Object.fromEntries(
            Object.entries(savedBelongings)
                .filter((entry): entry is [string, number] => typeof entry[1] === "number" && Number.isFinite(entry[1]))
                .map(([key, value]) => [key, clampSigned(value)])
        );
        const savedTargets = this.memory.belongingTargets && typeof this.memory.belongingTargets === "object"
            ? this.memory.belongingTargets as Record<string, unknown>
            : {};
        this.belongingTargets = { ...BASE_BELONGING_TARGETS };
        for (const [key, raw] of Object.entries(savedTargets)) {
            if (raw && typeof raw === "object") {
                const entry = raw as { target?: unknown; source?: unknown };
                if (typeof entry.target === "number" && Number.isFinite(entry.target) && entry.target > 0) {
                    this.belongingTargets[key] = {
                        target: entry.target,
                        source: typeof entry.source === "string" ? entry.source : "saved"
                    };
                }
            }
        }
        // Legacy development memory used literal "hide"; there is no acquisition path yet.
        delete this.belongingTargets.hide;
        delete this.belongings.hide;
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

    debug(message: string): void {
        this.debugSink?.(message);
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
        this.updateHomeStock(view);
        this.updateBelongings(now);
        const elapsed = this.lastNeedsTick > 0 ? Math.min(5, (now - this.lastNeedsTick) / 1000) : 0;
        this.lastNeedsTick = now;
        this.updateNeeds(view, elapsed);
        this.publishNeeds();
        this.publishThreat(threatIn(view));
    }

    private updateHomeStock(view: NpcView): void {
        const categories: Record<string, number> = {};
        const amounts: Record<string, number> = {};
        const prefabs: Record<string, number> = {};
        let containers = 0;
        for (const entry of view.objects) {
            if (entry.container == null) continue;
            if (distance(entry.pos, this.home) > 24) continue;
            containers++;
            for (const [key, value] of Object.entries(entry.container.categories)) {
                categories[key] = (categories[key] ?? 0) + value;
            }
            for (const [key, value] of Object.entries(entry.container.amounts)) {
                amounts[key] = (amounts[key] ?? 0) + value;
            }
            for (const [key, value] of Object.entries(entry.container.prefabs ?? {})) {
                prefabs[key] = (prefabs[key] ?? 0) + value;
            }
        }

        this.homeStock = { containers, categories, amounts, prefabs };
    }

    private updateBelongings(now: number): void {
        if (this.homeStock.containers === 0 || now - this.lastBelongingsTick < BELONGINGS_UPDATE_MS) return;
        this.lastBelongingsTick = now;
        for (const [key, spec] of Object.entries(this.belongingTargets)) {
            const actual = this.homeStock.amounts[key] ?? this.homeStock.prefabs[key] ?? 0;
            const target = spec.target;
            const sample = clampSigned((actual - target) / Math.max(1, target));
            const current = this.belongings[key] ?? 0;
            const next = clampSigned(current + Math.max(-0.05, Math.min(0.05, (sample - current) * 0.25)));
            if (Math.abs(next - current) > 0.0001) {
                this.belongings[key] = next;
                this.dirty = true;
                this.debugSink?.(`[belongings] ${key}=${next.toFixed(2)} actual=${actual.toFixed(0)} target=${target}`);
            }
        }
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
            belongings: { ...this.belongings },
            belongingTargets: { ...this.belongingTargets },
            visited: [...this.visited].slice(-64),
            blocked: Object.fromEntries([...this.blocked].slice(-64))
        };
    }

    setBelongingTarget(key: string, target: number, source = "profession"): void {
        if (target <= 0) {
            this.removeBelongingTarget(key);
            return;
        }

        this.belongingTargets[key] = { target, source };
        this.belongings[key] ??= 0;
        this.dirty = true;
    }

    removeBelongingTarget(key: string): void {
        delete this.belongingTargets[key];
        delete this.belongings[key];
        this.dirty = true;
    }

    clearBelongingSource(source: string): void {
        for (const [key, spec] of Object.entries(this.belongingTargets)) {
            if (spec.source === source) this.removeBelongingTarget(key);
        }
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

    beginActivity(activity: string): void {
        this.activity = activity;
        this.activeTarget = null;
        this.activeDestination = null;
        this.publish("activity", activity);
        this.board.emit("activity:start", { activity });
        this.debugSink?.(
            `[activity] start ${activity} needs=${formatNeeds(this.needs)} ` +
                `home=${Math.round(this.home.x)},${Math.round(this.home.z)}`
        );
    }

    action(name: string, context: BehaviorActionContext): BehaviorOutcome {
        return this.actionHandler(name, context);
    }

    condition(name: string, context: BehaviorActionContext): boolean {
        return this.conditionHandler(name, context);
    }

    satisfy(effects: Readonly<Record<string, number>>): void {
        this.needs.energy = clamp01(this.needs.energy + (effects.energy ?? 0));
        this.needs.social = clamp01(this.needs.social + (effects.social ?? 0));
        this.needs.curiosity = clamp01(this.needs.curiosity + (effects.curiosity ?? 0));
        this.publishNeeds();
        this.dirty = true;
        this.debugSink?.(`[satisfy] ${JSON.stringify(effects)} -> needs=${formatNeeds(this.needs)}`);
    }

    finishActivity(reason: "success" | "failure" | "preempted"): void {
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
            this.debugSink?.(
                `[activity] end ${activity} ${reason} needs=${formatNeeds(this.needs)} ` +
                    `homesick=${this.homesick.toFixed(2)} stamina=${Math.round(this.actor.getStats().stamina)}`
            );
        }
    }

    dispose(): void {
        this.activity = null;
        this.publish("activity", "idle");
        this.board.emit("despawn");
    }

    private updateNeeds(view: NpcView, elapsed: number): void {
        if (elapsed <= 0) return;
        const moving = view.action?.kind === "moveTo" && view.action.status === "running";
        const resting = this.activity === "rest";
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
