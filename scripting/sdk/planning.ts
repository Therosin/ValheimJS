import type { NpcAffordances, NpcInteractOptions, NpcInteractionResult, NpcSensed, NpcView } from "./api/types";

/**
 * Policy-free goal scheduler.
 *
 * The planner owns only mechanics: score goals supplied by the plugin, pick the best eligible
 * one, preempt with hysteresis, invoke goal callbacks, and apply optional cooldowns. It has no
 * built-in goals, reactions, priorities or interpretation. What a goal means and when it should
 * win is entirely plugin policy.
 */

export type GoalStatus = "running" | "success" | "failure";
export type GoalExitReason = "success" | "failure" | "preempted";

/** Minimal action surface a planner needs. Matches `ValheimBaseNPC`. */
export interface PlannerActor {
    moveTo(x: number, y: number, z: number, run: boolean): unknown;
    idle(): unknown;
    stop(): unknown;
    wait(seconds: number): unknown;
    lookAt(x: number, y: number, z: number): unknown;
    face(id: string): unknown;
    pick(id: string): unknown;
    interact?(id: string, options?: NpcInteractOptions): NpcInteractionResult;
    affordances?(id: string): NpcAffordances;
}

export interface GoalContext {
    readonly view: NpcView;
    readonly actor: PlannerActor;
    readonly now: number;
    readonly goal: string;
    log(message: string): void;
    moveTo(x: number, y: number, z: number, run?: boolean): unknown;
    wait(seconds: number): unknown;
    face(id: string): unknown;
    pick(id: string): unknown;
    interact(id: string, options?: NpcInteractOptions): NpcInteractionResult;
    affordances(id: string): NpcAffordances;
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
    private activeSince = 0;
    private lastReplan = 0;
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

        this.active = null;
        this.goals = goals.slice();
        this.cooldownUntil.clear();
        this.requested = null;
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

        this.active = null;
        this.cooldownUntil.clear();
        this.requested = null;
    }

    tick(view: NpcView, now = this.clock()): string | null {
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

        this.active = null;
        if (goal.cooldownMs !== undefined && goal.cooldownMs > 0) {
            this.cooldownUntil.set(goal.id, now + goal.cooldownMs);
        }

        if (goal.exit === undefined) {
            return;
        }

        try {
            const view = this.lastView;
            if (view !== null) {
                goal.exit(this.context(view, goal, now), reason);
            }
        } catch (error) {
            this.log?.(`goal '${goal.id}' exit failed: ${String(error)}`);
        }
    }

    private lastView: NpcView | null = null;

    private context(view: NpcView, goal: Goal, now: number): GoalContext {
        this.lastView = view;
        const actor = this.actor;
        return {
            view,
            actor,
            now,
            goal: goal.id,
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
