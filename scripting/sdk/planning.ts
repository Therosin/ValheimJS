import {
    actions,
    ActionRegistry,
    type ActionContext,
    type ActionHandler,
    type ActionOutcome,
    type ActionSettlement,
    type ActionResultLike,
    type InventoryActions,
    type PlannerActor,
    type Vec3Like
} from "./actions";
import type { CraftResult, NpcAffordances, NpcInteractOptions, NpcInteractionResult, NpcSensed, NpcView } from "./api/types";

export { ActionRegistry, actions, registerAction } from "./actions";
export type {
    ActionContext,
    ActionHandler,
    ActionOutcome,
    ActionSettlement,
    ActionResultLike,
    InventoryActions,
    PlannerActor,
    Vec3Like
} from "./actions";

/**
 * Policy-free planning primitives.
 *
 * `Planner` owns goal selection mechanics. `Plan` owns a linear, cancellable step sequence and
 * looks each step up in the action registry. Neither knows what a goal means, when it should
 * matter, or which reaction is correct. Built-in actions live in `sdk/actions/*`; plugins can
 * register more with `registerAction`.
 */

export type GoalStatus = "running" | "success" | "failure";
export type GoalExitReason = "success" | "failure" | "preempted";
export type PlanStatus = "idle" | "running" | "success" | "failure" | "cancelled";

export interface GoalContext {
    readonly view: NpcView;
    readonly actor: PlannerActor;
    readonly now: number;
    readonly goal: string;
    readonly planStatus: PlanStatus | null;
    log(message: string): void;
    moveTo(x: number, y: number, z: number, run?: boolean): ActionResultLike;
    wait(seconds: number): ActionResultLike;
    face(id: string): ActionResultLike;
    pick(id: string): ActionResultLike;
    interact(id: string, options?: NpcInteractOptions): NpcInteractionResult;
    affordances(id: string): NpcAffordances;
    /** Starts a step plan, replacing any previous plan owned by this goal. */
    run(steps: Step[], options?: PlanOptions): Plan;
    cancelPlan(reason?: string): void;
    /** Most recent perception entry with this id, when still in the store. */
    sense(id: string): NpcSensed | null;
}

export interface Goal {
    /** Stable plugin-defined id. */
    readonly id: string;
    /** Higher wins; <= 0 means ineligible. Called on the replan cadence. */
    score(view: NpcView): number;
    /** Optional hard gate evaluated before `score`. */
    eligible?(view: NpcView): boolean;
    enter?(ctx: GoalContext): void;
    /** Advance the goal. The planner never inspects why; the plugin decides. */
    tick(ctx: GoalContext): GoalStatus;
    exit?(ctx: GoalContext, reason: GoalExitReason): void;
    /** Optional pause after a finished or preempted goal, chosen by the plugin. */
    cooldownMs?: number;
    /** Required score margin to preempt this goal. Default 0. */
    hysteresis?: number;
}

export interface PlannerOptions {
    /** Minimum score to select any goal. Default 0. */
    threshold?: number;
    /** How often goals are re-scored. Default 500 ms. */
    replanMs?: number;
    /** Clock used for cooldowns/requests; defaults to `Date.now`. */
    now?: () => number;
    log?: (message: string) => void;
}

export class Planner {
    private readonly actor: PlannerActor;
    private readonly threshold: number;
    private readonly replanMs: number;
    private readonly clock: () => number;
    private readonly log?: (message: string) => void;
    private readonly cooldownUntil = new Map<string, number>();
    private goals: Goal[] = [];
    private active: Goal | null = null;
    private plan: Plan | null = null;
    private activeSince = 0;
    private lastReplan = 0;
    private lastView: NpcView | null = null;
    private requested: { id: string; until: number } | null = null;

    constructor(actor: PlannerActor, options: PlannerOptions = {}) {
        this.actor = actor;
        this.threshold = options.threshold ?? 0;
        this.replanMs = options.replanMs ?? 500;
        this.clock = options.now ?? (() => Date.now());
        this.log = options.log;
    }

    get current(): string | null {
        return this.active?.id ?? null;
    }

    get currentElapsedMs(): number {
        return this.active === null ? 0 : this.lastReplan - this.activeSince;
    }

    setGoals(goals: Goal[]): void {
        if (this.active !== null) {
            this.finish("preempted", this.clock());
        }

        this.goals = goals.slice();
        this.cooldownUntil.clear();
        this.requested = null;
        this.plan = null;
    }

    /** Forces one goal on the next eligible tick, bypassing its score. */
    request(id: string, view: NpcView, untilMs = 30_000): void {
        if (!this.goals.some(goal => goal.id === id)) return;
        this.requested = { id, until: this.clock() + untilMs };
    }

    /** Drops active state and cooldowns; goals stay installed. */
    reset(): void {
        if (this.active !== null) {
            this.finish("preempted", this.clock());
        }

        this.cooldownUntil.clear();
        this.requested = null;
        this.plan = null;
    }

    tick(view: NpcView, now = this.clock()): string | null {
        this.lastView = view;
        if (this.active !== null && now - this.lastReplan >= this.replanMs) {
            this.maybePreempt(view, now);
        }

        if (this.active === null) {
            this.select(view, now);
        }

        this.lastReplan = now;
        if (this.active === null) {
            return null;
        }

        this.plan?.tick(view, now);
        const goal = this.active;
        const context = this.context(view, goal, now);
        const status = goal.tick(context);
        if (status !== "running") {
            this.finish(status, now);
        }

        return goal.id;
    }

    dispose(): void {
        this.reset();
        this.goals = [];
    }

    private maybePreempt(view: NpcView, now: number): void {
        const current = this.active!;
        const candidate = this.best(view, now, current.id);
        if (candidate === null || candidate.goal.id === current.id) {
            return;
        }

        const margin = current.hysteresis ?? 0;
        if (candidate.score > current.score(view) + margin) {
            this.finish("preempted", now);
        }
    }

    private select(view: NpcView, now: number): void {
        const requested = this.requested;
        if (requested !== null) {
            if (now > requested.until) {
                this.requested = null;
            } else {
                const goal = this.goals.find(candidate => candidate.id === requested.id);
                if (goal !== undefined && (goal.eligible?.(view) ?? true)) {
                    this.start(goal, view, now);
                    return;
                }
            }
        }

        const candidate = this.best(view, now, null);
        if (candidate === null) {
            return;
        }

        this.start(candidate.goal, view, now);
    }

    private best(view: NpcView, now: number, exclude: string | null): { goal: Goal; score: number } | null {
        let best: { goal: Goal; score: number } | null = null;
        for (const goal of this.goals) {
            if (goal.id === exclude) continue;
            if ((this.cooldownUntil.get(goal.id) ?? 0) > now) continue;
            if (!(goal.eligible?.(view) ?? true)) continue;

            const score = goal.score(view);
            if (!Number.isFinite(score) || score <= this.threshold) continue;
            if (best === null || score > best.score) {
                best = { goal, score };
            }
        }

        return best;
    }

    private start(goal: Goal, view: NpcView, now: number): void {
        this.plan = null;
        this.active = goal;
        this.activeSince = now;
        this.lastReplan = now;
        this.requested = null;
        const context = this.context(view, goal, now);
        try {
            goal.enter?.(context);
        } catch (error) {
            this.log?.(`goal '${goal.id}' enter failed: ${String(error)}`);
            this.finish("failure", now);
        }
    }

    private finish(reason: GoalExitReason, now: number): void {
        const goal = this.active;
        if (goal === null) {
            return;
        }

        const view = this.lastView;
        this.active = null;
        if (goal.cooldownMs !== undefined && goal.cooldownMs > 0) {
            this.cooldownUntil.set(goal.id, now + goal.cooldownMs);
        }

        if (goal.exit !== undefined && view !== null) {
            try {
                goal.exit(this.context(view, goal, now), reason);
            } catch (error) {
                this.log?.(`goal '${goal.id}' exit failed: ${String(error)}`);
            }
        }

        this.plan?.cancel("goal-finished");
        this.plan = null;
    }

    private context(view: NpcView, goal: Goal, now: number): GoalContext {
        const actor = this.actor;
        const planner = this;
        return {
            view,
            actor,
            now,
            goal: goal.id,
            get planStatus() {
                return planner.plan?.planStatus ?? null;
            },
            log: message => this.log?.(`[${goal.id}] ${message}`),
            moveTo: (x, y, z, run = false) => actor.moveTo(x, y, z, run),
            wait: seconds => actor.wait(seconds),
            face: id => actor.face(id),
            pick: id => actor.pick(id),
            interact: (id, options = {}) => {
                if (actor.interact === undefined) {
                    return { ok: false, reason: "unsupported", verb: options.verb ?? "", count: 0 };
                }

                return actor.interact(id, options);
            },
            affordances: id => {
                if (actor.affordances === undefined) {
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
                        distance: 0,
                        inRange: false
                    };
                }

                return actor.affordances(id);
            },
            run: (steps, options = {}) => {
                planner.plan?.cancel("replaced");
                planner.plan = new Plan(actor, steps, options, planner.clock);
                return planner.plan;
            },
            cancelPlan: reason => {
                planner.plan?.cancel(reason);
                planner.plan = null;
            },
            sense: id => nearestSense(view, id)
        };
    }
}

export interface RetryOptions {
    attempts: number;
    delayMs?: number;
}

export interface PlanOptions {
    /** Whole-plan timeout. */
    timeoutMs?: number;
    /** Hold through `paused` actions up to this long; omitted = hold until plan timeout. */
    pauseGraceMs?: number;
    /** Inventory used by `equip`/`unequip`/`consume`/`drop` steps. */
    inventory?: InventoryActions;
    /** Crafting used by `craft` steps; pass `recipe => npc.craft(this, recipe)`. */
    craft?: (recipe: string) => CraftResult;
    log?: (message: string) => void;
}

export interface InteractStep extends NpcInteractOptions {
    target: string;
    /** Approach into range first. Default true. */
    approach?: boolean | { distance?: number; run?: boolean };
}

type StepBase = { timeoutMs?: number; retry?: RetryOptions };

export type Step = StepBase &
    (
        | { moveTo: Vec3Like; run?: boolean }
        | { wait: number }
        | { lookAt: Vec3Like }
        | { face: string | Vec3Like }
        | { pick: string }
        | { stop: true }
        | { idle: true }
        | { attack: { target: string; secondary?: boolean } }
        | { jump: true }
        | { interact: InteractStep }
        | { equip: unknown }
        | { unequip: unknown }
        | { consume: unknown }
        | { drop: unknown; count?: number }
        | { craft: string }
        | { follow: { target: string; distance?: number; run?: boolean } }
        | { until: (context: ActionContext) => boolean }
        | { call: (context: ActionContext) => ActionOutcome | void }
        | { action: string; config?: unknown }
    );

type Awaiting = {
    kind: string;
    pausedAt: number | null;
};

export class Plan {
    private readonly actor: PlannerActor;
    private readonly steps: Step[];
    private readonly options: PlanOptions;
    private readonly clock: () => number;
    private readonly registry: ActionRegistry;
    private readonly state: Record<string, unknown> = {};
    private local: Record<string, unknown> = {};
    private index = 0;
    private status: PlanStatus = "idle";
    private reasonValue: string | null = null;
    private startedAt = 0;
    private stepStartedAt = 0;
    private retries = 0;
    private retryAt = 0;
    private awaiting: Awaiting | null = null;
    private settlement: ActionSettlement | null = null;

    constructor(
        actor: PlannerActor,
        steps: Step[],
        options: PlanOptions = {},
        clock: () => number = () => Date.now(),
        registry: ActionRegistry = actions
    ) {
        this.actor = actor;
        this.steps = steps;
        this.options = options;
        this.clock = clock;
        this.registry = registry;
    }

    get stepIndex(): number {
        return this.index;
    }

    get planStatus(): PlanStatus {
        return this.status;
    }

    get reason(): string | null {
        return this.reasonValue;
    }

    tick(view: NpcView, now = this.clock()): PlanStatus {
        if (this.status === "idle") {
            this.status = "running";
            this.startedAt = now;
            this.stepStartedAt = now;
        }

        if (this.status !== "running") {
            return this.status;
        }

        if (this.options.timeoutMs !== undefined && now - this.startedAt > this.options.timeoutMs) {
            return this.fail("plan-timeout", now);
        }

        if (now < this.retryAt) {
            return this.status;
        }

        const step = this.steps[this.index];
        if (step === undefined) {
            this.status = "success";
            return this.status;
        }

        if (step.timeoutMs !== undefined && now - this.stepStartedAt > step.timeoutMs) {
            return this.fail("step-timeout", now);
        }

        this.pollAwaiting(view, now);
        const action = this.resolve(step);
        if (action === null) {
            return this.fail("unknown-action", now);
        }

        const context = this.context(view, now);
        const outcome = action.handler.tick(context, action.config);
        return this.apply(outcome, now);
    }

    cancel(reason = "cancelled"): void {
        if (this.status === "success" || this.status === "failure" || this.status === "cancelled") {
            return;
        }

        this.status = "cancelled";
        this.reasonValue = reason;
        this.awaiting = null;
        this.settlement = null;
    }

    private pollAwaiting(view: NpcView, now: number): void {
        if (this.awaiting === null) {
            return;
        }

        const action = view.action;
        if (action === null) {
            return;
        }

        if (action.kind !== this.awaiting.kind) {
            this.settlement = { kind: this.awaiting.kind, status: "cancelled", reason: "superseded" };
            return;
        }

        if (action.status === "arrived" || action.status === "failed" || action.status === "cancelled") {
            this.settlement = { kind: action.kind, status: action.status, reason: action.reason };
            return;
        }

        if (action.status === "paused") {
            if (this.awaiting.pausedAt === null) {
                this.awaiting.pausedAt = now;
            }

            if (this.options.pauseGraceMs !== undefined && now - this.awaiting.pausedAt > this.options.pauseGraceMs) {
                this.settlement = { kind: action.kind, status: "failed", reason: action.reason ?? "paused" };
            }

            return;
        }

        this.awaiting.pausedAt = null;
    }

    private resolve(step: Step): { handler: ActionHandler<unknown>; config: unknown } | null {
        for (const key of Object.keys(step)) {
            if (key === "timeoutMs" || key === "retry") continue;
            if (this.registry.has(key)) {
                return { handler: this.registry.get(key)!, config: step };
            }
        }

        if ("action" in step && this.registry.has(step.action)) {
            return { handler: this.registry.get(step.action)!, config: step.config };
        }

        return null;
    }

    private context(view: NpcView, now: number): ActionContext {
        const plan = this;
        return {
            view,
            actor: this.actor,
            now,
            state: this.state,
            local: this.local,
            inventory: this.options.inventory ?? null,
            craft: this.options.craft ?? null,
            get awaiting() {
                return plan.awaiting !== null;
            },
            log: message => this.options.log?.(`step ${this.index}: ${message}`),
            sense: id => nearestSense(view, id),
            issue: (kind, issue) => {
                const result = issue();
                if (result !== undefined && !result.ok) {
                    return result;
                }

                this.awaiting = { kind, pausedAt: null };
                return result;
            },
            takeSettled: () => {
                const settlement = this.settlement;
                if (settlement === null) {
                    return null;
                }

                this.settlement = null;
                this.awaiting = null;
                return settlement;
            }
        };
    }

    private apply(outcome: ActionOutcome, now: number): PlanStatus {
        if (outcome === "running") {
            return this.status;
        }

        if (outcome === "success") {
            this.advance(now);
            return this.status;
        }

        if (outcome === "failure") {
            return this.fail("action-failed", now);
        }

        if (outcome.status === "success") {
            this.advance(now);
        } else if (outcome.status === "failure") {
            return this.fail(outcome.reason ?? "action-failed", now);
        }

        return this.status;
    }

    private advance(now: number): void {
        this.index++;
        this.stepStartedAt = now;
        this.retries = 0;
        this.local = {};
        this.awaiting = null;
        this.settlement = null;
        if (this.index >= this.steps.length) {
            this.status = "success";
        }
    }

    private fail(reason: string, now: number): PlanStatus {
        const step = this.steps[this.index];
        const retry = step?.retry;
        if (retry !== undefined && this.retries < retry.attempts) {
            this.retries++;
            this.retryAt = now + (retry.delayMs ?? 0);
            this.stepStartedAt = now;
            this.local = {};
            this.awaiting = null;
            this.settlement = null;
            return this.status;
        }

        this.status = "failure";
        this.reasonValue = reason;
        this.awaiting = null;
        this.settlement = null;
        this.options.log?.(`step ${this.index} failed: ${reason}`);
        return this.status;
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

export type StepOutcome = ActionOutcome;
