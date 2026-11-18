import { describe, expect, test } from "bun:test";
import type { NpcView } from "../sdk/api/types";
import type { Goal, PlannerActor } from "../sdk/planning";
import { UtilityAI, defaultUtilityStats, type UtilityFeedbackContext, type UtilityStats } from "../sdk/utility";
import { feedback } from "../testing/life-demo/definitions/profiles";

const ok = { ok: true, reason: null };

function actor(): PlannerActor {
    return {
        moveTo: () => ok,
        idle: () => ok,
        stop: () => ok,
        wait: () => ok,
        lookAt: () => ok,
        face: () => ok,
        pick: () => ok
    };
}

function view(overrides: Partial<NpcView> = {}): NpcView {
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
        objects: [],
        ...overrides
    };
}

function goal(id: string, overrides: Partial<Goal> = {}): Goal {
    return {
        id,
        score: () => 0,
        tick: () => "running",
        ...overrides
    };
}

describe("utility AI", () => {
    test("keeps Goal.score when no description exists", () => {
        const ai = new UtilityAI({
            actor: actor(),
            stats: { Predictability: 1 },
            planner: { now: () => 0 },
            goals: [
                goal("fallback", {
                    score: () => 5,
                    tick: () => "success"
                })
            ]
        });

        expect(ai.tick(view())).toBe("fallback");
    });

    test("uses describe instead of score when both exist", () => {
        let scoreCalls = 0;
        const ai = new UtilityAI({
            actor: actor(),
            stats: { Curiosity: 1, Predictability: 1 },
            considerations: {
                curiosity: ({ factors, stats }) => (factors.curiosity ?? 0) * stats.Curiosity * 10
            },
            planner: { now: () => 0 },
            goals: [
                goal("described", {
                    score: () => {
                        scoreCalls++;
                        return 999;
                    },
                    describe: () => ({ base: 1, factors: { curiosity: 1 } }),
                    tick: () => "success"
                })
            ]
        });

        expect(ai.tick(view())).toBe("described");
        expect(scoreCalls).toBe(0);
    });

    test("passes the current view and stats into describe", () => {
        let seen = 0;
        const ai = new UtilityAI({
            actor: actor(),
            stats: { Explorer: 0.5, Predictability: 1 },
            planner: { now: () => 0 },
            goals: [
                goal("context", {
                    describe: ({ view, stats }) => {
                        seen = view.x + stats.Explorer;
                        return { base: 1 };
                    },
                    tick: () => "success"
                })
            ]
        });

        ai.available(view({ x: 5 }));
        expect(seen).toBe(5.5);
    });

    test("evaluates contextual factors and filters available goals", () => {
        const ai = new UtilityAI({
            actor: actor(),
            stats: { Curiosity: 1, Social: 0, Predictability: 1 },
            considerations: {
                curiosity: ({ factors, stats }) => (factors.curiosity ?? 0) * stats.Curiosity * 10,
                social: ({ factors, stats }) => (factors.social ?? 0) * stats.Social * 10
            },
            planner: { now: () => 0 },
            goals: [
                goal("inspect", {
                    describe: () => ({ factors: { curiosity: 1 } }),
                    tick: () => "success"
                }),
                goal("socialize", {
                    describe: () => ({ factors: { social: 1 } }),
                    tick: () => "success"
                }),
                goal("blocked", {
                    describe: () => ({ base: 100 }),
                    eligible: () => false
                })
            ]
        });

        expect(ai.available(view()).map(candidate => candidate.goal.id)).toEqual(["inspect"]);
        expect(ai.tick(view())).toBe("inspect");

        ai.setStats({ Curiosity: 0, Social: 1 });
        expect(ai.available(view()).map(candidate => candidate.goal.id)).toEqual(["socialize"]);
        expect(ai.tick(view())).toBe("socialize");
    });

    test("stored stats override initial profile defaults", () => {
        const ai = new UtilityAI({
            actor: actor(),
            stats: { Explorer: 0.2 },
            store: {
                load: () => ({ Explorer: 0.8 }),
                save: () => {}
            },
            goals: []
        });

        expect(ai.stats.Explorer).toBe(0.8);
    });

    test("predictability moves selection from deterministic to random", () => {
        const deterministic = new UtilityAI({
            actor: actor(),
            stats: { Predictability: 1 },
            random: () => 0.99,
            planner: { now: () => 0 },
            goals: [
                goal("high", { describe: () => ({ base: 10 }) }),
                goal("low", { describe: () => ({ base: 1 }) })
            ]
        });
        expect(deterministic.tick(view())).toBe("high");

        const random = new UtilityAI({
            actor: actor(),
            stats: { Predictability: 0 },
            random: () => 0.99,
            planner: { now: () => 0 },
            goals: [
                goal("high", { describe: () => ({ base: 10 }) }),
                goal("low", { describe: () => ({ base: 1 }) })
            ]
        });
        expect(random.tick(view())).toBe("low");
    });

    test("records event context at event time and applies shared feedback", () => {

        let ticks = 0;
        let food = 0;
        const saved: UtilityStats[] = [];
        const ai = new UtilityAI({
            actor: actor(),
            stats: { Predictability: 1 },
            maxStep: 0.02,
            snapshot: () => ({ food }),
            store: {
                load: () => ({}),
                save: stats => saved.push({ ...stats })
            },
            feedback: [
                {
                    id: "wilderness-danger",
                    adjust: context => {
                        const attackedAway = context.events.some(
                            event => event.name === "damaged" && (event.observed.awayFromHome ?? 0) >= 10
                        );
                        return attackedAway ? { Explorer: -0.02 } : {};
                    }
                },
                {
                    id: "hunting-food",
                    adjust: context =>
                        context.goal.id === "hunt" && (context.observed.food ?? 0) > 0
                            ? { Hunter: 0.02 }
                            : {}
                }
            ],
            planner: { now: () => 0 },
            goals: [
                goal("hunt", {
                    describe: () => ({ base: 10 }),
                    tick: () => (++ticks === 1 ? "running" : "success")
                })
            ]
        });

        const away = view({ x: 20, home: { x: 0, y: 0, z: 0 } });
        const home = view({ x: 0, home: { x: 0, y: 0, z: 0 } });

        expect(ai.tick(away)).toBe("hunt");
        expect(ai.record("damaged", { damage: 10 }, away)).toBe(true);
        food = 1;
        expect(ai.tick(home)).toBe("hunt");

        expect(ai.lastOutcome?.events).toHaveLength(1);
        expect(ai.lastOutcome?.events[0]?.observed.awayFromHome).toBeCloseTo(20);
        expect(ai.lastOutcome?.observed.food).toBe(1);
        expect(ai.stats.Explorer).toBeCloseTo(defaultUtilityStats.Explorer! - 0.02);
        expect(ai.stats.Hunter).toBeCloseTo(defaultUtilityStats.Hunter! + 0.02);
        expect(saved).toHaveLength(1);
    });

    test("runs an existing Plan through the activity goal", () => {
        const calls: string[] = [];
        let planStatus: string | null = null;
        const ai = new UtilityAI({
            actor: actor(),
            stats: { Predictability: 1 },
            feedback: [
                {
                    id: "plan-outcome",
                    adjust: context => {
                        planStatus = context.planStatus;
                        return {};
                    }
                }
            ],
            planner: { now: () => 0 },
            goals: [
                goal("work", {
                    describe: () => ({ base: 5 }),
                    enter: context => {
                        context.run([{ call: () => calls.push("call") }]);
                    },
                    tick: context => (context.planStatus === "success" ? "success" : "running")
                })
            ]
        });

        expect(ai.tick(view())).toBe("work");
        expect(calls).toEqual(["call"]);
        expect(planStatus).toBe("success");
    });

    test("captures the committed expectation at entry", () => {
        const ai = new UtilityAI({
            actor: actor(),
            stats: { Predictability: 1 },
            planner: { now: () => 0 },
            goals: [
                goal("work", {
                    describe: () => ({
                        base: 1,
                        factors: { effort: 0.7, distance: 0.4 },
                        satisfies: { energy: 0.3 }
                    }),
                    tick: () => "success"
                })
            ]
        });

        expect(ai.tick(view())).toBe("work");
        expect(ai.lastOutcome?.expected).toEqual({
            factors: { effort: 0.7, distance: 0.4 },
            satisfies: { energy: 0.3 }
        });
    });

    test("adaptation can change the next selection", () => {
        let exploreTicks = 0;
        const ai = new UtilityAI({
            actor: actor(),
            stats: { Explorer: 0.6, Social: 0.5, Predictability: 1 },
            maxStep: 0.2,
            considerations: {
                exploration: ({ factors, stats }) => (factors.exploration ?? 0) * stats.Explorer * 10,
                social: ({ factors, stats }) => (factors.social ?? 0) * stats.Social * 10
            },
            feedback: [
                {
                    id: "wilderness-danger",
                    adjust: context =>
                        context.goal.id === "explore" && context.events.some(event => event.name === "damaged")
                            ? { Explorer: -0.2 }
                            : {}
                }
            ],
            planner: { now: () => 0 },
            goals: [
                goal("explore", {
                    describe: () => ({ factors: { exploration: 1 } }),
                    tick: () => (++exploreTicks === 1 ? "running" : "success")
                }),
                goal("socialize", {
                    describe: () => ({ factors: { social: 1 } }),
                    tick: () => "success"
                })
            ]
        });

        expect(ai.tick(view())).toBe("explore");
        expect(ai.record("damaged", { damage: 10 }, view({ x: 20 }))).toBe(true);
        expect(ai.tick(view())).toBe("explore");
        expect(ai.stats.Explorer).toBeCloseTo(0.4);
        expect(ai.tick(view())).toBe("socialize");
    });

    test("resolves a missing requirement with another known goal and resumes", () => {
        let wood = false;
        const selected: string[] = [];
        const ai = new UtilityAI({
            actor: actor(),
            stats: { Predictability: 1 },
            planner: { now: () => 0 },
            goals: [
                goal("cook", {
                    describe: () => ({ base: 10, requires: wood ? [] : ["wood"] }),
                    enter: () => selected.push("cook"),
                    tick: () => "success"
                }),
                goal("gather-wood", {
                    describe: () => ({ base: 1, provides: ["wood"] }),
                    enter: () => selected.push("gather-wood"),
                    tick: () => {
                        wood = true;
                        return "success";
                    }
                })
            ]
        });

        expect(ai.available(view()).map(candidate => candidate.goal.id)).toEqual(["gather-wood"]);
        expect(ai.tick(view())).toBe("gather-wood");
        expect(ai.tick(view())).toBe("cook");
        expect(selected).toEqual(["gather-wood", "cook"]);
    });

    test("cools an objective down when its prerequisite provider fails", () => {
        let now = 0;
        const ai = new UtilityAI({
            actor: actor(),
            stats: { Predictability: 1 },
            prerequisiteCooldownMs: 30_000,
            planner: { now: () => now },
            goals: [
                goal("cook", {
                    describe: () => ({ base: 10, requires: ["wood"] }),
                    tick: () => "success"
                }),
                goal("gather-wood", {
                    describe: () => ({ base: 1, provides: ["wood"] }),
                    cooldownMs: 60_000,
                    tick: () => "failure"
                })
            ]
        });

        expect(ai.tick(view())).toBe("gather-wood");
        expect(ai.lastOutcome?.status).toBe("failure");
        now = 1;
        expect(ai.tick(view())).toBeNull();
    });

    test("bounds repeated prerequisite attempts", () => {
        const selected: string[] = [];
        const ai = new UtilityAI({
            actor: actor(),
            stats: { Predictability: 1 },
            maxPrerequisiteAttempts: 1,
            planner: { now: () => 0 },
            goals: [
                goal("cook", {
                    describe: () => ({ base: 10, requires: ["wood"] }),
                    enter: () => selected.push("cook"),
                    tick: () => "success"
                }),
                goal("stubborn-wood", {
                    describe: () => ({ base: 1, provides: ["wood"] }),
                    enter: () => selected.push("stubborn-wood"),
                    tick: () => "success"
                })
            ]
        });

        expect(ai.tick(view())).toBe("stubborn-wood");
        expect(ai.tick(view())).toBe("stubborn-wood");
        expect(selected).toEqual(["stubborn-wood", "stubborn-wood"]);
    });
    test("hunt feedback weighs reward against injury, not success alone", () => {
        const rule = feedback.find(candidate => candidate.id === "hunt-outcome");
        if (rule === undefined) throw new Error("hunt-outcome rule missing");
        const before = view({ health: 100, maxHealth: 100 });
        const base = {
            goal: { id: "hunt", score: () => 0, tick: () => "success" as const },
            description: null,
            expected: { factors: {}, satisfies: { food: 1 } },
            status: "success" as const,
            durationMs: 30_000,
            before,
            after: before,
            stats: {},
            events: [],
            planStatus: "success" as const
        };
        const hurt = rule.adjust({
            ...base,
            observed: { healthDelta: -39, loot: 2 }
        } as UtilityFeedbackContext);
        const lucky = rule.adjust({
            ...base,
            observed: { healthDelta: -0.6, loot: 1 }
        } as UtilityFeedbackContext);

        expect(hurt.Hunter).toBeLessThan(0);
        expect(lucky.Hunter).toBeGreaterThan(0);
    });

});
