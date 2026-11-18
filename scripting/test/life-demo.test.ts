import { describe, expect, test } from "bun:test";
import type {
    ActionResult,
    InventoryItem,
    NpcAction,
    NpcPos,
    NpcSensed,
    NpcStats,
    NpcView
} from "../sdk/api/types";
import type { Inventory } from "../sdk/api/generated";
import type { BehaviorActionContext, BehaviorOutcome } from "../testing/life-demo/lib/behaviour";
import {
    createActionHandler,
    createConditionHandler,
    type VillagerActionHost,
    type VillagerActor,
    type VillagerBoard,
    type VillagerNeeds
} from "../testing/life-demo/lib/actions";
import {
    createVillagerAI,
    profileFor,
    type VillagerActivity,
    type VillagerContext
} from "../testing/life-demo/definitions";
import { VillagerRuntime } from "../testing/life-demo/lib/villager";

const ok: ActionResult = { ok: true, reason: null };

const emptyStats: NpcStats = {
    health: 100,
    maxHealth: 100,
    healthRatio: 1,
    stamina: 100,
    maxStamina: 100,
    staminaRatio: 1,
    eitr: 0,
    maxEitr: 0,
    eitrRatio: 0,
    food: [],
    statusEffects: [],
    hungry: true
};

function emptyInventory(): Inventory {
    const itemResult = { ok: false, reason: "no-item" };
    return {
        available: true,
        kind: "body",
        writable: true,
        weight: 0,
        capacity: 32,
        freeSlots: 32,
        items: [] as InventoryItem[],
        equipment: {
            right: null,
            left: null,
            helmet: null,
            chest: null,
            legs: null,
            shoulder: null,
            utility: null,
            trinket: null
        },
        has: () => false,
        count: () => 0,
        add: () => itemResult,
        remove: () => itemResult,
        use: () => itemResult,
        consume: () => itemResult,
        equip: () => itemResult,
        unequip: () => itemResult,
        drop: () => itemResult,
        canUse: () => false,
        canConsume: () => false,
        canEquip: () => false
    } as unknown as Inventory;
}

class FakeActor implements VillagerActor {
    moveTo(): ActionResult {
        return ok;
    }

    stop(): ActionResult {
        return ok;
    }

    wait(): ActionResult {
        return ok;
    }

    lookAt(): ActionResult {
        return ok;
    }

    face(): ActionResult {
        return ok;
    }

    idle(): ActionResult {
        return ok;
    }

    pick(): ActionResult {
        return ok;
    }

    getStats(): NpcStats {
        return emptyStats;
    }

    getInventory(): Inventory {
        return emptyInventory();
    }
}

class FakeBoard implements VillagerBoard {
    readonly values: Record<string, unknown> = {};
    readonly signals: Array<{ name: string; data: unknown }> = [];

    set(key: string, value: unknown): void {
        this.values[key] = value;
    }

    emit(name: string, data?: unknown): void {
        this.signals.push({ name, data });
    }
}

class FakeContext implements VillagerContext {
    readonly actor = new FakeActor();
    readonly needs: VillagerNeeds;
    readonly board = new FakeBoard();
    readonly home: NpcPos = { x: 0, y: 0, z: 0 };
    readonly sheltered = false;
    readonly homesick = 0;
    readonly activities: string[] = [];
    readonly trees: string[] = [];
    readonly exits: Array<"success" | "failure" | "preempted"> = [];
    readonly satisfied: Array<Readonly<Record<string, number>>> = [];
    status: BehaviorOutcome | "cancelled" = "success";

    constructor(name: string) {
        this.needs = { ...profileFor(name).needs } as VillagerNeeds;
    }

    distanceFromHome(view: NpcView): number {
        return Math.hypot(view.x - this.home.x, view.z - this.home.z);
    }

    hasFood(): boolean {
        return false;
    }

    hasFoodSource(view: NpcView): boolean {
        return view.objects.some(entry => entry.kind === "pickable" && entry.ready && entry.food);
    }

    nearbyPlayer(view: NpcView): NpcSensed | null {
        return view.nearby.find(entry => entry.isPlayer && entry.visible && !entry.isDead) ?? null;
    }

    unknownObject(view: NpcView): NpcSensed | null {
        return view.objects.find(entry => entry.visible && !entry.isDead && entry.distance <= 14) ?? null;
    }

    threat(view: NpcView): NpcSensed | null {
        return view.nearby.find(entry => entry.enemy && entry.visible && !entry.isDead) ?? null;
    }

    findShelter(): NpcSensed | null {
        return null;
    }

    startActivity(activity: VillagerActivity, treeId: string): void {
        this.activities.push(activity);
        this.trees.push(treeId);
    }

    tickActivity(): BehaviorOutcome | "cancelled" {
        return this.status;
    }

    endActivity(reason: "success" | "failure" | "preempted"): void {
        this.exits.push(reason);
    }

    satisfy(effects: Readonly<Record<string, number>>): void {
        this.satisfied.push({ ...effects });
    }

    publish(): void {}
}

class FakeActionHost implements VillagerActionHost {
    readonly actor = new FakeActor();
    readonly board = new FakeBoard();
    readonly random = () => 0.5;
    readonly memory: Record<string, unknown> = {};
    readonly visited = new Set<string>();
    readonly blocked = new Map<string, number>();
    home: NpcPos = { x: 0, y: 10, z: 0 };
    sheltered = false;
    activeTarget: NpcSensed | null = null;
    activeDestination: NpcPos | null = null;

    markDirty(): void {}

    publish(): void {}

    setHome(home: NpcPos, shelterId: string): void {
        this.home = home;
        this.sheltered = true;
        this.memory.shelter = shelterId;
    }
}

function treeContext(current: NpcView, args: Record<string, unknown> = {}): BehaviorActionContext {
    return {
        args,
        state: { senses: current },
        this: null,
        deltaTime: 0.5,
        interrupted: false
    };
}

const trees = {
    wander: "wander",
    inspect: "inspect",
    socialize: "socialize",
    forage: "forage",
    explore: "explore",
    seekHome: "seekHome",
    return: "return",
    rest: "rest",
    flee: "flee",
    eat: "eat"
} as Record<VillagerActivity, string>;

function ai(name: string, context: FakeContext) {
    return createVillagerAI({
        context,
        name,
        trees,
        random: () => 0.5,
        store: { load: () => undefined, save: () => {} }
    });
}

function view(overrides: Partial<NpcView> = {}): NpcView {
    return {
        id: "npc-1",
        kind: "villager",
        x: 0,
        y: 10,
        z: 0,
        health: 100,
        maxHealth: 100,
        isAlive: true,
        faction: "Players",
        isTamed: false,
        level: 1,
        home: { x: 0, y: 10, z: 0 },
        ready: true,
        swimming: false,
        self: emptyStats,
        action: idle,
        environment: { day: 1, isDay: true, biome: "Meadows", weather: null },
        nearby: [],
        remembered: [],
        objects: [],
        ...overrides
    };
}

function sensed(overrides: Partial<NpcSensed>): NpcSensed {
    return {
        id: "sensed-1",
        name: "Sensed",
        kind: "",
        prefab: null,
        faction: "",
        pos: { x: 0, y: 10, z: 0 },
        rel: { x: 0, y: 0, z: 0 },
        distance: 1,
        bearing: 0,
        visible: true,
        inRange: true,
        ready: true,
        noise: 0,
        audible: false,
        food: false,
        enemy: false,
        isPlayer: false,
        isNpc: false,
        isTamed: false,
        isDead: false,
        level: 1,
        isObject: false,
        health: 100,
        maxHealth: 100,
        firstSeen: 0,
        lastSeen: 0,
        seenCount: 1,
        ...overrides
    };
}

const idle: NpcAction = {
    kind: "idle",
    status: "idle",
    reason: null,
    targetId: null,
    target: null,
    progress: null
};

describe("utility-driven villagers", () => {
    test("Bob socializes while Astrid inspects the same scene", () => {
        const player = sensed({
            id: "player-1",
            name: "Astrid",
            isPlayer: true,
            distance: 6,
            pos: { x: 6, y: 10, z: 0 }
        });
        const object = sensed({
            id: "chest-1",
            name: "Chest",
            kind: "container",
            isObject: true,
            distance: 4,
            pos: { x: 4, y: 10, z: 0 }
        });
        const scene = view({ nearby: [player], objects: [object] });

        const bobContext = new FakeContext("Bob");
        expect(ai("Bob", bobContext).tick(scene)).toBe("socialize");
        expect(bobContext.trees).toEqual(["socialize"]);

        const astridContext = new FakeContext("Astrid");
        expect(ai("Astrid", astridContext).tick(scene)).toBe("inspect");
        expect(astridContext.trees).toEqual(["inspect"]);
    });

    test("tired villagers rest at night and apply the declared satisfaction", () => {
        const context = new FakeContext("Bob");
        context.needs.energy = 0.2;
        const night = view({
            environment: { day: 1, isDay: false, biome: "Meadows", weather: null }
        });

        expect(ai("Bob", context).tick(night)).toBe("rest");
        expect(context.exits).toEqual(["success"]);
        expect(context.satisfied).toContainEqual({ energy: 0.35 });
    });

    test("runtime satisfaction changes the fast needs state", () => {
        const runtime = new VillagerRuntime({
            actor: new FakeActor(),
            runtime: {
                instantiate: () => "tree",
                tick: () => "running",
                release: () => {}
            },
            home: { x: 0, y: 0, z: 0 }
        });
        runtime.needs.energy = 0.5;
        runtime.needs.social = 0.8;
        runtime.needs.curiosity = 0.5;

        runtime.satisfy({ energy: 0.2, social: -0.3, curiosity: -0.1 });

        expect(runtime.needs.energy).toBeCloseTo(0.7);
        expect(runtime.needs.social).toBeCloseTo(0.5);
        expect(runtime.needs.curiosity).toBeCloseTo(0.4);
    });

    test("a visible enemy selects flee", () => {
        const context = new FakeContext("Astrid");
        const enemy = sensed({
            id: "enemy-1",
            enemy: true,
            distance: 4,
            pos: { x: 4, y: 10, z: 0 }
        });

        expect(ai("Astrid", context).tick(view({ nearby: [enemy] }))).toBe("flee");
    });

    test("tree conditions use the villager runtime facts", () => {
        const host = new FakeActionHost();
        const conditions = createConditionHandler(host);

        expect(conditions("at-home", treeContext(view()))).toBe(true);
        expect(conditions("at-home", treeContext(view({ x: 10 })))).toBe(false);

        const food = sensed({
            kind: "pickable",
            food: true,
            ready: true,
            distance: 5,
            pos: { x: 5, y: 10, z: 0 }
        });
        expect(conditions("has-food-source", treeContext(view({ objects: [food] })))).toBe(true);
    });

    test("tree actions select targets and claim a home", () => {
        const host = new FakeActionHost();
        const actions = createActionHandler(host);
        const player = sensed({
            id: "player-1",
            isPlayer: true,
            distance: 6,
            pos: { x: 6, y: 11, z: 0 }
        });
        const context = treeContext(view({ nearby: [player] }));

        expect(actions("select-target", { ...context, args: { kind: "player", maxDistance: 18 } })).toBe(
            "success"
        );
        expect(host.activeTarget?.id).toBe("player-1");
        expect((context.state.target as NpcSensed).id).toBe("player-1");

        expect(actions("claim-home", context)).toBe("success");
        expect(host.home).toEqual({ x: 6, y: 11, z: 0 });
        expect(host.sheltered).toBe(true);
    });

    test("tree actions issue movement through the actor", () => {
        const host = new FakeActionHost();
        const actions = createActionHandler(host);
        const context = treeContext(view());

        expect(actions("select-point", { ...context, args: { around: "home", min: 3, max: 8 } })).toBe(
            "success"
        );
        const destination = context.state.destination as NpcPos;
        expect(host.activeDestination).toEqual(destination);

        expect(actions("move-to", context)).toBe("running");
        expect(context.state.moveIssued).toBe(true);
    });
});
