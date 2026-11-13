import { describe, expect, test } from "bun:test";
import type { NpcIntent, NpcPos, NpcSensed, NpcView } from "../sdk/api/types";
import { VillagerController, type VillagerActor } from "../testing/life-demo/lib/controller";

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

function controller(actor: FakeActor, options: { home?: NpcPos; memory?: Record<string, unknown>; needs?: { energy?: number; social?: number; curiosity?: number } } = {}) {
    return new VillagerController({
        actor,
        home: options.home ?? { x: 0, y: 10, z: 0 },
        memory: options.memory,
        needs: { curiosity: 0.4, ...options.needs },
        random: () => 0.5
    });
}

describe("villager controller", () => {
    test("wanders around home when nothing needs attention", () => {
        const actor = new FakeActor();
        const brain = controller(actor);
        expect(brain.tick(view(), 1000)).toBe("wander");
        expect(actor.calls[0]).toStartWith("moveTo ");
        expect(actor.calls[0]).toContain("run=false");
    });

    test("flees a visible enemy with run enabled", () => {
        const actor = new FakeActor();
        const brain = controller(actor);
        const enemy = sensed({ id: "enemy", enemy: true, distance: 4, pos: { x: 4, y: 10, z: 0 } });
        expect(brain.tick(view({ nearby: [enemy] }), 1000)).toBe("flee");
        expect(actor.calls[0]).toContain("run=true");
    });

    test("rests at home when night and tired, then recovers energy", () => {
        const actor = new FakeActor();
        const brain = controller(actor, { needs: { energy: 0.2 } });
        const night = view({ environment: { day: 1, isDay: false, biome: "Meadows", weather: null } });

        brain.tick(night, 1000);
        expect(brain.current).toBe("rest");
        expect(actor.calls[0]).toStartWith("wait ");

        night.intent = { ...idle, kind: "wait", status: "arrived" };
        brain.tick(night, 2000);
        expect(brain.needs.energy).toBeGreaterThan(0.2);
        expect(brain.current).toBe("rest");
    });

    test("socializes with a nearby player and records the meeting", () => {
        const actor = new FakeActor();
        const brain = controller(actor, { needs: { social: 0.9, curiosity: 0.1 } });
        const player = sensed({ id: "player-1", name: "Astrid", isPlayer: true, distance: 6, pos: { x: 6, y: 10, z: 0 } });
        const current = view({ nearby: [player] });

        brain.tick(current, 1000);
        expect(brain.current).toBe("socialize");
        expect(actor.calls[0]).toContain("moveTo 6,10,0");

        current.intent = { ...idle, kind: "moveTo", status: "arrived" };
        brain.tick(current, 2000);
        expect(actor.calls).toContain("face player-1");

        current.intent = { ...idle, kind: "face", status: "arrived" };
        brain.tick(current, 3000);
        expect(actor.calls.some(call => call.startsWith("wait "))).toBe(true);

        current.intent = { ...idle, kind: "wait", status: "arrived" };
        brain.tick(current, 4000);
        expect(brain.needs.social).toBeLessThan(0.9);
        expect(brain.snapshotMemory().lastMet).toMatchObject({ id: "player-1", name: "Astrid" });
    });

    test("inspects an unknown object once, then remembers it", () => {
        const actor = new FakeActor();
        const brain = controller(actor, { needs: { social: 0.1, curiosity: 0.9 } });
        const chest = sensed({ id: "chest-1", name: "Chest", kind: "container", isObject: true, distance: 4, pos: { x: 4, y: 10, z: 0 } });
        const current = view({ objects: [chest] });

        brain.tick(current, 1000);
        expect(brain.current).toBe("inspect");

        current.intent = { ...idle, kind: "moveTo", status: "arrived" };
        brain.tick(current, 2000);
        expect(actor.calls).toContain("lookAt 4,10,0");

        current.intent = { ...idle, kind: "lookAt", status: "arrived" };
        brain.tick(current, 3000);
        expect(actor.calls.some(call => call.startsWith("wait "))).toBe(true);

        current.intent = { ...idle, kind: "wait", status: "arrived" };
        brain.tick(current, 4000);
        expect(brain.snapshotMemory().visited).toContain("chest-1");
    });

    test("uses remembered home and accepts a manual request", () => {
        const actor = new FakeActor();
        const brain = controller(actor, { memory: { home: { x: 50, y: 12, z: -20 } }, needs: { energy: 0.9 } });
        expect(brain.snapshotMemory().home).toEqual({ x: 50, y: 12, z: -20 });

        brain.request("rest", null, 1000);
        brain.tick(view(), 1000);
        expect(brain.current).toBe("rest");
    });

    test("forages away from home when curious during the day", () => {
        const actor = new FakeActor();
        const brain = controller(actor, { needs: { curiosity: 0.95, energy: 0.9 } });
        expect(brain.tick(view(), 1000)).toBe("forage");
        expect(actor.calls[0]).toContain("run=true");
    });

    test("returns home when it has wandered far away", () => {
        const actor = new FakeActor();
        const brain = controller(actor, { needs: { curiosity: 0.4, energy: 0.9 } });
        expect(brain.tick(view({ x: 40, z: 0 }), 1000)).toBe("return");
        expect(actor.calls[0]).toContain("moveTo 0,10,0");
    });

    test("claims an empty bed as its home", () => {
        const actor = new FakeActor();
        const brain = controller(actor, { needs: { curiosity: 0.4, social: 0.1, energy: 0.9 } });
        const bed = sensed({
            id: "bed-1",
            name: "Bed",
            kind: "bed",
            isObject: true,
            distance: 9,
            pos: { x: 9, y: 11, z: 0 }
        });
        const current = view({ objects: [bed] });

        brain.tick(current, 1000);
        expect(brain.current).toBe("seekHome");
        expect(brain.isSheltered).toBe(false);

        current.intent = { ...idle, kind: "moveTo", status: "arrived" };
        brain.tick(current, 2000);
        expect(brain.isSheltered).toBe(true);
        expect(brain.homePosition).toEqual({ x: 9, y: 11, z: 0 });
        expect(brain.snapshotMemory().shelter).toBe("bed-1");
        expect(actor.calls).toContain("lookAt 9,11,0");
    });

    test("a completed forage trip makes the brain head home", () => {
        const actor = new FakeActor();
        const brain = controller(actor, { needs: { curiosity: 0.95, social: 0.9, energy: 0.9 } });
        const current = view();

        brain.tick(current, 1000);
        expect(brain.current).toBe("forage");

        current.intent = { ...idle, kind: "moveTo", status: "arrived" };
        brain.tick(current, 2000);
        current.intent = { ...idle, kind: "lookAt", status: "arrived" };
        brain.tick(current, 3000);
        current.intent = { ...idle, kind: "wait", status: "arrived" };
        brain.tick(current, 4000);

        const player = sensed({ id: "player-1", isPlayer: true, distance: 5, pos: { x: 25, y: 10, z: 0 } });
        const far = view({ x: 20, z: 0, nearby: [player] });
        expect(brain.tick(far, 5000)).toBe("return");
        expect(actor.calls.at(-1)).toContain("moveTo 0,10,0");
    });

    test("remembers an unreachable object instead of retrying it", () => {
        const actor = new FakeActor();
        const brain = controller(actor, { needs: { social: 0.1, curiosity: 0.9 } });
        const fish = sensed({
            id: "fish-1",
            name: "Fish1",
            kind: "item",
            isObject: true,
            distance: 6,
            pos: { x: 6, y: 8, z: 0 }
        });
        const current = view({ objects: [fish] });

        brain.tick(current, 1000);
        expect(brain.current).toBe("inspect");

        current.intent = { ...idle, kind: "moveTo", status: "failed", reason: "nav-unavailable" };
        brain.tick(current, 2000);
        expect(brain.snapshotMemory().blocked).toMatchObject({ "fish-1": 302000 });

        current.intent = idle;
        brain.tick(current, 3000);
        expect(brain.current).not.toBe("inspect");
    });

    test("abandons a failed intent and backs off", () => {
        const actor = new FakeActor();
        const brain = controller(actor);
        const current = view();
        brain.tick(current, 1000);

        current.intent = { ...idle, kind: "moveTo", status: "failed", reason: "nav-stuck" };
        brain.tick(current, 2000);
        expect(brain.current).toBe("wander");
        expect(actor.calls.filter(call => call.startsWith("moveTo ")).length).toBe(1);
    });
});
