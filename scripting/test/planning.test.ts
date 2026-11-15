import { describe, expect, test } from "bun:test";
import { Plan, Planner, registerAction, type Goal, type PlannerActor } from "../sdk/planning";
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
        swimming: false,
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

function sensed(id: string, distance: number): NpcSensed {
    return {
        id,
        name: id,
        kind: "container",
        prefab: null,
        faction: "",
        pos: { x: 10, y: 0, z: 0 },
        rel: { x: 10, y: 0, z: 0 },
        distance,
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
        isObject: true,
        health: 1,
        maxHealth: 1,
        firstSeen: 0,
        lastSeen: 0,
        seenCount: 1
    };
}

function action(kind: string, status: NpcAction["status"], reason: string | null = null): NpcAction {
    return {
        seq: 1,
        kind,
        status,
        reason,
        targetId: null,
        target: null,
        progress: null
    };
}

describe("plan", () => {
    test("awaits issued actions by kind and status", () => {
        const fake = actor();
        const calls: string[] = [];
        fake.moveTo = () => {
            calls.push("moveTo");
            return { ok: true };
        };
        const view = viewWith();
        const plan = new Plan(fake, [
            { moveTo: { x: 1, y: 0, z: 2 } },
            { call: () => calls.push("call") }
        ], { now: () => 0 });

        expect(plan.tick(view, 0)).toBe("running");
        expect(calls).toEqual(["moveTo"]);

        view.action = action("moveTo", "running");
        expect(plan.tick(view, 1)).toBe("running");

        view.action = action("moveTo", "arrived");
        expect(plan.tick(view, 2)).toBe("running");
        expect(plan.tick(view, 3)).toBe("success");
        expect(calls).toEqual(["moveTo", "call"]);
    });

    test("holds on paused actions and can time out", () => {
        const fake = actor();
        fake.moveTo = () => ({ ok: true });
        const view = viewWith();
        const plan = new Plan(fake, [{ moveTo: { x: 1, y: 0, z: 0 } }], { pauseGraceMs: 100 });

        view.action = action("moveTo", "paused", "combat");
        plan.tick(view, 0);
        view.action = action("moveTo", "paused", "combat");
        expect(plan.tick(view, 150)).toBe("running");
        expect(plan.tick(view, 300)).toBe("failure");
        expect(plan.reason).toBe("combat");
    });

    test("approaches an interaction target before interacting", () => {
        const fake = actor();
        let attempts = 0;
        let moved = 0;
        fake.moveTo = () => {
            moved++;
            return { ok: true };
        };
        fake.interact = () => {
            attempts++;
            return { ok: attempts > 1, reason: attempts > 1 ? null : "out-of-range", verb: "store", count: 1 };
        };
        const view = viewWith();
        view.objects = [sensed("2:2", 10)];
        const plan = new Plan(fake, [
            { interact: { target: "2:2", verb: "store", approach: { distance: 3 } } }
        ]);

        plan.tick(view, 0);
        expect(moved).toBe(1);

        view.action = action("moveTo", "arrived");
        plan.tick(view, 1);
        view.objects[0]!.distance = 2;
        plan.tick(view, 2);
        expect(plan.tick(view, 3)).toBe("success");
        expect(attempts).toBe(2);
    });

    test("follows a moving target until inside the distance", () => {
        const fake = actor();
        fake.moveTo = () => ({ ok: true });
        const view = viewWith();
        view.nearby = [sensed("1:2", 10)];
        view.nearby[0]!.isObject = false;
        const plan = new Plan(fake, [{ follow: { target: "1:2", distance: 4 } }]);

        plan.tick(view, 0);
        view.action = action("moveTo", "arrived");
        plan.tick(view, 1);
        view.nearby[0]!.distance = 3;
        expect(plan.tick(view, 2)).toBe("success");
    });

    test("retries a failed step the plugin asked to retry", () => {
        const fake = actor();
        let attempts = 0;
        fake.interact = () => {
            attempts++;
            return { ok: false, reason: "no-room", verb: "store", count: 0 };
        };
        const view = viewWith();
        const plan = new Plan(fake, [
            { interact: { target: "2:2", verb: "store", approach: false }, retry: { attempts: 1 } }
        ]);

        expect(plan.tick(view, 0)).toBe("running");
        expect(attempts).toBe(1);
        expect(plan.tick(view, 1)).toBe("failure");
        expect(attempts).toBe(2);
    });

    test("until, timeout and cancellation", () => {
        let ready = false;
        const plan = new Plan(actor(), [{ until: () => ready }], { timeoutMs: 100 });
        const view = viewWith();

        expect(plan.tick(view, 0)).toBe("running");
        ready = true;
        expect(plan.tick(view, 10)).toBe("success");

        const slow = new Plan(actor(), [{ until: () => false }], { timeoutMs: 100 });
        expect(slow.tick(view, 0)).toBe("running");
        expect(slow.tick(view, 150)).toBe("failure");
        expect(slow.reason).toBe("plan-timeout");

        slow.cancel("plugin-cancel");
        expect(slow.planStatus).toBe("failure");
    });

    test("planner ticks a goal-owned plan and exposes its status", () => {
        const fake = actor();
        fake.moveTo = () => ({ ok: true });
        const view = viewWith();
        const planner = new Planner(fake, { now: () => 0 });
        let done = false;
        planner.setGoals([
            {
                id: "go",
                score: () => (done ? 0 : 1),
                enter: ctx => {
                    ctx.run([{ moveTo: { x: 1, y: 0, z: 0 } }]);
                },
                tick: ctx => ctx.planStatus === "success" ? "success" : "running",
                exit: () => {
                    done = true;
                }
            }
        ]);

        expect(planner.tick(view, 0)).toBe("go");
        view.action = action("moveTo", "arrived");
        expect(planner.tick(view, 1)).toBe("go");
        expect(planner.tick(view, 2)).toBeNull();
    });

    test("plugin actions register and run by name", () => {
        let received = 0;
        registerAction<{ value: number }>("remember", {
            tick(_context, config) {
                received = config.value;
                return "success";
            }
        });

        const view = viewWith();
        const plan = new Plan(actor(), [{ action: "remember", config: { value: 7 } }]);
        expect(plan.tick(view, 0)).toBe("success");
        expect(received).toBe(7);
    });

    test("inventory steps use the supplied inventory", () => {
        const calls: string[] = [];
        const inventory = {
            use: () => (calls.push("use"), { ok: true }),
            equip: () => (calls.push("equip"), { ok: true }),
            unequip: () => (calls.push("unequip"), { ok: true }),
            drop: (_selector: unknown, count?: number) => {
                calls.push(`drop:${count ?? 1}`);
                return { ok: true };
            }
        };
        const view = viewWith();
        const plan = new Plan(actor(), [
            { equip: "Club" },
            { consume: "Raspberry" },
            { drop: "Wood", count: 2 }
        ], { inventory });

        expect(plan.tick(view, 0)).toBe("running");
        expect(plan.tick(view, 1)).toBe("running");
        expect(plan.tick(view, 2)).toBe("success");
        expect(calls).toEqual(["equip", "use", "drop:2"]);

        const missing = new Plan(actor(), [{ equip: "Club" }]);
        expect(missing.tick(view, 0)).toBe("failure");

        const crafted = new Plan(actor(), [{ craft: "Club" }], {
            craft: () => ({ ok: true, reasons: [], count: 1 })
        });
        expect(crafted.tick(view, 0)).toBe("success");
    });
});

function viewWith(): NpcView {
    return { ...view(), action: null };
}
