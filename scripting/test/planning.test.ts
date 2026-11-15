import { describe, expect, test } from "bun:test";
import { Planner, type Goal, type PlannerActor } from "../sdk/planning";
import type { NpcView } from "../sdk/api/types";

function view(): NpcView {
    return {
        id: "1:1",
        kind: "villager",
        x: 0,
        y: 0,
        z: 0,
        health: 100,
        maxHealth: 100,
        isAlive: true,
        faction: "",
        isTamed: false,
        level: 1,
        home: { x: 0, y: 0, z: 0 },
        ready: true,
        combat: { engaged: false, threatId: null },
        self: null,
        action: null,
        environment: null,
        nearby: [],
        remembered: [],
        objects: []
    };
}

function actor(): PlannerActor & { calls: string[] } {
    const calls: string[] = [];
    return {
        calls,
        moveTo: () => {
            calls.push("moveTo");
            return { ok: true };
        },
        idle: () => (calls.push("idle"), { ok: true }),
        stop: () => (calls.push("stop"), { ok: true }),
        wait: () => (calls.push("wait"), { ok: true }),
        lookAt: () => (calls.push("lookAt"), { ok: true }),
        face: () => (calls.push("face"), { ok: true }),
        pick: () => (calls.push("pick"), { ok: true })
    };
}

function goal(id: string, score: number, overrides: Partial<Goal> = {}): Goal {
    return {
        id,
        score: () => score,
        tick: () => "running",
        ...overrides
    };
}

describe("planner", () => {
    test("selects the highest eligible score and ignores ineligible goals", () => {
        const planner = new Planner(actor(), { now: () => 0 });
        planner.setGoals([
            goal("low", 1),
            goal("blocked", 100, { eligible: () => false }),
            goal("high", 5)
        ]);

        expect(planner.tick(view())).toBe("high");
    });

    test("runs callbacks in order and applies plugin cooldowns", () => {
        let now = 0;
        const order: string[] = [];
        const planner = new Planner(actor(), { now: () => now });
        planner.setGoals([
            goal("work", 5, {
                enter: () => order.push("enter"),
                tick: () => {
                    order.push("tick");
                    return "success";
                },
                exit: (_ctx, reason) => order.push(`exit:${reason}`),
                cooldownMs: 1000
            })
        ]);

        expect(planner.tick(view())).toBe("work");
        expect(order).toEqual(["enter", "tick", "exit:success"]);

        now = 1;
        expect(planner.tick(view())).toBeNull();

        now = 500;
        expect(planner.tick(view())).toBeNull();

        now = 1001;
        expect(planner.tick(view())).toBe("work");
    });

    test("preempts only when the candidate clears the plugin hysteresis", () => {
        let candidate = 5;
        const exits: string[] = [];
        const planner = new Planner(actor(), { now: () => 0, replanMs: 0 });
        planner.setGoals([
            goal("current", 5, {
                hysteresis: 2,
                exit: (_ctx, reason) => exits.push(reason)
            }),
            goal("candidate", 0, {
                score: () => candidate
            })
        ]);

        expect(planner.tick(view())).toBe("current");
        expect(planner.tick(view())).toBe("current");

        candidate = 9;
        expect(planner.tick(view())).toBe("candidate");
        expect(exits).toEqual(["preempted"]);
    });

    test("request forces a goal regardless of score", () => {
        const planner = new Planner(actor(), { now: () => 0 });
        planner.setGoals([goal("high", 10), goal("manual", 1)]);
        planner.request("manual", view());

        expect(planner.tick(view())).toBe("manual");
    });

    test("context helpers call the actor without adding policy", () => {
        const fake = actor();
        const planner = new Planner(fake, { now: () => 0 });
        planner.setGoals([
            goal("work", 1, {
                enter: context => {
                    context.moveTo(1, 2, 3, true);
                    context.face("2:2");
                }
            })
        ]);

        planner.tick(view());
        expect(fake.calls).toEqual(["moveTo", "face"]);
    });
});
