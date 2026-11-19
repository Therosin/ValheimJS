import { npc, type InventoryItem } from "../../../sdk/index";
import type { NpcItemInfo, NpcSensed, NpcView } from "../../../sdk/api/types";
import type { Inventory } from "../../../sdk/api/generated";
import type { Goal, GoalDescription } from "../../../sdk/planning";
import type { VillagerActivity, VillagerContext } from "./types";

interface ActivityDescription {
    readonly base?: number;
    readonly factors?: Readonly<Record<string, number>>;
    readonly satisfies?: Readonly<Record<string, number>>;
    readonly requires?: readonly string[];
    readonly provides?: readonly string[];
}

interface ActivityOptions {
    readonly cooldownMs?: number;
    readonly hysteresis?: number;
    readonly commitMs?: number;
    readonly urgent?: boolean;
    readonly satisfies?: Readonly<Record<string, number>>;
    eligible?(view: NpcView, context: VillagerContext): boolean;
    describe(
        view: NpcView,
        context: VillagerContext,
        stats: Readonly<Record<string, number>>
    ): ActivityDescription;
    state?(context: VillagerContext): Record<string, unknown>;
}

/**
 * One activity is one existing YAML tree. The goal supplies contextual facts; UtilityAI owns the
 * utility math and the Planner owns selection and execution.
 */
function activityGoal(
    context: VillagerContext,
    trees: Record<VillagerActivity, string>,
    id: VillagerActivity,
    options: ActivityOptions
): Goal {
    return {
        id,
        cooldownMs: options.cooldownMs,
        hysteresis: options.hysteresis,
        commitMs: options.commitMs,
        urgent: options.urgent,
        score: () => 0,
        describe: ({ view, stats }) => {
            const description = options.describe(view, context, stats);
            return {
                ...description,
                satisfies: options.satisfies
            } as GoalDescription;
        },
        eligible: view => options.eligible?.(view, context) ?? true,
        behaviour: {
            tree: trees[id],
            state: () => ({
                home: context.home,
                target: null,
                destination: null,
                run: false,
                moveIssued: false,
                waitIssued: false,
                ...options.state?.(context)
            }),
            action: (name, actionContext) => context.action(name, actionContext),
            condition: (name, actionContext) => context.condition(name, actionContext)
        },
        enter: () => context.beginActivity(id),
        exit: (_ctx, reason) => {
            if (reason === "success" && options.satisfies !== undefined) {
                context.satisfy(options.satisfies);
            }
            context.finishActivity(reason);
        }
    };
}

function nearestHarvestTarget(
    view: NpcView,
    context: VillagerContext
): { id: string; kind: "tree" | "rock"; distance: number } | null {
    const tool = context.actor.getInventory().equipment?.right;
    if (tool === null || tool === undefined || tool.condition === "broken") {
        return null;
    }

    const candidates = view.objects
        .filter(entry => entry.visible && !entry.isDead)
        .filter(entry => {
            if (entry.kind !== "tree" && entry.kind !== "rock") return false;
            if ((entry.toolTier ?? 0) > tool.toolTier) return false;
            if (entry.kind === "tree") return tool.chop > 0;
            return tool.pickaxe > 0;
        })
        .sort((left, right) => left.distance - right.distance);

    const target = candidates[0];
    return target === undefined
        ? null
        : {
              id: target.id,
              kind: target.kind === "tree" ? "tree" : "rock",
              distance: target.distance
          };
}

function nearestLog(view: NpcView, context: VillagerContext): NpcSensed | null {
    const tool = context.actor.getInventory().equipment?.right;
    if (tool === null || tool === undefined || tool.condition === "broken" || tool.chop <= 0) {
        return null;
    }

    return (
        view.objects
            .filter(entry => entry.visible && !entry.isDead && entry.kind === "log")
            .filter(entry => (entry.toolTier ?? 0) <= tool.toolTier)
            .sort((left, right) => left.distance - right.distance)[0] ?? null
    );
}

type ResourceIntentState = "reserved" | "loading" | "invalid";

interface ResourceIntent {
    readonly owner: string;
    readonly state: ResourceIntentState;
    readonly expiresAt: number;
}

const RESOURCE_INTENTS = new Map<string, ResourceIntent>();

function resourceUsable(id: string, owner: string, now: number): boolean {
    const intent = RESOURCE_INTENTS.get(id);
    if (intent === undefined || intent.expiresAt <= now) return true;
    return intent.owner === owner;
}

function reserveResource(id: string, owner: string, now: number, ttlMs = 5_000): boolean {
    const intent = RESOURCE_INTENTS.get(id);
    if (intent !== undefined && intent.expiresAt > now && intent.owner !== owner) return false;
    if (!npc.reserve(id, owner, ttlMs)) return false;
    RESOURCE_INTENTS.set(id, { owner, state: "reserved", expiresAt: now + ttlMs });
    return true;
}

function releaseResource(id: string | null, owner: string, state: ResourceIntentState, now: number): void {
    if (id === null) return;
    const intent = RESOURCE_INTENTS.get(id);
    if (intent !== undefined && intent.owner !== owner && intent.expiresAt > now) return;
    npc.release(id, owner);
    if (state !== "invalid" && state !== "loading") {
        RESOURCE_INTENTS.delete(id);
        return;
    }
    npc.reserve(id, owner, state === "loading" ? 2_000 : 5_000);
    RESOURCE_INTENTS.set(id, { owner, state, expiresAt: now + (state === "loading" ? 2_000 : 5_000) });
}

function clearResource(id: string | null, owner: string): void {
    if (id === null) return;
    const intent = RESOURCE_INTENTS.get(id);
    if (intent === undefined || intent.owner === owner) {
        npc.release(id, owner);
        RESOURCE_INTENTS.delete(id);
    }
}

interface ItemDecision {
    readonly keep: boolean;
    readonly equip: boolean;
    readonly reason: string;
}

function damageScore(info: NpcItemInfo | InventoryItem): number {
    let total = 0;
    for (const value of Object.values(info.damage)) {
        if (typeof value === "number" && Number.isFinite(value)) total += value;
    }
    return total;
}

function equipmentFor(
    equipment: Inventory["equipment"],
    slot: string
): InventoryItem | null {
    if (equipment === null) return null;
    switch (slot) {
        case "head": return equipment.helmet;
        case "chest": return equipment.chest;
        case "legs": return equipment.legs;
        case "shoulder": return equipment.shoulder;
        case "utility": return equipment.utility;
        case "trinket": return equipment.trinket;
        case "left": return equipment.left;
        case "right": return equipment.right;
        default: return null;
    }
}

function itemDecision(entry: NpcSensed, context: VillagerContext): ItemDecision {
    const info = entry.item;
    if (info == null) {
        return { keep: true, equip: false, reason: "metadata-unavailable" };
    }

    const inventory = context.actor.getInventory();
    if (info.isFood) {
        return context.actor.getStats().food.length < 3
            ? { keep: true, equip: false, reason: "food-slot" }
            : { keep: false, equip: false, reason: "food-full" };
    }

    if (info.type === "weapon" || info.type === "tool") {
        const current = equipmentFor(inventory.equipment, "right");
        if (current === null) return { keep: true, equip: true, reason: "no-right-item" };
        if (current.prefab === entry.prefab) return { keep: false, equip: false, reason: "duplicate" };
        if (info.toolTier > current.toolTier || damageScore(info) > damageScore(current)) {
            return { keep: true, equip: true, reason: "upgrade" };
        }
        return { keep: false, equip: false, reason: "not-upgrade" };
    }

    if (info.type === "armor") {
        const current = equipmentFor(inventory.equipment, info.equipSlot);
        if (current === null) return { keep: true, equip: true, reason: "armor-slot" };
        if (current.prefab === entry.prefab) return { keep: false, equip: false, reason: "duplicate" };
        if (info.quality > current.quality || info.armor > current.armor) {
            return { keep: true, equip: true, reason: "armor-upgrade" };
        }
        return { keep: false, equip: false, reason: "not-upgrade" };
    }

    if (info.type === "ammo") {
        const count = inventory.count(entry.prefab ?? "");
        return count < 100
            ? { keep: true, equip: false, reason: "ammo-low" }
            : { keep: false, equip: false, reason: "ammo-full" };
    }

    if (info.type === "material") {
        const count = inventory.count(entry.prefab ?? "");
        return count < 64
            ? { keep: true, equip: false, reason: "material-low" }
            : { keep: false, equip: false, reason: "material-full" };
    }

    return { keep: true, equip: false, reason: "unclassified-item" };
}

function nearestUsefulDrop(
    view: NpcView,
    context: VillagerContext,
    excludeId?: string | null
): NpcSensed | null {
    const now = Date.now();
    const candidates = view.objects
        .filter(entry => entry.visible && !entry.isDead && entry.kind === "item")
        .filter(entry => entry.id !== excludeId)
        .filter(entry => resourceUsable(entry.id, context.id, now))
        .filter(entry => entry.distance <= 20)
        .sort((left, right) => left.distance - right.distance);

    for (const candidate of candidates) {
        const decision = itemDecision(candidate, context);
        if (decision.keep) return candidate;
        context.debug?.(
            `collect ignore ${candidate.prefab ?? candidate.name}: ${decision.reason}`
        );
    }
    return null;
}

function nearestDrop(view: NpcView, owner: string, excludeId?: string | null): NpcSensed | null {
    const now = Date.now();
    return (
        view.objects
            .filter(entry => entry.visible && !entry.isDead && entry.kind === "item")
            .filter(entry => entry.id !== excludeId)
            .filter(entry => resourceUsable(entry.id, owner, now))
            .filter(entry => entry.distance <= 20)
            .sort((left, right) => left.distance - right.distance)[0] ?? null
    );
}

function woodCount(context: VillagerContext): number {
    let total = 0;
    for (const item of context.actor.getInventory().items) {
        if (item.prefab === "Wood") total += item.stack;
    }
    return total;
}

function harvestGoal(context: VillagerContext): Goal {
    let activeTarget: string | null = null;
    let stage: "tree" | "log" | "collect" = "tree";
    let waitUntil = 0;

    const start = (
        ctx: Parameters<NonNullable<Goal["enter"]>>[0],
        activity: "harvest" | "pickup",
        target: string
    ) => {
        activeTarget = target;
        context.publish("activity", stage === "collect" ? "collect" : "harvest");
        context.board.emit("activity:stage", { activity: "harvest", stage, targetId: target });
        ctx.log(`harvest stage=${stage} target=${target}`);
        const sensed = ctx.view.objects.find(entry => entry.id === target);
        if (activity === "pickup") {
            ctx.log(`pickup ${sensed?.prefab ?? sensed?.name ?? target}`);
            ctx.run([{ interact: { target, verb: "pickup" } }], {
                timeoutMs: 30_000,
                log: message => ctx.log(message)
            });
            return;
        }

        ctx.run([{ harvest: { target } }], {
            timeoutMs: 60_000,
            log: message => ctx.log(message)
        });
    };

    return {
        id: "harvest",
        cooldownMs: 5_000,
        hysteresis: 100,
        score: () => 0,
        eligible: view =>
            nearestHarvestTarget(view, context) !== null &&
            context.actor.getStats().staminaRatio > 0.25,
        describe: ({ view }) => {
            const target = nearestHarvestTarget(view, context);
            const homeWood = context.homeStock.amounts.wood ?? 0;
            const homeStone = context.homeStock.amounts.stone ?? 0;
            const deficit = target?.kind === "tree"
                ? Math.max(0, 50 - homeWood)
                : Math.max(0, 50 - homeStone);
            return {
                base: (woodCount(context) < 5 && target?.kind === "tree" ? 110 : 20) +
                    Math.min(80, deficit) +
                    Math.max(0, -(context.belongings[target?.kind === "tree" ? "wood" : "stone"] || 0)) * 40,
                factors: {
                    effort: 0.6,
                    distance: target ? Math.min(1, target.distance / 14) : 1
                },
                provides: target?.kind === "tree" ? ["wood"] : ["stone"]
            };
        },
        enter: ctx => {
            stage = "tree";
            const target = nearestHarvestTarget(ctx.view, context);
            if (target === null) return;
            activeTarget = target.id;
            context.board.emit("activity:start", { activity: "harvest", targetId: target.id });
            start(ctx, "harvest", target.id);
        },
        tick: ctx => {
            if (ctx.planStatus === "running") return "running";
            if (ctx.planStatus === "failure") return "failure";

            if (stage === "tree") {
                stage = "log";
                waitUntil = ctx.now + 5_000;
            }

            if (stage === "log") {
                const log = nearestLog(ctx.view, context);
                if (log !== null) {
                    start(ctx, "harvest", log.id);
                    return "running";
                }

                if (ctx.now < waitUntil) return "running";
                stage = "collect";
                waitUntil = ctx.now + 5_000;
            }

            if (stage === "collect") {
                const drop = nearestDrop(ctx.view, context.id);
                if (drop !== null) {
                    start(ctx, "pickup", drop.id);
                    return "running";
                }

                if (ctx.now < waitUntil) return "running";
            }

            return "success";
        },
        exit: (_ctx, reason) => {
            context.publish("activity", "idle");
            context.board.emit(reason === "success" ? "activity:complete" : "activity:failed", {
                activity: "harvest",
                targetId: activeTarget
            });
            activeTarget = null;
        }
    };
}

function nearestContainer(view: NpcView, blocked?: ReadonlySet<string>): NpcSensed | null {
    return (
        view.objects
            .filter(entry => entry.visible && !entry.isDead && entry.kind === "container")
            .filter(entry => blocked?.has(entry.id) !== true)
            .filter(entry => entry.distance <= 40)
            .sort((left, right) => {
                const leftBuilt = left.prefab?.startsWith("piece_chest") === true ? 0 : 1;
                const rightBuilt = right.prefab?.startsWith("piece_chest") === true ? 0 : 1;
                return leftBuilt - rightBuilt || left.distance - right.distance;
            })[0] ?? null
    );
}

interface StoredEntry {
    readonly container: NpcSensed;
    readonly item: InventoryItem;
}

function storageEntries(view: NpcView): StoredEntry[] {
    const containers = view.objects
        .filter(entry => entry.visible && !entry.isDead && entry.kind === "container")
        .filter(entry => entry.distance <= 40)
        .sort((left, right) => left.distance - right.distance)
        .slice(0, 4);

    const entries: StoredEntry[] = [];
    for (const container of containers) {
        try {
            const inventory = npc.container(container.id);
            for (const item of inventory.items) {
                if (item.stack > 0) entries.push({ container, item });
            }
        } catch {
            // A culled container has no live inventory; it will be available when loaded.
        }
    }
    return entries;
}

function stored(entries: readonly StoredEntry[], type: InventoryItem["type"]): StoredEntry | null {
    return entries.find(entry => entry.item.type === type) ?? null;
}

function storedRawFood(entries: readonly StoredEntry[]): StoredEntry | null {
    return entries.find(
        entry => entry.item.type === "food" && !entry.item.prefab.startsWith("Cooked")
    ) ?? null;
}

function storedPrefab(entries: readonly StoredEntry[], prefab: string): StoredEntry | null {
    return entries.find(entry => entry.item.prefab === prefab) ?? null;
}

const STORABLE_MATERIALS = new Set([
    "Wood",
    "Stone",
    "Resin",
    "Feathers",
    "Flint",
    "LeatherScraps",
    "BoneFragments",
    "BeechSeeds"
]);

export function storableItems(context: VillagerContext): InventoryItem[] {
    return context.actor
        .getInventory()
        .items.filter(item => item.stack > 0 && STORABLE_MATERIALS.has(item.prefab));
}

function woodStacks(context: VillagerContext): number {
    return context.actor.getInventory().items
        .filter(item => item.prefab === "Wood")
        .reduce((total, item) => total + item.stack, 0);
}

function storeGoal(context: VillagerContext): Goal {
    let activeTarget: string | null = null;
    const blocked = new Set<string>();

    return {
        id: "store",
        cooldownMs: 4_000,
        hysteresis: 20,
        score: () => 0,
        eligible: view =>
            storableItems(context).reduce((total, item) => total + item.stack, 0) >= 20 &&
            nearestContainer(view, blocked) !== null,
        describe: ({ view }) => {
            const container = nearestContainer(view, blocked);
            const total = storableItems(context).reduce((sum, item) => sum + item.stack, 0);
            return {
                base: 20 + Math.min(30, total),
                factors: {
                    distance: container ? Math.min(1, container.distance / 24) : 1,
                    effort: 0.4
                }
            };
        },
        enter: ctx => {
            const container = nearestContainer(ctx.view, blocked);
            if (container === null) return;
            activeTarget = container.id;
            context.publish("activity", "store");
            ctx.log(`store materials -> ${container.prefab ?? container.name} (${container.id})`);
            context.board.emit("activity:start", { activity: "store", targetId: container.id });
            ctx.run(
                [
                    ...storableItems(context).map(item => ({
                        interact: {
                            target: container.id,
                            verb: "store",
                            prefab: item.prefab,
                            count: item.stack
                        }
                    }))
                ],
                { timeoutMs: 60_000, log: message => ctx.log(message) }
            );
        },
        tick: ctx => {
            if (ctx.planStatus === "running") return "running";
            if (ctx.planStatus === "failure") {
                if (ctx.planReason === "no-access" && activeTarget !== null) {
                    blocked.add(activeTarget);
                    ctx.log(`store no-access; skipping ${activeTarget}`);
                    return "running";
                }
                return "failure";
            }
            const items = storableItems(context);
            if (items.length === 0) return "success";

            const container = nearestContainer(ctx.view, blocked);
            if (container === null) return "success";
            activeTarget = container.id;
            ctx.log(`store materials -> ${container.prefab ?? container.name} (${container.id})`);
            context.board.emit("activity:stage", { activity: "store", targetId: container.id });
            ctx.run(
                [
                    ...items.map(item => ({
                        interact: {
                            target: container.id,
                            verb: "store",
                            prefab: item.prefab,
                            count: item.stack
                        }
                    }))
                ],
                { timeoutMs: 60_000, log: message => ctx.log(message) }
            );
            return "running";
        },
        exit: (_ctx, reason) => {
            context.publish("activity", "idle");
            context.board.emit(reason === "success" ? "activity:complete" : "activity:failed", {
                activity: "store",
                targetId: activeTarget
            });
            activeTarget = null;
        }
    };
}

function needsStoredFood(context: VillagerContext, view: NpcView): boolean {
    if (!context.hasFood()) return stored(storageEntries(view), "food") !== null;
    return cookingStation(view) !== null && cookableFood(context) === 0 && storedRawFood(storageEntries(view)) !== null;
}

function withdrawGoal(context: VillagerContext): Goal {
    let activeTarget: string | null = null;

    return {
        id: "withdraw",
        cooldownMs: 2_000,
        hysteresis: 10,
        urgent: true,
        score: () => 0,
        eligible: view => needsStoredFood(context, view),
        describe: ({ view }) => {
            const entries = storageEntries(view);
            const provides: string[] = [];
            if (stored(entries, "food") !== null) provides.push("food");
            if (storedPrefab(entries, "Wood") !== null) provides.push("wood");
            if (storedPrefab(entries, "Stone") !== null) provides.push("stone");
            return {
                base: 900,
                factors: {},
                provides
            };
        },
        enter: ctx => {
            const entries = storageEntries(ctx.view);
            const wanted = context.hasFood()
                ? storedRawFood(entries)
                : stored(entries, "food");
            const target = wanted ?? storedPrefab(entries, "Wood") ?? entries[0] ?? null;
            if (target === null) return;
            activeTarget = target.item.prefab;
            context.publish("activity", "withdraw");
            ctx.log(
                `withdraw ${target.item.prefab} x${target.item.stack} <- ${target.container.prefab ?? target.container.name} (${target.container.id})`
            );
            context.board.emit("activity:start", { activity: "withdraw", targetId: target.container.id });
            ctx.run(
                [
                    {
                        interact: {
                            target: target.container.id,
                            verb: "take",
                            prefab: target.item.prefab,
                            count: target.item.stack
                        }
                    }
                ],
                { timeoutMs: 45_000, log: message => ctx.log(message) }
            );
        },
        tick: ctx => {
            if (ctx.planStatus === "running") return "running";
            if (ctx.planStatus === "failure") return "failure";
            if (needsStoredFood(context, ctx.view)) {
                const entries = storageEntries(ctx.view);
                const food = context.hasFood() ? storedRawFood(entries) : stored(entries, "food");
                if (food !== null) {
                    activeTarget = food.item.prefab;
                    ctx.log(
                        `withdraw ${food.item.prefab} x${food.item.stack} <- ${food.container.prefab ?? food.container.name} (${food.container.id})`
                    );
                    ctx.run(
                        [
                            {
                                interact: {
                                    target: food.container.id,
                                    verb: "take",
                                    prefab: food.item.prefab,
                                    count: food.item.stack
                                }
                            }
                        ],
                        { timeoutMs: 45_000, log: message => ctx.log(message) }
                    );
                    return "running";
                }
            }
            return "success";
        },
        exit: (_ctx, reason) => {
            context.publish("activity", "idle");
            context.board.emit(reason === "success" ? "activity:complete" : "activity:failed", {
                activity: "withdraw",
                targetId: activeTarget
            });
            activeTarget = null;
        }
    };
}

const PREY_PREFIXES = ["Boar", "Neck", "Deer", "Hare", "Chicken"] as const;

function preyRisk(prey: NpcSensed | null): number {
    const prefab = prey?.prefab ?? "";
    if (prefab.startsWith("Boar")) return 0.45;
    if (prefab.startsWith("Neck")) return 0.25;
    if (prefab.startsWith("Deer")) return 0.15;
    return 0.08;
}

function huntPrey(view: NpcView): NpcSensed | null {
    return (
        view.nearby
            .filter(entry => !entry.isPlayer && !entry.isNpc && !entry.isTamed)
            .filter(entry => !entry.isDead && entry.health > 0)
            .filter(entry => PREY_PREFIXES.some(prefix => (entry.prefab ?? "").startsWith(prefix)))
            .filter(entry => (entry.visible || entry.inRange) && entry.distance <= 28)
            .sort((left, right) => left.distance - right.distance)[0] ?? null
    );
}

function huntLoot(view: NpcView, owner: string, excludeId?: string | null): NpcSensed | null {
    const now = Date.now();
    return (
        view.objects
            .filter(entry => entry.kind === "item" && !entry.isDead && entry.visible)
            .filter(entry => entry.id !== excludeId)
            .filter(entry => resourceUsable(entry.id, owner, now))
            .filter(entry => entry.distance <= 12)
            .sort((left, right) => left.distance - right.distance)[0] ?? null
    );
}

interface HuntEligibility {
    readonly ok: boolean;
    readonly reason: string;
}

function huntEligibility(context: VillagerContext, view: NpcView): HuntEligibility {
    const prey = huntPrey(view);
    if (prey === null) {
        const nearby = view.nearby
            .filter(entry => !entry.isPlayer && !entry.isNpc)
            .slice(0, 4)
            .map(
                entry =>
                    `${entry.prefab ?? entry.name}:d=${entry.distance.toFixed(1)}` +
                    `:in=${entry.inRange ? 1 : 0}:vis=${entry.visible ? 1 : 0}` +
                    `:dead=${entry.isDead ? 1 : 0}:tame=${entry.isTamed ? 1 : 0}` +
                    `:hp=${entry.health.toFixed(0)}`
            )
            .join(",");
        return { ok: false, reason: `no-prey [${nearby || "none"}]` };
    }

    const stop = huntStop(context, view);
    if (stop !== null) return { ok: false, reason: stop };

    const weapon = context.actor.getInventory().equipment?.right;
    if (weapon === null || weapon === undefined) return { ok: false, reason: "no-weapon" };
    if (weapon.condition === "broken") return { ok: false, reason: "weapon-broken" };

    return {
        ok: true,
        reason: `prey=${prey.prefab ?? prey.name} dist=${prey.distance.toFixed(1)}`
    };
}

function huntStop(context: VillagerContext, view: NpcView): string | null {
    const stats = context.actor.getStats();
    if (stats.healthRatio < 0.35) return "wounded";
    if (stats.food.length >= 3) return "fed";
    const inventory = context.actor.getInventory();
    if (inventory.freeSlots <= 1) return "full";
    return null;
}

function huntGoal(context: VillagerContext): Goal {
    let preyId: string | null = null;
    let stage: "seek" | "loot" | "look" = "seek";
    let lookUntil = 0;
    let lootUntil = 0;
    let activeLoot: string | null = null;
    let nextLookAt = 0;
    let lastStop: string | null = null;
    let lastEligibility: string | null = null;

    return {
        id: "hunt",
        cooldownMs: 10_000,
        hysteresis: 50,
        commitMs: 15_000,
        score: () => 0,
        eligible: view => {
            const result = huntEligibility(context, view);
            if (result.reason !== lastEligibility) {
                lastEligibility = result.reason;
                context.log?.(`hunt eligibility: ${result.reason}`);
            }
            return result.ok;
        },
        describe: ({ view, stats }) => {
            const prey = huntPrey(view);
            return {
                base: 680 + (stats.Hunter ?? 0) * 50,
                factors: {
                    hunting: 0.8,
                    risk: preyRisk(prey),
                    distance: prey ? Math.min(1, prey.distance / 22) : 1,
                    effort: 0.5
                },
                provides: ["food"],
                satisfies: { food: prey?.prefab?.startsWith("Boar") ? 1.5 : 1 }
            };
        },
        enter: ctx => {
            const prey = huntPrey(ctx.view);
            if (prey === null) return;
            preyId = prey.id;
            stage = "seek";
            lastStop = null;
            context.actor.crouch?.(true);
            context.publish("activity", "hunt");
            ctx.log(`hunt crouch on; start ${prey.prefab ?? prey.name} (${prey.id})`);
            context.board.emit("activity:start", { activity: "hunt", targetId: prey.id });
        },
        tick: ctx => {
            if (ctx.planStatus === "running") return "running";
            if (ctx.planStatus === "failure") {
                if (stage === "loot") {
                    releaseResource(activeLoot, context.id, ctx.planReason === "not-loaded" ? "loading" : "invalid", ctx.now);
                    const next = huntLoot(ctx.view, context.id, activeLoot);
                    if (next !== null && reserveResource(next.id, context.id, ctx.now)) {
                        activeLoot = next.id;
                        ctx.log(`hunt loot retry ${next.prefab ?? next.name}`);
                        ctx.run([{ interact: { target: next.id, verb: "pickup" } }], {
                            timeoutMs: 30_000,
                            log: message => ctx.log(message)
                        });
                        return "running";
                    }

                    activeLoot = null;
                    stage = "look";
                    lookUntil = ctx.now + 2_000;
                    return "running";
                }

                return "failure";
            }

            const stop = huntStop(context, ctx.view);
            if (stop !== null) {
                lastStop = stop;
                ctx.log(`hunt stop: ${stop}`);
                return "success";
            }

            if (stage === "seek") {
                const prey = ctx.view.nearby.find(entry => entry.id === preyId);
                if (!prey || prey.isDead) {
                    ctx.log(`hunt target gone: ${preyId}`);
                    stage = "loot";
                    lootUntil = ctx.now + 5_000;
                    return "running";
                }

                if (prey.distance > 2.8) {
                    context.actor.moveTo(prey.pos.x, prey.pos.y, prey.pos.z, false);
                    return "running";
                }

                const result = context.actor.attack?.(prey.id, false);
                if (result?.ok) {
                    ctx.log(`hunt attack ${prey.prefab ?? prey.name}`);
                    return "running";
                }

                if (result?.reason === "out-of-range") {
                    context.actor.moveTo(prey.pos.x, prey.pos.y, prey.pos.z, false);
                    return "running";
                }

                if (result?.reason === "target-missing") {
                    stage = "loot";
                    return "running";
                }

                ctx.log(`hunt attack failed: ${result?.reason ?? "unsupported"}`);
                return "failure";
            }

            if (stage === "loot") {
                const drop = huntLoot(ctx.view, context.id);
                if (drop !== null && reserveResource(drop.id, context.id, ctx.now)) {
                    activeLoot = drop.id;
                    ctx.log(`hunt loot ${drop.prefab ?? drop.name}`);
                    ctx.run([{ interact: { target: drop.id, verb: "pickup" } }], {
                        timeoutMs: 30_000,
                        log: message => ctx.log(message)
                    });
                    return "running";
                }

                if (ctx.now < lootUntil) return "running";

                activeLoot = null;
                stage = "look";
                lookUntil = ctx.now + 3_000;
                nextLookAt = 0;
                ctx.log("hunt look around");
                return "running";
            }

            const loot = huntLoot(ctx.view, context.id);
            if (loot !== null && reserveResource(loot.id, context.id, ctx.now)) {
                activeLoot = loot.id;
                ctx.log(`hunt loot ${loot.prefab ?? loot.name}`);
                stage = "loot";
                lootUntil = ctx.now + 5_000;
                return "running";
            }

            const next = huntPrey(ctx.view);
            if (next !== null) {
                preyId = next.id;
                stage = "seek";
                ctx.log(`hunt spotted ${next.prefab ?? next.name}`);
                return "running";
            }

            if (ctx.now < lookUntil) {
                if (ctx.now >= nextLookAt) {
                    nextLookAt = ctx.now + 700;
                    context.actor.lookAt(
                        ctx.view.x + Math.random() * 8 - 4,
                        ctx.view.y,
                        ctx.view.z + Math.random() * 8 - 4
                    );
                }
                return "running";
            }

            return "success";
        },
        exit: (_ctx, reason) => {
            clearResource(activeLoot, context.id);
            activeLoot = null;
            context.actor.crouch?.(false);
            _ctx.log(`hunt crouch off (${reason})`);
            context.publish("activity", "idle");
            context.board.emit(reason === "success" ? "activity:complete" : "activity:failed", {
                activity: "hunt",
                targetId: preyId,
                stop: lastStop
            });
            preyId = null;
        }
    };
}

function cookingStation(view: NpcView): NpcSensed | null {
    return (
        view.objects
            .filter(entry => entry.visible && !entry.isDead && entry.kind === "cooking")
            .filter(entry => entry.distance <= 40)
            .sort((left, right) => left.distance - right.distance)[0] ?? null
    );
}

export function cookableFood(context: VillagerContext): number {
    return context.actor.getInventory().items.filter(
        item =>
            item.type === "food" &&
            item.stack > 0 &&
            !item.prefab.startsWith("Cooked")
    ).length;
}

function cookGoal(context: VillagerContext): Goal {
    let activeStation: string | null = null;
    let startedAt = 0;
    let seenEventAt: unknown = undefined;

    const event = (): Record<string, unknown> | null => {
        const value = context.board.get?.("cook");
        return value !== null && typeof value === "object" ? (value as Record<string, unknown>) : null;
    };

    return {
        id: "cook",
        cooldownMs: 5_000,
        hysteresis: 80,
        score: () => 0,
        eligible: view => cookingStation(view) !== null && cookableFood(context) > 0,
        describe: ({ view }) => {
            const station = cookingStation(view);
            return {
                base: 60,
                factors: {
                    cooking: 0.5,
                    distance: station ? Math.min(1, station.distance / 20) : 1,
                    effort: 0.4
                }
            };
        },
        enter: ctx => {
            const station = cookingStation(ctx.view);
            if (station === null) return;
            activeStation = station.id;
            startedAt = ctx.now;
            const current = event();
            seenEventAt = current?.at;
            context.publish("activity", "cook");
            ctx.log(`cook start ${station.prefab ?? station.name} (${station.id})`);
            context.board.emit("activity:start", { activity: "cook", targetId: station.id });
            ctx.run([{ interact: { target: station.id, verb: "cook" } }], {
                timeoutMs: 30_000,
                log: message => ctx.log(message)
            });
        },
        tick: ctx => {
            if (ctx.planStatus === "running") return "running";
            if (ctx.planStatus === "failure") return "failure";

            const current = event();
            if (current !== null && current.at !== seenEventAt) {
                if (current.ok === true) {
                    ctx.log(`cook done ${String(current.item ?? "food")} x${String(current.count ?? 1)}`);
                    return "success";
                }
                ctx.log(`cook failed: ${String(current.reason ?? "unknown")}`);
                return "failure";
            }

            if (ctx.now - startedAt > 90_000) {
                ctx.log("cook timeout waiting for station");
                return "failure";
            }

            return "running";
        },
        exit: (_ctx, reason) => {
            context.publish("activity", "idle");
            context.board.emit(reason === "success" ? "activity:complete" : "activity:failed", {
                activity: "cook",
                targetId: activeStation
            });
            activeStation = null;
        }
    };
}

function collectGoal(context: VillagerContext): Goal {
    let activeTarget: string | null = null;
    let activeEquip: string | null = null;
    let holdUntil = 0;
    let loadingAttempts = 0;

    return {
        id: "collect",
        cooldownMs: 1_000,
        hysteresis: 30,
        commitMs: 8_000,
        score: () => 0,
        eligible: view =>
            context.board.get?.("activity") !== "hunt" && nearestUsefulDrop(view, context) !== null,
        describe: ({ view }) => {
            const target =
                activeTarget === null
                    ? nearestUsefulDrop(view, context)
                    : (view.objects.find(entry => entry.id === activeTarget) ?? nearestUsefulDrop(view, context));
            return {
                base: target !== null ? 850 : 60,
                factors: {
                    distance: target ? Math.min(1, target.distance / 12) : 1
                },
                provides: ["wood"]
            };
        },
        enter: ctx => {
            const drop = nearestUsefulDrop(ctx.view, context);
            if (drop === null || !reserveResource(drop.id, context.id, ctx.now)) return;
            const decision = itemDecision(drop, context);
            activeTarget = drop.id;
            activeEquip = decision.equip ? drop.prefab : null;
            context.debug?.(`collect keep ${drop.prefab ?? drop.name}: ${decision.reason}`);
            context.publish("activity", "collect");
            ctx.log(`collect pickup ${drop.prefab ?? drop.name} (${drop.id})`);
            context.board.emit("activity:start", { activity: "collect", targetId: drop.id });
            ctx.run([{ interact: { target: drop.id, verb: "pickup" } }], {
                timeoutMs: 30_000,
                log: message => ctx.log(message)
            });
        },
        tick: ctx => {
            if (ctx.planStatus === "running") return "running";
            if (ctx.now < holdUntil) return "running";
            if (ctx.planStatus === "failure") {
                const reason = ctx.planReason ?? "unknown";
                releaseResource(activeTarget, context.id, reason === "not-loaded" ? "loading" : "invalid", ctx.now);
                ctx.log(`collect ${reason}; releasing ${activeTarget ?? "target"}`);

                if (reason === "not-loaded" && loadingAttempts < 3) {
                    loadingAttempts++;
                    holdUntil = ctx.now + 1_200;
                    context.board.emit("activity:stage", {
                        activity: "collect",
                        stage: "loading",
                        targetId: activeTarget
                    });
                    return "running";
                }

                loadingAttempts = 0;
                const next = nearestUsefulDrop(ctx.view, context, activeTarget);
                if (next === null) {
                    activeTarget = null;
                    return "success";
                }
                if (!reserveResource(next.id, context.id, ctx.now)) return "running";
                const nextDecision = itemDecision(next, context);
                activeTarget = next.id;
                activeEquip = nextDecision.equip ? next.prefab : null;
                context.debug?.(`collect keep ${next.prefab ?? next.name}: ${nextDecision.reason}`);
                ctx.log(`collect retry ${next.prefab ?? next.name} (${next.id})`);
                ctx.run([{ interact: { target: next.id, verb: "pickup" } }], {
                    timeoutMs: 30_000,
                    log: message => ctx.log(message)
                });
                return "running";
            }
            loadingAttempts = 0;
            if (activeEquip !== null) {
                const inventory = context.actor.getInventory();
                const item = inventory.items.find(candidate => candidate.prefab === activeEquip);
                if (item !== undefined) {
                    const equipped = inventory.equip(item);
                    context.debug?.(
                        `collect equip ${activeEquip}: ${equipped.ok ? "ok" : equipped.reason ?? "rejected"}`
                    );
                }
                activeEquip = null;
            }
            const drop = nearestUsefulDrop(ctx.view, context);
            if (drop === null) {
                clearResource(activeTarget, context.id);
                activeTarget = null;
                return "success";
            }
            if (!reserveResource(drop.id, context.id, ctx.now)) return "running";
            const decision = itemDecision(drop, context);
            activeTarget = drop.id;
            activeEquip = decision.equip ? drop.prefab : null;
            context.debug?.(`collect keep ${drop.prefab ?? drop.name}: ${decision.reason}`);
            context.board.emit("activity:stage", { activity: "collect", targetId: drop.id });
            ctx.run([{ interact: { target: drop.id, verb: "pickup" } }], {
                timeoutMs: 30_000,
                log: message => ctx.log(message)
            });
            return "running";
        },
        exit: (_ctx, reason) => {
            clearResource(activeTarget, context.id);
            context.publish("activity", "idle");
            context.board.emit(reason === "success" ? "activity:complete" : "activity:failed", {
                activity: "collect",
                targetId: activeTarget
            });
            activeTarget = null;
            activeEquip = null;
        }
    };
}

export function createActivities(
    context: VillagerContext,
    trees: Record<VillagerActivity, string>
): Goal[] {
    return [
        activityGoal(context, trees, "wander", {
            cooldownMs: 3_000,
            commitMs: 3_000,
            describe: view => ({
                base: 8,
                factors: {
                    exploration: 0.35,
                    novelty: 0.2,
                    effort: 0.1,
                    distance: Math.min(1, context.distanceFromHome(view) / 30)
                }
            })
        }),

        activityGoal(context, trees, "inspect", {
            cooldownMs: 20_000,
            commitMs: 4_000,
            satisfies: { curiosity: -0.35 },
            eligible: (view, ctx) =>
                ctx.unknownObject(view) !== null && ctx.needs.curiosity > 0.55,
            describe: (view, ctx) => {
                const object = ctx.unknownObject(view);
                return {
                    base: 20 + ctx.needs.curiosity * 20,
                    factors: {
                        novelty: 1,
                        effort: 0.2,
                        distance: object ? Math.min(1, object.distance / 14) : 0
                    }
                };
            }
        }),

        activityGoal(context, trees, "socialize", {
            cooldownMs: 25_000,
            commitMs: 5_000,
            satisfies: { social: -0.4 },
            eligible: (view, ctx) =>
                ctx.nearbyPlayer(view) !== null && ctx.needs.social > 0.55,
            describe: (view, ctx) => {
                const player = ctx.nearbyPlayer(view);
                return {
                    base: 20 + ctx.needs.social * 20,
                    factors: {
                        social: 1,
                        distance: player ? Math.min(1, player.distance / 18) : 0
                    }
                };
            }
        }),

        activityGoal(context, trees, "forage", {
            cooldownMs: 20_000,
            commitMs: 5_000,
            satisfies: { curiosity: -0.25 },
            eligible: (_view, ctx) => ctx.needs.energy > 0.35,
            describe: (view, ctx) => ({
                base: 10 + ctx.needs.curiosity * 20 + (view.environment?.isDay === false ? -10 : 0),
                factors: {
                    exploration: 1,
                    novelty: 0.4,
                    effort: 0.5,
                    distance: 0.6
                }
            })
        }),

        activityGoal(context, trees, "explore", {
            cooldownMs: 25_000,
            commitMs: 5_000,
            satisfies: { curiosity: -0.3 },
            eligible: (view, ctx) =>
                ctx.needs.energy > 0.4 && ctx.unknownObject(view) === null,
            describe: (view, ctx) => ({
                base: 30 + ctx.needs.curiosity * 15 + (view.environment?.isDay === false ? -10 : 0),
                factors: {
                    exploration: 1.2,
                    novelty: 0.6,
                    effort: 0.7,
                    distance: 0.8
                }
            }),
            state: () => ({ waitSeconds: 3 })
        }),

        activityGoal(context, trees, "seekHome", {
            cooldownMs: 8_000,
            commitMs: 3_000,
            eligible: (view, ctx) => ctx.findShelter(view) !== null,
            describe: (view, ctx) => {
                const shelter = ctx.findShelter(view);
                return {
                    base: 20,
                    factors: {
                        effort: 0.2,
                        distance: shelter ? Math.min(1, shelter.distance / 14) : 0
                    }
                };
            }
        }),

        activityGoal(context, trees, "return", {
            cooldownMs: 1_000,
            commitMs: 3_000,
            eligible: (view, ctx) =>
                ctx.homesick > 0.2 && ctx.distanceFromHome(view) > 4,
            describe: (view, ctx, stats) => {
                const away = ctx.distanceFromHome(view);
                const explorer = stats.Explorer ?? 0.5;
                const threshold = 0.55 + explorer * 0.3;
                const urgency =
                    ctx.homesick <= threshold
                        ? 0
                        : (ctx.homesick - threshold) / Math.max(0.01, 1 - threshold);

                return {
                    base: urgency > 0 ? 250 + urgency * 550 : 0,
                    factors: {
                        distance: Math.min(1, away / 60),
                        effort: 0.2
                    }
                };
            }
        }),

        activityGoal(context, trees, "rest", {
            cooldownMs: 20_000,
            hysteresis: 500,
            satisfies: { energy: 0.35 },
            describe: view => {
                const night = view.environment?.isDay === false;
                const stamina = context.actor.getStats().staminaRatio;
                let base = 0;
                if (stamina < 0.15) base = 800;
                else if (context.needs.energy < 0.2) base = 800;
                else if (night && context.needs.energy < 0.9) {
                    base = 500 + (1 - context.needs.energy) * 200;
                }

                return {
                    base,
                    factors: { effort: 0.1 }
                };
            },
            state: () => ({ waitSeconds: 25 })
        }),

        activityGoal(context, trees, "flee", {
            cooldownMs: 1_000,
            urgent: true,
            satisfies: { energy: -0.05 },
            eligible: (view, ctx) => {
                const threat = ctx.threat(view);
                if (threat === null) return false;
                const prefab = threat.prefab ?? "";
                if (PREY_PREFIXES.some(prefix => prefab.startsWith(prefix))) return false;
                return true;
            },
            describe: (view, ctx) => ({
                base: ctx.threat(view) !== null ? 1_000 : 0,
                factors: {}
            }),
            state: () => ({ waitSeconds: 2 })
        }),

        activityGoal(context, trees, "eat", {
            cooldownMs: 30_000,
            urgent: true,
            eligible: (view, ctx) =>
                ctx.actor.getStats().food.length < 3 && (ctx.hasFood() || ctx.hasFoodSource(view)),
            describe: (view, ctx) => ({
                base: ctx.hasFood() ? 900 : 800,
                factors: {
                    cooking: 0.5,
                    distance: ctx.hasFoodSource(view) ? 0.4 : 0
                }
            }),
            state: () => ({ waitSeconds: 1 })
        }),

        harvestGoal(context),
        collectGoal(context),
        storeGoal(context),
        withdrawGoal(context),
        cookGoal(context),
        huntGoal(context)
    ];
}
