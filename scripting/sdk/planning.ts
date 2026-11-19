import {
    actions,
    position,
    ActionRegistry,
    type ActionContext,
    type ActionHandler,
    type ActionOutcome,
    type ActionResultLike,
    type InventoryActions,
    type PlannerActor,
    type Vec3Like
} from "./actions";
import { context } from "./runtime";
import type { CraftResult, NpcAffordances, NpcInteractOptions, NpcInteractionResult, NpcSensed, NpcView } from "./api/types";

export { ActionRegistry, actions, position, registerAction } from "./actions";
export type {
    ActionContext,
    ActionHandler,
    ActionOutcome,
    ActionResultLike,
    InventoryActions,
    PlannerActor,
    Vec3Like
} from "./actions";

/**
 * Policy-free planning primitives.
 *
 * The native planning host owns plan cursors, timeouts, retries and goal selection. This module
 * keeps the policy surface: goals, callbacks, step types and the `PlanHandle` facade. Neither the
 * host nor the handle knows what a goal means or which reaction is correct. Built-in JS actions
 * live in `sdk/actions/*`; plugins can register more with `registerAction`.
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
    readonly planReason: string | null;
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

export interface GoalDescriptionContext {
    readonly view: NpcView;
    readonly now: number;
    readonly stats: Readonly<Record<string, number>>;
}

/**
 * Contextual facts about a goal, evaluated before selection. Factors are facts, not utility
 * deltas; a selection policy decides how they matter. Requirements and provisions are capability
 * metadata used by prerequisite resolvers.
 */
export interface GoalDescription {
    readonly base?: number;
    readonly factors?: Readonly<Record<string, number>>;
    readonly requires?: readonly string[];
    readonly provides?: readonly string[];
    /** Expected effects the activity claims to deliver. The runtime still has to apply them. */
    readonly satisfies?: Readonly<Record<string, number>>;
}

export interface BehaviourActionContext {
    args: Record<string, unknown>;
    state: Record<string, unknown>;
    this: unknown;
    deltaTime: number;
    interrupted: boolean;
}

export type BehaviourOutcome = "success" | "failure" | "running";

export interface BehaviourGoalOptions {
    /** Tree id from `ai.compile` or `planning.behaviourCompile`. */
    readonly tree: string;
    /** Additional initial state merged over the tree defaults. */
    readonly state?: Record<string, unknown> | (() => Record<string, unknown>);
    action?(name: string, context: BehaviourActionContext): BehaviourOutcome;
    condition?(name: string, context: BehaviourActionContext): boolean;
    exec?(code: string, context: BehaviourActionContext): void;
    random?(): number;
    maxJumpsPerTick?: number;
}

export interface Goal {
    /** Stable plugin-defined id. */
    readonly id: string;
    /**
     * Existing score when no `describe` is present. A selection policy may replace this with a
     * contextual evaluation when `describe` exists; the Planner itself only uses the score.
     */
    score(view: NpcView): number;
    /**
     * Optional contextual description for selection policies. The Planner ignores this. When a
     * policy consumes it, the description replaces `score`; a goal that wants to retain its old
     * urgency can return it as `base`.
     */
    describe?(context: GoalDescriptionContext): GoalDescription | void;
    /** Optional hard gate evaluated before `score`. */
    eligible?(view: NpcView): boolean;
    enter?(ctx: GoalContext): void;
    /** Advance the goal. Omit for pure behaviour goals; the host owns the tree lifecycle. */
    tick?(ctx: GoalContext): GoalStatus;
    /** Native behaviour tree instantiated when this goal becomes active. */
    behaviour?: BehaviourGoalOptions;
    exit?(ctx: GoalContext, reason: GoalExitReason): void;
    /** Optional pause after a finished or preempted goal, chosen by the plugin. */
    cooldownMs?: number;
    /** Required score margin to preempt this goal. Default 0. */
    hysteresis?: number;
    /** Minimum time this goal may stay active before normal preemption. */
    commitMs?: number;
    /** May preempt a committed goal (survival/urgent needs). */
    urgent?: boolean;
}

/** One scored goal that survived eligibility, cooldown and the score threshold. */
export interface GoalCandidate {
    readonly goal: Goal;
    readonly score: number;
}

export interface PlannerOptions {
    /** Minimum score to select any goal. Default 0. */
    threshold?: number;
    /** How often goals are re-scored. Default 500 ms. */
    replanMs?: number;
    /** Clock used for cooldowns/requests; defaults to `Date.now`. */
    now?: () => number;
    log?: (message: string) => void;
    /** Gated selection/preemption diagnostics. */
    debug?: (message: string) => void;
    /**
     * Chooses among goal candidates when no goal is active. Defaults to the highest score.
     * Preemption still compares against the highest score, so a policy cannot make the active
     * goal unstable. Return a candidate from the supplied list, or null to stay idle. A candidate
     * that is not in the supplied list is treated as null.
     */
    select?: (candidates: readonly GoalCandidate[], view: NpcView, now: number) => GoalCandidate | null;
}

export class Planner {
    readonly id: string;

    private readonly actor: PlannerActor;
    private readonly clock: () => number;
    private readonly log?: (message: string) => void;
    private readonly debug?: (message: string) => void;
    private readonly selectPolicy?: PlannerOptions["select"];
    private goalsValue: Goal[] = [];
    private readonly goalsById = new Map<string, Goal>();
    private activePlan: PlanHandle | null = null;

    constructor(actor: PlannerActor, options: PlannerOptions = {}) {
        this.actor = actor;
        this.clock = options.now ?? (() => Date.now());
        this.log = options.log;
        this.debug = options.debug;
        this.selectPolicy = options.select;
        this.id = context().host.api.planning.plannerCreate(
            actor,
            {
                threshold: options.threshold ?? 0,
                replanMs: options.replanMs ?? 500,
                select: options.select
            },
            ((kind: string, ...args: unknown[]) => this.dispatch(kind, args)) as unknown as (
                ...args: unknown[]
            ) => unknown
        );
    }

    get current(): string | null {
        return context().host.api.planning.plannerCurrent(this.id);
    }

    get currentElapsedMs(): number {
        return context().host.api.planning.plannerCurrentElapsedMs(this.id);
    }

    setGoals(goals: Goal[]): void {
        const metadata = goals.map(goal => {
            const behaviour = (goal as Goal & { behaviour?: unknown }).behaviour;
            return {
                id: goal.id,
                hasTick: typeof goal.tick === "function",
                cooldownMs: goal.cooldownMs,
                hysteresis: goal.hysteresis,
                commitMs: goal.commitMs,
                urgent: goal.urgent,
                behaviour
            };
        });

        // Host finishes the old active goal through the dispatcher before the new catalogue is
        // installed, so keep the old map alive until plannerSetGoals returns.
        context().host.api.planning.plannerSetGoals(this.id, metadata);
        this.goalsValue = goals.slice();
        this.goalsById.clear();
        for (const goal of goals) {
            this.goalsById.set(goal.id, goal);
        }
        this.activePlan = null;
    }

    /** Forces one goal on the next eligible tick, bypassing its score. */
    request(id: string, _view: NpcView, untilMs = 30_000): void {
        if (!this.goalsById.has(id)) return;
        context().host.api.planning.plannerRequest(this.id, id, this.clock() + untilMs);
    }

    reset(): void {
        context().host.api.planning.plannerReset(this.id);
        this.activePlan = null;
    }

    tick(view: NpcView, now = this.clock()): string | null {
        return context().host.api.planning.plannerTick(this.id, view, now);
    }

    /** Scored goals that currently pass eligibility, cooldown and the plugin threshold. */
    candidates(view: NpcView, now = this.clock()): readonly GoalCandidate[] {
        const raw = context().host.api.planning.plannerCandidates(this.id, view, now) as {
            goal: string;
            score: number;
        }[];
        return raw
            .map(candidate => {
                const goal = this.goalsById.get(candidate.goal);
                return goal === undefined ? null : { goal, score: candidate.score };
            })
            .filter((candidate): candidate is GoalCandidate => candidate !== null);
    }

    dispose(): void {
        context().host.api.planning.plannerDispose(this.id);
        this.goalsValue = [];
        this.goalsById.clear();
        this.activePlan = null;
    }

    private dispatch(kind: string, args: unknown[]): unknown {
        switch (kind) {
            case "score": {
                const goal = this.goal(args[0]);
                return goal?.score(args[1] as NpcView);
            }
            case "eligible": {
                const goal = this.goal(args[0]);
                return goal?.eligible?.(args[1] as NpcView) ?? true;
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
                    goal.exit?.(this.goalContext(goal, args, args[3] as GoalExitReason), args[3] as GoalExitReason);
                } finally {
                    this.activePlan = null;
                }
                return undefined;
            }
            case "select": {
                if (this.selectPolicy === undefined) return null;
                const raw = args[0] as { goal: string; score: number }[];
                const candidates = raw
                    .map(candidate => {
                        const goal = this.goalsById.get(candidate.goal);
                        return goal === undefined ? null : { goal, score: candidate.score };
                    })
                    .filter((candidate): candidate is GoalCandidate => candidate !== null);
                return this.selectPolicy(candidates, args[1] as NpcView, args[2] as number)?.goal.id ?? null;
            }
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
        const planner = this;
        return {
            view,
            actor: this.actor,
            now,
            goal: goal.id,
            planStatus,
            planReason,
            log: message => this.log?.(`[${goal.id}] ${message}`),
            moveTo: (x, y, z, run = false) => this.actor.moveTo(x, y, z, run),
            wait: seconds => this.actor.wait(seconds),
            face: id => this.actor.face(id),
            pick: id => this.actor.pick(id),
            interact: (id, options = {}) => {
                if (this.actor.interact === undefined) {
                    return { ok: false, reason: "unsupported", verb: options.verb ?? "", count: 0 };
                }

                return this.actor.interact(id, options);
            },
            affordances: id => {
                if (this.actor.affordances === undefined) {
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

                return this.actor.affordances(id);
            },
            run: (steps, options = {}) => {
                const planId = context().host.api.planning.planCreate(
                    this.actor,
                    steps,
                    options,
                    stepDispatch(this.actor, options) as unknown as (...values: unknown[]) => unknown
                );
                context().host.api.planning.plannerSetPlan(this.id, planId);
                const handle = new PlanHandle(planId);
                this.activePlan = handle;
                return handle;
            },
            cancelPlan: reason => {
                if (this.activePlan !== null) {
                    this.activePlan.cancel(reason);
                    this.activePlan = null;
                }
                context().host.api.planning.plannerSetPlan(this.id, null);
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

export interface HarvestStep {
    target: string;
    /** Approach into range first. Default true. */
    approach?: boolean | { distance?: number; run?: boolean };
}

export type Step = StepBase &
    (
        | { moveTo: Vec3Like; run?: boolean }
        | { wait: number }
        | { lookAt: Vec3Like }
        | { face: string | Vec3Like }
        | { pick: string }
        | { harvest: HarvestStep }
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

export class PlanHandle {
    readonly id: string;

    constructor(id: string) {
        this.id = id;
    }

    get stepIndex(): number {
        return context().host.api.planning.planStepIndex(this.id);
    }

    get planStatus(): PlanStatus {
        return context().host.api.planning.planStatus(this.id) as PlanStatus;
    }

    get reason(): string | null {
        return context().host.api.planning.planReason(this.id);
    }

    /** Advances the native plan. Called by the owning Planner. */
    tick(view: NpcView, now: number): PlanStatus {
        return context().host.api.planning.planTick(this.id, view, now).status;
    }

    cancel(reason = "cancelled"): void {
        context().host.api.planning.planCancel(this.id, reason);
    }

    release(): void {
        context().host.api.planning.planRelease(this.id);
    }
}

/** Compatibility alias. Plans are created by `GoalContext.run`; they are no longer constructible. */
export type Plan = PlanHandle;

export function stepDispatch(actor: PlannerActor, options: PlanOptions) {
    return (
        _kind: string,
        step: Step,
        view: NpcView,
        now: number,
        state: Record<string, unknown>,
        local: Record<string, unknown>
    ): ActionOutcome => {
        const resolved = resolveAction(step);
        if (resolved === null) {
            return { status: "failure", reason: "unknown-action" };
        }

        const contextValue: ActionContext = {
            view,
            actor,
            now,
            state,
            local,
            inventory: options.inventory ?? null,
            craft: options.craft ?? null,
            log: message => options.log?.(message),
            sense: id => nearestSense(view, id)
        };
        return resolved.handler.tick(contextValue, resolved.config);
    };
}

function resolveAction(step: Step): { handler: ActionHandler<unknown>; config: unknown } | null {
    for (const key of Object.keys(step)) {
        if (key === "timeoutMs" || key === "retry") continue;
        if (actions.has(key)) {
            return { handler: actions.get(key)!, config: step };
        }
    }

    if ("action" in step && actions.has(step.action)) {
        return { handler: actions.get(step.action)!, config: step.config };
    }

    return null;
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
