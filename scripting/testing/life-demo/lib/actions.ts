import type { Inventory } from "../../../sdk/api/generated";
import type {
    ActionResult,
    InventoryItem,
    NpcAffordances,
    NpcPos,
    NpcSensed,
    NpcStats,
    NpcView
} from "../../../sdk/api/types";
import type { BehaviorActionContext, BehaviorOutcome } from "./behaviour";

export interface VillagerActor {
    readonly id?: string;
    moveTo(x: number, y: number, z: number, run: boolean): ActionResult;
    stop(): ActionResult;
    wait(seconds: number): ActionResult;
    lookAt(x: number, y: number, z: number): ActionResult;
    face(id: string): ActionResult;
    idle(): ActionResult;
    pick(id: string): ActionResult;
    attack?(id: string, secondary?: boolean): ActionResult;
    crouch?(crouched: boolean): ActionResult;
    affordances?(id: string): NpcAffordances;
    getStats(): NpcStats;
    getInventory(): Inventory;
}

export interface VillagerBoard {
    set(key: string, value: unknown): void;
    emit(name: string, data?: unknown): void;
    get?(key: string): unknown;
}

export interface VillagerNeeds {
    energy: number;
    social: number;
    curiosity: number;
}

/** State shared by the tree actions and the runtime that owns them. */
export interface VillagerActionHost {
    readonly actor: VillagerActor;
    readonly board: VillagerBoard;
    readonly random: () => number;
    readonly memory: Record<string, unknown>;
    readonly visited: Set<string>;
    readonly blocked: Map<string, number>;
    home: NpcPos;
    sheltered: boolean;
    activeTarget: NpcSensed | null;
    activeDestination: NpcPos | null;
    readonly log?: (message: string) => void;
    /** Gated debug detail; the runtime only supplies this when config debug=true. */
    debug?(message: string): void;
    markDirty(): void;
    publish(key: string, value: unknown): void;
    setHome(home: NpcPos, shelterId: string): void;
}

export function createActionHandler(
    host: VillagerActionHost
): (name: string, ctx: BehaviorActionContext) => BehaviorOutcome {
    return (name, ctx) => action(host, name, ctx);
}

export function createConditionHandler(
    host: VillagerActionHost
): (name: string, ctx: BehaviorActionContext) => boolean {
    return (name, ctx) => {
        if (name === "hungry") {
            return host.actor.getStats().food.length === 0;
        }

        if (name === "has-food") {
            const inventory = host.actor.getInventory();
            return inventory.items.some(item => item.type === "food" && inventory.canConsume(item));
        }

        if (name === "has-food-source") {
            const view = ctx.state.senses as NpcView | undefined;
            return !!view && hasFoodSource(view);
        }

        if (name !== "at-home") {
            return false;
        }

        const view = ctx.state.senses as NpcView | undefined;
        return !!view && distance(view, host.home) <= 2.5;
    };
}

export function hasFoodSource(view: NpcView): boolean {
    return view.objects.some(
        entry => entry.kind === "pickable" && entry.ready && entry.food && entry.distance <= 24
    );
}

export function threat(view: NpcView): NpcSensed | null {
    return nearest(
        view.nearby,
        entry => entry.enemy && entry.visible && !entry.isDead && entry.distance <= 18
    );
}

export function findShelter(host: VillagerActionHost, view: NpcView): NpcSensed | null {
    if (host.sheltered) {
        return null;
    }

    return (
        view.objects
            .filter(entry => entry.visible && !entry.isDead && (entry.kind === "bed" || entry.kind === "door"))
            .find(entry =>
                view.nearby.every(
                    other =>
                        !other.isPlayer ||
                        Math.hypot(other.pos.x - entry.pos.x, other.pos.z - entry.pos.z) > 12
                )
            ) ?? null
    );
}

function action(host: VillagerActionHost, name: string, ctx: BehaviorActionContext): BehaviorOutcome {
    const state = ctx.state;
    const view = state.senses as NpcView | undefined;

    if (name === "select-point") {
        if (!view) return "failure";
        const around = stringOr(ctx.args.around, "home") === "self" ? view : host.home;
        const point = pointAround(host, around, view.y, numberOr(ctx.args.min, 3), numberOr(ctx.args.max, 8));
        state.destination = point;
        host.activeDestination = point;
        state.run = ctx.args.run === true;
        state.moveIssued = false;
        return "success";
    }

    if (name === "select-home") {
        state.destination = host.home;
        host.activeDestination = host.home;
        state.run = false;
        state.moveIssued = false;
        return "success";
    }

    if (name === "select-target") {
        return selectTarget(host, ctx, state, view);
    }

    if (name === "select-food") {
        const inventory = host.actor.getInventory();
        const food = inventory.items.find(item => item.type === "food" && inventory.canConsume(item));
        if (food) {
            state.food = food;
            state.target = null;
            return "success";
        }

        if (!view) return "failure";
        const source = nearest(
            view.objects,
            entry => entry.kind === "pickable" && entry.ready && entry.food && entry.distance <= 24
        );
        if (!source) return "failure";
        host.activeTarget = source;
        host.activeDestination = source.pos;
        state.target = source;
        state.destination = source.pos;
        state.moveIssued = false;
        return "success";
    }

    if (name === "pick-food") {
        const target = state.target as NpcSensed | undefined;
        if (!view || !target) return "failure";
        const current = view.action;
        if (current?.kind === "pick") {
            if (current.status === "arrived") return "success";
            if (current.status === "failed" || current.status === "cancelled") return "failure";
            return "running";
        }

        const result = host.actor.pick(target.id);
        if (result.ok) host.log?.(`pick food ${target.prefab ?? target.name}`);
        return result.ok ? "running" : "failure";
    }

    if (name === "eat") {
        const inventory = host.actor.getInventory();
        const food = chooseFood(inventory, host.actor.getStats());
        if (!food) return "failure";
        const result = inventory.use(food);
        if (result.ok) host.log?.(`eat ${food.prefab} (${food.name})`);
        return result.ok ? "success" : "failure";
    }

    if (name === "select-shelter") {
        if (!view) return "failure";
        const shelter = findShelter(host, view);
        if (!shelter) return "failure";
        host.activeTarget = shelter;
        state.target = shelter;
        state.destination = shelter.pos;
        host.activeDestination = shelter.pos;
        state.moveIssued = false;
        return "success";
    }

    if (name === "select-flee-point") {
        if (!view) return "failure";
        const enemy = threat(view);
        if (!enemy) return "failure";
        host.activeTarget = enemy;
        state.target = enemy;
        state.destination = fleePoint(view, enemy);
        host.activeDestination = state.destination as NpcPos;
        state.run = true;
        state.moveIssued = false;
        return "success";
    }

    if (name === "move-to") {
        return moveAction(host, state, view);
    }

    if (name === "wait") {
        return waitAction(host, ctx, state, view);
    }

    if (name === "look-at") {
        const target = state.target as NpcSensed | undefined;
        if (!target) return "failure";
        return host.actor.lookAt(target.pos.x, target.pos.y, target.pos.z).ok ? "success" : "failure";
    }

    if (name === "look-around") {
        if (!view) return "failure";
        const point = pointAround(host, view, view.y, 2, 5);
        return host.actor.lookAt(point.x, point.y, point.z).ok ? "success" : "failure";
    }

    if (name === "face") {
        const target = state.target as NpcSensed | undefined;
        if (!target) {
            host.debug?.("face missing target");
            return "failure";
        }
        // Reference companions face a sensed point with LookAt; resolving a player ZDO by handle
        // is not guaranteed to succeed from a dedicated server. Use the sensed position.
        const result = host.actor.lookAt(target.pos.x, target.pos.y, target.pos.z);
        host.debug?.(
            `face target=${target.name || target.id} distance=${target.distance.toFixed(1)} ` +
                `pos=${target.pos.x.toFixed(1)},${target.pos.z.toFixed(1)} ok=${result.ok} ` +
                `reason=${result.reason ?? "-"}`
        );
        return result.ok ? "success" : "failure";
    }

    if (name === "claim-home") {
        const target = state.target as NpcSensed | undefined;
        if (!target) return "failure";
        host.setHome(target.pos, target.id);
        host.board.emit("shelter:claimed", targetSummary(target));
        host.log?.(`claimed shelter ${target.name || target.id}`);
        return "success";
    }

    if (name === "mark-visited") {
        const target = state.target as NpcSensed | undefined;
        if (!target) return "failure";
        host.visited.add(target.id);
        host.markDirty();
        return "success";
    }

    if (name === "remember-meeting") {
        const target = state.target as NpcSensed | undefined;
        if (!target) return "failure";
        host.memory.lastMet = { id: target.id, name: target.name, at: Date.now() };
        host.markDirty();
        host.board.emit("met", targetSummary(target));
        return "success";
    }

    return "failure";
}

function selectTarget(
    host: VillagerActionHost,
    ctx: BehaviorActionContext,
    state: Record<string, unknown>,
    view: NpcView | undefined
): BehaviorOutcome {
    if (!view) return "failure";
    const kind = stringOr(ctx.args.kind, "any");
    const maxDistance = numberOr(ctx.args.maxDistance, 20);
    const source = kind === "object" ? view.objects : view.nearby;
    const target = nearest(
        source,
        entry =>
            entry.visible &&
            !entry.isDead &&
            entry.distance <= maxDistance &&
            (kind !== "player" || entry.isPlayer) &&
            (kind !== "enemy" || entry.enemy) &&
            (kind !== "object" || entry.isObject) &&
            (entry.isObject ? !host.visited.has(entry.id) && (host.blocked.get(entry.id) ?? 0) <= Date.now() : true)
    );
    if (!target) {
        host.debug?.(`select-target none kind=${kind} max=${maxDistance}`);
        return "failure";
    }
    host.debug?.(`select-target ${target.name || target.id} kind=${kind} dist=${target.distance.toFixed(1)}`);
    host.activeTarget = target;
    state.target = target;
    state.destination = target.pos;
    host.activeDestination = target.pos;
    state.moveIssued = false;
    return "success";
}

function moveAction(
    host: VillagerActionHost,
    state: Record<string, unknown>,
    view: NpcView | undefined
): BehaviorOutcome {
    if (!view) return "failure";
    if (state.moveIssued === true) {
        const current = view.action;
        if (current?.kind === "moveTo") {
            if (current.status === "arrived") return "success";
            if (current.status === "failed" || current.status === "cancelled") {
                host.debug?.(`move-to ${current.status} reason=${current.reason ?? "-"}`);
                return "failure";
            }
            return "running";
        }
        return "running";
    }

    const destination = state.destination as NpcPos | undefined;
    if (!isPosition(destination)) {
        host.debug?.("move-to missing destination");
        return "failure";
    }
    const result = host.actor.moveTo(destination.x, destination.y, destination.z, state.run === true);
    if (!result.ok) {
        host.debug?.(
            `move-to failed target=${destination.x.toFixed(1)},${destination.z.toFixed(1)} ` +
                `run=${state.run === true} reason=${result.reason ?? "rejected"}`
        );
        return "failure";
    }
    host.debug?.(
        `move-to target=${destination.x.toFixed(1)},${destination.y.toFixed(1)},${destination.z.toFixed(1)} ` +
            `run=${state.run === true}`
    );
    state.moveIssued = true;
    return "running";
}

function waitAction(
    host: VillagerActionHost,
    ctx: BehaviorActionContext,
    state: Record<string, unknown>,
    view: NpcView | undefined
): BehaviorOutcome {
    if (!view) return "failure";
    if (state.waitIssued === true) {
        const current = view.action;
        if (current?.kind === "wait") {
            if (current.status === "arrived") return "success";
            if (current.status === "failed" || current.status === "cancelled") {
                host.debug?.(`wait ${current.status} reason=${current.reason ?? "-"}`);
                return "failure";
            }
            return "running";
        }
        host.debug?.(`wait not active action=${current?.kind ?? "none"} status=${current?.status ?? "none"}`);
        return "running";
    }

    const seconds = numberOr(ctx.args.seconds, numberOr(state.waitSeconds, 2));
    const result = host.actor.wait(seconds);
    host.debug?.(
        `wait issue seconds=${seconds.toFixed(2)} args=${String(ctx.args.seconds ?? "missing")} ` +
            `state=${String(state.waitSeconds ?? "missing")} ok=${result.ok} reason=${result.reason ?? "-"}`
    );
    if (!result.ok) return "failure";
    state.waitIssued = true;
    return "running";
}

function pointAround(
    host: VillagerActionHost,
    origin: NpcPos,
    y: number,
    minimum: number,
    maximum: number
): NpcPos {
    const angle = host.random() * Math.PI * 2;
    const radius = minimum + host.random() * (maximum - minimum);
    return {
        x: origin.x + Math.cos(angle) * radius,
        y,
        z: origin.z + Math.sin(angle) * radius
    };
}

function fleePoint(view: NpcView, enemy: NpcSensed): NpcPos {
    const dx = view.x - enemy.pos.x;
    const dz = view.z - enemy.pos.z;
    const length = Math.hypot(dx, dz) || 1;
    return { x: view.x + (dx / length) * 10, y: view.y, z: view.z + (dz / length) * 10 };
}

function chooseFood(inventory: Inventory, stats: NpcStats): InventoryItem | null {
    const needHealth = 1 - stats.healthRatio;
    const needStamina = 1 - stats.staminaRatio;
    let best: { item: InventoryItem; score: number } | null = null;
    for (const item of inventory.items) {
        if (item.type !== "food" || !item.food || !inventory.canConsume(item)) continue;
        const minutes = Math.max(1, item.food.burnTime) / 60;
        const score =
            (item.food.health / minutes) * needHealth +
            (item.food.stamina / minutes) * needStamina +
            item.food.regen * 6 * needHealth;
        if (!best || score > best.score) best = { item, score };
    }

    return best?.item ?? null;
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

function nearest(entries: NpcSensed[], predicate: (entry: NpcSensed) => boolean): NpcSensed | null {
    let best: NpcSensed | null = null;
    for (const entry of entries) {
        if (!predicate(entry)) continue;
        if (!best || entry.distance < best.distance) best = entry;
    }
    return best;
}

function distance(a: NpcPos, b: NpcPos): number {
    return Math.hypot(a.x - b.x, a.z - b.z);
}

function isPosition(value: unknown): value is NpcPos {
    if (!value || typeof value !== "object") return false;
    const pos = value as Partial<NpcPos>;
    return Number.isFinite(pos.x) && Number.isFinite(pos.y) && Number.isFinite(pos.z);
}

function numberOr(value: unknown, fallback: number): number {
    return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function stringOr(value: unknown, fallback: string): string {
    return typeof value === "string" ? value : fallback;
}
