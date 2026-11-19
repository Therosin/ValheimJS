import { context } from "../runtime";
import type { NpcSensed, NpcView } from "../api/types";
import {
    type Goal,
    type GoalCandidate,
    type GoalContext,
    type GoalDescription,
    type GoalExitReason,
    type PlanStatus,
    PlanHandle,
    stepDispatch,
    type Step,
    type PlanOptions
} from "../planning";
import { defaultUtilityStats } from "./stats";
import type {
    UtilityAIOptions,
    UtilityCandidate,
    UtilityFeedbackContext,
    UtilityStats
} from "./types";

/**
 * Utility policy facade. The host owns selection, prerequisite state, event capture, snapshots
 * and feedback math; this class supplies goal callbacks and the plugin's stats store.
 */
export class UtilityAI {
    readonly id: string;

    private readonly options: UtilityAIOptions;
    private readonly clock: () => number;
    private readonly considerations: NonNullable<UtilityAIOptions["considerations"]>;
    private readonly feedback: NonNullable<UtilityAIOptions["feedback"]>;
    private readonly store?: UtilityAIOptions["store"];
    private readonly debug?: (message: string) => void;
    private readonly log?: (message: string) => void;
    private goalsValue: Goal[] = [];
    private readonly goalsById = new Map<string, Goal>();
    private activePlan: PlanHandle | null = null;

    constructor(options: UtilityAIOptions) {
        this.options = options;
        this.clock = options.planner?.now ?? (() => Date.now());
        this.considerations = options.considerations ?? {};
        this.feedback = options.feedback ?? [];
        this.store = options.store;
        this.debug = options.debug;
        this.log = options.planner?.log;

        const loaded = options.store?.load() ?? {};
        const stats = { ...defaultUtilityStats, ...options.stats, ...loaded };
        this.id = context().host.api.planning.utilityCreate(
            options.actor,
            {
                maxStep: Math.max(0, options.maxStep ?? 0.02),
                maxPrerequisiteAttempts: Math.max(1, options.maxPrerequisiteAttempts ?? 2),
                prerequisiteCooldownMs: Math.max(0, options.prerequisiteCooldownMs ?? 30_000),
                considerations: Object.keys(this.considerations),
                feedback: this.feedback.map(rule => rule.id),
                stats,
                planner: options.planner ?? {}
            },
            ((kind: string, ...args: unknown[]) => this.dispatch(kind, args)) as unknown as (
                ...args: unknown[]
            ) => unknown
        );
        this.setGoals(options.goals ?? []);
    }

    get stats(): Readonly<UtilityStats> {
        return context().host.api.planning.utilityStats(this.id) as UtilityStats;
    }

    get goals(): readonly Goal[] {
        return this.goalsValue.slice();
    }

    get lastOutcome(): UtilityFeedbackContext | null {
        return context().host.api.planning.utilityLastOutcome(this.id) as UtilityFeedbackContext | null;
    }

    get current(): string | null {
        return context().host.api.planning.utilityCurrent(this.id);
    }

    get currentElapsedMs(): number {
        return context().host.api.planning.utilityCurrentElapsedMs(this.id);
    }

    setGoals(goals: readonly Goal[]): void {
        const metadata = goals.map(goal => ({
            id: goal.id,
            hasTick: typeof goal.tick === "function",
            cooldownMs: goal.cooldownMs,
            hysteresis: goal.hysteresis,
            commitMs: goal.commitMs,
            urgent: goal.urgent,
            behaviour: (goal as Goal & { behaviour?: unknown }).behaviour
        }));

        // The host finishes any active goal through the dispatcher, so keep the old goal map
        // installed until utilitySetGoals returns.
        context().host.api.planning.utilitySetGoals(this.id, metadata);
        this.goalsValue = goals.slice();
        this.goalsById.clear();
        for (const goal of goals) {
            this.goalsById.set(goal.id, goal);
        }
        this.activePlan = null;
    }

    available(view: NpcView, now = this.clock()): UtilityCandidate[] {
        const raw = context().host.api.planning.utilityAvailable(this.id, view, now) as {
            goal: string;
            score: number;
        }[];
        return raw
            .map(candidate => {
                const goal = this.goalsById.get(candidate.goal);
                return goal === undefined ? null : { goal, score: candidate.score };
            })
            .filter((candidate): candidate is UtilityCandidate => candidate !== null);
    }

    tick(view: NpcView, now = this.clock()): string | null {
        return context().host.api.planning.utilityTick(this.id, view, now);
    }

    record(name: string, data?: unknown, view?: NpcView): boolean {
        return context().host.api.planning.utilityRecord(this.id, name, data ?? null, view ?? null);
    }

    request(id: string, _view: NpcView, untilMs = 30_000): void {
        if (!this.goalsById.has(id)) return;
        context().host.api.planning.utilityRequest(this.id, id, this.clock() + untilMs);
    }

    reset(): void {
        context().host.api.planning.utilityReset(this.id);
        this.activePlan = null;
    }

    dispose(): void {
        context().host.api.planning.utilityDispose(this.id);
        this.goalsValue = [];
        this.goalsById.clear();
        this.activePlan = null;
    }

    setStats(stats: Partial<UtilityStats>): void {
        context().host.api.planning.utilitySetStats(this.id, stats);
    }

    private dispatch(kind: string, args: unknown[]): unknown {
        switch (kind) {
            case "score": {
                return this.goal(args[0])?.score(args[1] as NpcView);
            }
            case "eligible": {
                const goal = this.goal(args[0]);
                return goal?.eligible?.(args[1] as NpcView) ?? true;
            }
            case "describe": {
                const goal = this.goal(args[0]);
                if (goal?.describe === undefined) return null;
                return goal.describe({
                    view: args[1] as NpcView,
                    now: args[2] as number,
                    stats: args[3] as UtilityStats
                }) ?? {};
            }
            case "consideration": {
                const name = args[0] as string;
                const consideration = this.considerations[name];
                if (consideration === undefined) return 0;
                const description = (args[2] as GoalDescription | null) ?? {};
                const view = args[3] as NpcView;
                const now = args[4] as number;
                const stats = args[5] as UtilityStats;
                const goal = this.goal(args[1]);
                if (goal === undefined) return 0;
                return consideration({
                    goal,
                    description,
                    factors: description.factors ?? {},
                    view,
                    now,
                    stats
                });
            }
            case "enter": {
                const goal = this.goal(args[0]);
                if (goal === undefined) return undefined;
                this.activePlan = null;
                goal.enter?.(this.goalContext(goal, args));
                return undefined;
            }
            case "tick": {
                const goal = this.goal(args[0]);
                return goal?.tick?.(this.goalContext(goal, args)) ?? "running";
            }
            case "exit": {
                const goal = this.goal(args[0]);
                if (goal === undefined) return undefined;
                try {
                    goal.exit?.(
                        this.goalContext(goal, args, args[3] as GoalExitReason),
                        args[3] as GoalExitReason
                    );
                } finally {
                    this.activePlan = null;
                }
                return undefined;
            }
            case "feedback": {
                const rule = this.feedback.find(candidate => candidate.id === args[0]);
                if (rule === undefined) return {};
                return rule.adjust(args[1] as UtilityFeedbackContext);
            }
            case "snapshot": {
                if (this.options.snapshot === undefined) return {};
                return this.options.snapshot(this.goalContext(this.goal(args[0])!, args));
            }
            case "random":
                return this.options.random?.() ?? Math.random();
            case "save":
                this.store?.save(args[0] as UtilityStats);
                return undefined;
            case "debug":
                this.debug?.(String(args[0] ?? ""));
                return undefined;
            case "log":
                this.log?.(String(args[0] ?? ""));
                return undefined;
            default:
                return undefined;
        }
    }

    private goal(id: unknown): Goal | undefined {
        return typeof id === "string" ? this.goalsById.get(id) : undefined;
    }

    private goalContext(goal: Goal, args: unknown[], exitReason?: GoalExitReason): GoalContext {
        const view = args[1] as NpcView;
        const now = args[2] as number;
        const planStatus = (args[4] as PlanStatus | null | undefined) ?? null;
        const planReason = (args[5] as string | null | undefined) ?? null;
        return {
            view,
            actor: this.options.actor,
            now,
            goal: goal.id,
            planStatus,
            planReason,
            log: message => this.log?.(`[${goal.id}] ${message}`),
            moveTo: (x, y, z, run = false) => this.options.actor.moveTo(x, y, z, run),
            wait: seconds => this.options.actor.wait(seconds),
            face: id => this.options.actor.face(id),
            pick: id => this.options.actor.pick(id),
            interact: (id, options = {}) => {
                if (this.options.actor.interact === undefined) {
                    return { ok: false, reason: "unsupported", verb: options.verb ?? "", count: 0 };
                }

                return this.options.actor.interact(id, options);
            },
            affordances: id => {
                if (this.options.actor.affordances === undefined) {
                    return {
                        id,
                        kind: "",
                        name: "",
                        hoverName: null,
                        hoverText: null,
                        supported: false,
                        playerOnly: false,
                        reason: "unsupported",
                        verbs: [],
                        gives: [],
                        requires: [],
                        toolTier: 0,
                        distance: 0,
                        inRange: false
                    };
                }

                return this.options.actor.affordances(id);
            },
            run: (steps, options = {}) => {
                const planId = context().host.api.planning.planCreate(
                    this.options.actor,
                    steps,
                    options,
                    stepDispatch(this.options.actor, options) as unknown as (
                        ...values: unknown[]
                    ) => unknown
                );
                context().host.api.planning.utilitySetPlan(this.id, planId);
                const handle = new PlanHandle(planId);
                this.activePlan = handle;
                return handle;
            },
            cancelPlan: reason => {
                if (this.activePlan !== null) {
                    this.activePlan.cancel(reason);
                    this.activePlan = null;
                }
                context().host.api.planning.utilitySetPlan(this.id, null);
            },
            sense: id => nearestSense(view, id)
        };
    }
}

function nearestSense(view: NpcView, id: string): NpcSensed | null {
    for (const entry of view.nearby) {
        if (entry.id === id) return entry;
    }
    for (const entry of view.objects) {
        if (entry.id === id) return entry;
    }
    for (const entry of view.remembered) {
        if (entry.id === id) return entry;
    }
    return null;
}
