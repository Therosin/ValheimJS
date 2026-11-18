import type { NpcView } from "../api/types";
import {
    Planner,
    type Goal,
    type GoalCandidate,
    type GoalContext,
    type GoalDescription,
    type GoalExitReason
} from "../planning";
import { collectFeedback } from "./feedback";
import { observeView, requirements, snapshotDelta, transitionFacts } from "./observation";
import { PrerequisiteResolver } from "./prerequisites";
import { describeGoal, pickCandidate, scoreGoal } from "./selection";
import { adjustStats, clamp01, defaultUtilityStats, normalizeStats } from "./stats";
import type {
    UtilityAIOptions,
    UtilityCandidate,
    UtilityEvent,
    UtilityExpectation,
    UtilityFeedbackContext,
    UtilityStats
} from "./types";

/**
 * Optional utility layer over the existing Planner.
 *
 * The Planner remains the activity catalogue and execution scheduler. UtilityAI only supplies a
 * selection policy and shared experience evaluation. Activities are ordinary Goals.
 */
export class UtilityAI {
    readonly planner: Planner;

    private readonly random: () => number;
    private readonly store?: UtilityAIOptions["store"];
    private readonly maxStep: number;
    private readonly clock: () => number;
    private readonly considerations: NonNullable<UtilityAIOptions["considerations"]>;
    private readonly feedback: NonNullable<UtilityAIOptions["feedback"]>;
    private readonly debug?: (message: string) => void;
    private readonly snapshot?: UtilityAIOptions["snapshot"];
    private readonly log?: (message: string) => void;
    private readonly prerequisites: PrerequisiteResolver;

    private goalsValue: Goal[] = [];
    private readonly descriptions = new Map<string, GoalDescription | null>();
    private statsValue: UtilityStats;
    private latestView: NpcView | null = null;
    private latestOutcome: UtilityFeedbackContext | null = null;
    private suppressSettlement = false;

    private activeGoal: Goal | null = null;
    private activeDescription: GoalDescription | null = null;
    private activeExpectation: UtilityExpectation | null = null;
    private activeSince = 0;
    private activeBefore: NpcView | null = null;
    private activeSnapshot: Readonly<Record<string, number>> = {};
    private activeEvents: UtilityEvent[] = [];

    constructor(options: UtilityAIOptions) {
        this.random = options.random ?? Math.random;
        this.store = options.store;
        this.maxStep = Math.max(0, options.maxStep ?? 0.02);
        this.clock = options.planner?.now ?? (() => Date.now());
        this.considerations = options.considerations ?? {};
        this.feedback = options.feedback ?? [];
        this.debug = options.debug;
        this.snapshot = options.snapshot;
        this.log = options.planner?.log;
        this.prerequisites = new PrerequisiteResolver(
            Math.max(1, options.maxPrerequisiteAttempts ?? 2),
            Math.max(0, options.prerequisiteCooldownMs ?? 30_000),
            this.debug
        );

        const loaded = options.store?.load() ?? {};
        this.statsValue = normalizeStats({ ...defaultUtilityStats, ...options.stats, ...loaded });
        this.debug?.(`[stats] initial ${formatStats(this.statsValue)}`);
        this.planner = new Planner(options.actor, {
            ...options.planner,
            debug: this.debug,
            select: (candidates, view, now) => this.select(candidates, view, now)
        });
        this.setGoals(options.goals ?? []);
    }

    get stats(): Readonly<UtilityStats> {
        return { ...this.statsValue };
    }

    get goals(): readonly Goal[] {
        return this.goalsValue.slice();
    }

    get lastOutcome(): UtilityFeedbackContext | null {
        return this.latestOutcome;
    }

    get current(): string | null {
        return this.planner.current;
    }

    /** Replaces the known goals. The Planner's goal list remains the catalogue. */
    setGoals(goals: readonly Goal[]): void {
        this.suppressSettlement = true;
        try {
            this.planner.setGoals(goals.map(goal => this.wrap(goal)));
        } finally {
            this.suppressSettlement = false;
        }

        this.goalsValue = goals.slice();
        this.descriptions.clear();
        this.prerequisites.reset();
        this.clearActive();
    }

    /** Scored goals that pass their existing conditions and current prerequisites right now. */
    available(view: NpcView, now = this.clock()): UtilityCandidate[] {
        const result: UtilityCandidate[] = [];
        for (const candidate of this.prerequisites.filter(this.planner.candidates(view, now), now)) {
            const description = this.descriptionFor(candidate.goal, view, now);
            if (requirements(description).length > 0) {
                continue;
            }

            result.push({ goal: candidate.goal, score: candidate.score });
        }

        return result;
    }

    tick(view: NpcView, now = this.clock()): string | null {
        this.latestView = view;
        return this.planner.tick(view, now);
    }

    /**
     * Records an observation while an activity is active. The event keeps a context snapshot
     * from this moment, so an attack away from home is still identifiable after the NPC returns.
     */
    record(name: string, data?: unknown, view?: NpcView): boolean {
        if (this.activeGoal === null) {
            return false;
        }

        const current = view ?? this.latestView;
        if (current === null) {
            return false;
        }

        this.latestView = current;
        const observed = observeView(current);
        this.activeEvents.push({
            name,
            data,
            at: this.clock(),
            observed
        });
        this.debug?.(
            `[event] ${name} away=${observed.awayFromHome.toFixed(1)} ` +
                `hp=${observed.health.toFixed(0)} sta=${observed.stamina.toFixed(0)}`
        );
        return true;
    }

    request(id: string, view: NpcView, untilMs?: number): void {
        this.prerequisites.reset();
        this.planner.request(id, view, untilMs);
    }

    reset(): void {
        this.suppressSettlement = true;
        try {
            this.planner.reset();
        } finally {
            this.suppressSettlement = false;
        }

        this.prerequisites.reset();
        this.descriptions.clear();
        this.latestView = null;
        this.latestOutcome = null;
        this.clearActive();
    }

    dispose(): void {
        this.reset();
        this.planner.dispose();
    }

    setStats(stats: Partial<UtilityStats>): void {
        this.statsValue = normalizeStats({ ...this.statsValue, ...stats });
        this.store?.save(this.stats);
        this.debug?.(`[stats] set ${formatStats(this.statsValue)}`);
    }

    private wrap(goal: Goal): Goal {
        return {
            id: goal.id,
            score: view => this.score(goal, view),
            eligible: view => goal.eligible?.(view) ?? true,
            enter: context => this.enter(goal, context),
            tick: context => goal.tick(context),
            exit: (context, reason) => this.exit(goal, context, reason),
            cooldownMs: goal.cooldownMs,
            hysteresis: goal.hysteresis,
            commitMs: goal.commitMs,
            urgent: goal.urgent
        };
    }

    private score(goal: Goal, view: NpcView): number {
        const now = this.clock();
        const description = this.describe(goal, view, now);
        this.descriptions.set(goal.id, description);
        return scoreGoal(goal, description, view, now, this.statsValue, this.considerations);
    }

    private describe(goal: Goal, view: NpcView, now: number): GoalDescription | null {
        return describeGoal(goal, view, now, this.statsValue);
    }

    private descriptionFor(goal: Goal, view: NpcView, now: number): GoalDescription | null {
        if (this.descriptions.has(goal.id)) {
            return this.descriptions.get(goal.id)!;
        }

        return this.describe(goal, view, now);
    }

    private select(
        candidates: readonly GoalCandidate[],
        view: NpcView,
        now: number
    ): GoalCandidate | null {
        const chosen = this.prerequisites.select(
            candidates,
            now,
            goal => this.descriptionFor(goal, view, now),
            pool => pickCandidate(pool, clamp01(this.stat("Predictability")), this.random)
        );
        if (this.debug !== undefined) {
            const summary = [...candidates]
                .sort((left, right) => right.score - left.score)
                .slice(0, 6)
                .map(candidate => `${candidate.goal.id}=${candidate.score.toFixed(1)}`)
                .join(" ");
            this.debug(`[select] ${chosen?.goal.id ?? "idle"}${summary.length > 0 ? ` | ${summary}` : ""}`);
        }
        return chosen;
    }

    private enter(goal: Goal, context: GoalContext): void {
        this.latestView = context.view;
        this.activeGoal = goal;
        this.activeDescription = this.descriptionFor(goal, context.view, context.now);
        this.activeExpectation = this.activeDescription === null
            ? null
            : {
                  factors: this.activeDescription.factors ?? {},
                  satisfies: this.activeDescription.satisfies ?? {}
              };
        this.activeSince = context.now;
        this.activeBefore = context.view;
        this.activeSnapshot = this.snapshot?.(context) ?? {};
        this.activeEvents = [];
        goal.enter?.(context);
    }

    private exit(goal: Goal, context: GoalContext, reason: GoalExitReason): void {
        try {
            goal.exit?.(context, reason);
        } finally {
            this.settle(goal, context, reason);
        }
    }

    private settle(goal: Goal, context: GoalContext, reason: GoalExitReason): void {
        if (this.suppressSettlement) {
            this.clearActive();
            return;
        }

        const before = this.activeBefore ?? context.view;
        const after = context.view;
        this.latestView = after;

        try {
            const durationMs = Math.max(0, context.now - this.activeSince);
            const afterSnapshot = this.snapshot?.(context) ?? {};
            const observed = {
                ...snapshotDelta(this.activeSnapshot, afterSnapshot),
                ...transitionFacts(before, after, durationMs)
            };
            const outcome: UtilityFeedbackContext = {
                goal,
                description: this.activeDescription,
                expected: this.activeExpectation,
                status: reason,
                durationMs,
                before,
                after,
                stats: this.stats,
                observed,
                events: this.activeEvents.slice(),
                planStatus: context.planStatus
            };
            this.latestOutcome = outcome;
            this.debug?.(
                `[settle] ${goal.id} ${reason} ${(durationMs / 1000).toFixed(1)}s ` +
                    `dist=${observed.distance.toFixed(1)} hp=${observed.healthDelta.toFixed(1)} ` +
                    `food=${observed.foodDelta.toFixed(0)} events=${outcome.events.length}`
            );

            this.prerequisites.settled(goal.id, reason, context.now);

            const adjustments = collectFeedback(this.feedback, outcome, this.log);
            const applied = adjustStats(this.statsValue, adjustments, this.maxStep);
            if (applied.changed) {
                this.statsValue = applied.next;
                this.store?.save(this.stats);
                this.debug?.(
                    `[feedback] ${goal.id} ${formatAdjustments(applied.applied)} ` +
                        `-> ${formatStats(this.statsValue)}`
                );
            }
        } finally {
            this.clearActive();
        }
    }

    private clearActive(): void {
        this.activeGoal = null;
        this.activeDescription = null;
        this.activeExpectation = null;
        this.activeBefore = null;
        this.activeSnapshot = {};
        this.activeEvents = [];
    }

    private stat(key: string): number {
        return this.statsValue[key] ?? 0.5;
    }
}

function formatAdjustments(adjustments: Readonly<Record<string, number>>): string {
    return Object.entries(adjustments)
        .map(([key, value]) => `${key}${value >= 0 ? "+" : ""}${value.toFixed(3)}`)
        .join(" ");
}

function formatStats(stats: Readonly<UtilityStats>): string {
    return Object.entries(stats)
        .map(([key, value]) => `${key}=${value.toFixed(2)}`)
        .join(" ");
}
