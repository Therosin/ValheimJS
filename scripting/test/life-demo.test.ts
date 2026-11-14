import { describe, expect, test } from "bun:test";
import type { NpcIntent, NpcPos, NpcSensed, NpcView } from "../sdk/api/types";
import {
    VillagerBrain,
    type BehaviorHandlers,
    type BehaviorOutcome,
    type BehaviorRuntime,
    type VillagerActor,
    type VillagerActivity
} from "../testing/life-demo/lib/brain";

class FakeActor implements VillagerActor {
    readonly calls: string[] = [];

    moveTo(x: number, y: number, z: number, run: boolean): boolean {
        this.calls.push(`moveTo ${x},${y},${z} run=${run}`);
        return true;
    }

    wait(seconds: number): boolean {
        this.calls.push(`wait ${seconds}`);
        return true;
    }

    lookAt(x: number, y: number, z: number): boolean {
        this.calls.push(`lookAt ${x},${y},${z}`);
        return true;
    }

    face(id: string): boolean {
        this.calls.push(`face ${id}`);
        return true;
    }

    idle(): boolean {
        this.calls.push("idle");
        return true;
    }
}

class FakeRuntime implements BehaviorRuntime {
    readonly started: string[] = [];
    readonly released: string[] = [];
    status: BehaviorOutcome | "cancelled" = "running";
    handlers: BehaviorHandlers | null = null;
    state: Record<string, unknown> = {};
    private next = 0;

    instantiate(treeId: string, options: BehaviorHandlers & { this?: unknown; state?: Record<string, unknown> }) {
        this.started.push(treeId);
        this.handlers = options;
        this.state = options.state ?? {};
        return `instance-${++this.next}`;
    }

    tick(): BehaviorOutcome | "cancelled" {
        return this.status;
    }

    release(instanceId: string): void {
        this.released.push(instanceId);
    }

    act(name: string, args: Record<string, unknown> = {}, view?: NpcView): BehaviorOutcome {
        if (!this.handlers) throw new Error("no active behaviour");
        const state = { ...this.state, ...(view ? { senses: view } : {}) };
        const result = this.handlers.action(name, {
            args,
            state,
            this: null,
            deltaTime: 0.5,
            interrupted: false
        });
        this.state = state;
        return result;
    }

    condition(name: string, view?: NpcView): boolean {
        if (!this.handlers) throw new Error("no active behaviour");
        const state = { ...this.state, ...(view ? { senses: view } : {}) };
        return this.handlers.condition(name, {
            args: {},
            state,
            this: null,
            deltaTime: 0.5,
            interrupted: false
        });
    }
}

const behaviors: Record<VillagerActivity, string> = {
    wander: "wander",
    inspect: "inspect",
    socialize: "socialize",
    forage: "forage",
    seekHome: "seekHome",
    return: "return",
    rest: "rest",
    flee: "flee"
};

const idle: NpcIntent = { kind: "idle", status: "idle", reason: null, targetId: null, target: null };

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
        intent: idle,
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

function brain(
    runtime: BehaviorRuntime,
    options: {
        memory?: Record<string, unknown>;
        needs?: { energy?: number; social?: number; curiosity?: number };
        home?: NpcPos;
    } = {}
) {
    return new VillagerBrain({
        actor: new FakeActor(),
        runtime,
        behaviors,
        home: options.home ?? { x: 0, y: 10, z: 0 },
        memory: options.memory,
        needs: { curiosity: 0.4, ...options.needs },
        random: () => 0.5
    });
}

describe("villager brain", () => {
    test("starts the selected YAML activity", () => {
        const runtime = new FakeRuntime();
        const current = brain(runtime);
        expect(current.tick(view(), 1000)).toBe("wander");
        expect(runtime.started).toEqual(["wander"]);
    });

    test("flees a visible enemy", () => {
        const runtime = new FakeRuntime();
        const current = brain(runtime);
        const enemy = sensed({ id: "enemy", enemy: true, distance: 4, pos: { x: 4, y: 10, z: 0 } });
        current.tick(view({ nearby: [enemy] }), 1000);
        expect(runtime.started).toEqual(["flee"]);
        expect(runtime.act("select-flee-point", {}, view({ nearby: [enemy] }))).toBe("success");
        expect(runtime.state.run).toBe(true);
    });

    test("rests at night and lets the activity set the wait duration", () => {
        const runtime = new FakeRuntime();
        const current = brain(runtime, { needs: { energy: 0.2 } });
        const night = view({ environment: { day: 1, isDay: false, biome: "Meadows", weather: null } });
        current.tick(night, 1000);
        expect(runtime.started).toEqual(["rest"]);
        expect(typeof runtime.state.waitSeconds).toBe("number");
        expect(current.needs.energy).toBeGreaterThan(0);
    });

    test("socializes with a nearby player", () => {
        const runtime = new FakeRuntime();
        const current = brain(runtime, { needs: { social: 0.9, curiosity: 0.1 } });
        const player = sensed({ id: "player-1", name: "Astrid", isPlayer: true, distance: 6, pos: { x: 6, y: 10, z: 0 } });
        current.tick(view({ nearby: [player] }), 1000);
        expect(runtime.started).toEqual(["socialize"]);
        expect(runtime.act("select-target", { kind: "player", maxDistance: 18 }, view({ nearby: [player] }))).toBe("success");
        expect((runtime.state.target as NpcSensed).id).toBe("player-1");
        expect(runtime.act("remember-meeting")).toBe("success");
        expect(current.snapshotMemory().lastMet).toMatchObject({ id: "player-1", name: "Astrid" });
    });

    test("inspects an unknown object and marks it visited", () => {
        const runtime = new FakeRuntime();
        const current = brain(runtime, { needs: { social: 0.1, curiosity: 0.9 } });
        const chest = sensed({ id: "chest-1", name: "Chest", kind: "container", isObject: true, distance: 4, pos: { x: 4, y: 10, z: 0 } });
        const currentView = view({ objects: [chest] });
        current.tick(currentView, 1000);
        expect(runtime.started).toEqual(["inspect"]);
        expect(runtime.act("select-target", { kind: "object", maxDistance: 14 }, currentView)).toBe("success");
        expect(runtime.act("mark-visited")).toBe("success");
        expect(current.snapshotMemory().visited).toContain("chest-1");
    });

    test("foraging ends homesick and selects return", () => {
        const runtime = new FakeRuntime();
        const current = brain(runtime, { needs: { curiosity: 0.95, social: 0.9, energy: 0.9 } });
        current.tick(view(), 1000);
        expect(runtime.started).toEqual(["forage"]);

        runtime.status = "success";
        current.tick(view({ x: 20 }), 2000);
        runtime.status = "running";

        const player = sensed({ id: "player-1", isPlayer: true, distance: 5, pos: { x: 25, y: 10, z: 0 } });
        current.tick(view({ x: 20, nearby: [player] }), 3000);
        expect(runtime.started.at(-1)).toBe("return");
    });

    test("seeks an empty bed and claims it through the action primitives", () => {
        const runtime = new FakeRuntime();
        const current = brain(runtime, { needs: { social: 0.1, curiosity: 0.4, energy: 0.9 } });
        const bed = sensed({
            id: "bed-1",
            name: "Bed",
            kind: "bed",
            isObject: true,
            distance: 9,
            pos: { x: 9, y: 11, z: 0 }
        });
        const currentView = view({ objects: [bed] });
        current.tick(currentView, 1000);
        expect(runtime.started).toEqual(["seekHome"]);
        expect(runtime.act("select-shelter", {}, currentView)).toBe("success");
        expect(runtime.act("claim-home")).toBe("success");
        expect(current.isSheltered).toBe(true);
        expect(current.homePosition).toEqual({ x: 9, y: 11, z: 0 });
        expect(current.snapshotMemory().shelter).toBe("bed-1");
    });

    test("blocks an unreachable inspect target after tree failure", () => {
        const runtime = new FakeRuntime();
        const current = brain(runtime, { needs: { social: 0.1, curiosity: 0.9 } });
        const fish = sensed({ id: "fish-1", name: "Fish1", kind: "item", isObject: true, distance: 6, pos: { x: 6, y: 8, z: 0 } });
        const currentView = view({ objects: [fish] });
        current.tick(currentView, 1000);
        runtime.act("select-target", { kind: "object", maxDistance: 14 }, currentView);

        runtime.status = "failure";
        current.tick(currentView, 2000);
        expect(current.snapshotMemory().blocked).toMatchObject({ "fish-1": 302000 });

        runtime.status = "running";
        current.tick(currentView, 3000);
        expect(runtime.started.at(-1)).not.toBe("inspect");
    });

    test("remembers home and accepts a manual request", () => {
        const runtime = new FakeRuntime();
        const current = brain(runtime, { memory: { home: { x: 50, y: 12, z: -20 } }, needs: { energy: 0.9 } });
        expect(current.snapshotMemory().home).toEqual({ x: 50, y: 12, z: -20 });

        current.request("rest", null, 1000);
        current.tick(view(), 1000);
        expect(runtime.started).toEqual(["rest"]);
    });
});
