import type { NpcView } from "../api/types";
import type {
    Goal,
    GoalContext,
    GoalDescription,
    GoalExitReason,
    PlanStatus,
    PlannerActor,
    PlannerOptions
} from "../planning";

export type UtilityStats = Record<string, number>;

export interface UtilityConsiderationContext {
    readonly goal: Goal;
    readonly description: GoalDescription;
    readonly factors: Readonly<Record<string, number>>;
    readonly view: NpcView;
    readonly now: number;
    readonly stats: Readonly<UtilityStats>;
}

/** Shared selection consideration; returns a utility contribution. */
export type UtilityConsideration = (context: UtilityConsiderationContext) => number;

/** One observation recorded while the activity was active. */
export interface UtilityEvent {
    readonly name: string;
    readonly data: unknown;
    readonly at: number;
    /** Context captured at the moment of the event, not at activity settlement. */
    readonly observed: Readonly<Record<string, number>>;
}

export type UtilityAdjustments = Readonly<Record<string, number>>;

/** The description that was committed when the activity actually started. */
export interface UtilityExpectation {
    readonly factors: Readonly<Record<string, number>>;
    readonly satisfies: Readonly<Record<string, number>>;
}

export interface UtilityFeedbackContext {
    readonly goal: Goal;
    readonly description: GoalDescription | null;
    /** Captured at activity entry; never taken from a discarded candidate pass. */
    readonly expected: UtilityExpectation | null;
    readonly status: GoalExitReason;
    readonly durationMs: number;
    readonly before: NpcView;
    readonly after: NpcView;
    /** Stats before this outcome's adjustment. */
    readonly stats: Readonly<UtilityStats>;
    /** State deltas plus plugin snapshot deltas. */
    readonly observed: Readonly<Record<string, number>>;
    readonly events: readonly UtilityEvent[];
    readonly planStatus: PlanStatus | null;
}

export type UtilityFeedback = (context: UtilityFeedbackContext) => UtilityAdjustments;

/** One shared feedback rule. Rules are reusable across activities. */
export interface UtilityFeedbackRule {
    readonly id: string;
    readonly adjust: UtilityFeedback;
}

export interface UtilityStatsStore {
    load(): Partial<UtilityStats> | null | undefined;
    save(stats: Readonly<UtilityStats>): void;
}

export interface UtilityAIOptions {
    actor: PlannerActor;
    goals?: readonly Goal[];
    stats?: Partial<UtilityStats>;
    /** Persistence owned by the plugin, for example `npc.memory`/`npc.setMemory`. */
    store?: UtilityStatsStore;
    /** Selection randomness source. Defaults to `Math.random`. */
    random?: () => number;
    /** Maximum absolute stat change per activity settlement. Default 0.02. */
    maxStep?: number;
    /** Attempts to resolve one missing-requirement set before cooling the activity down. Default 2. */
    maxPrerequisiteAttempts?: number;
    /** Cooldown after a prerequisite provider fails or the attempt bound is hit. Default 30 s. */
    prerequisiteCooldownMs?: number;
    /** Shared selection considerations, evaluated against each goal's description. */
    considerations?: Readonly<Record<string, UtilityConsideration>>;
    /** Shared feedback rules, evaluated after every activity settlement. */
    feedback?: readonly UtilityFeedbackRule[];
    /** Gated diagnostic stream for selection, events and feedback. */
    debug?: (message: string) => void;
    /** Optional counters captured at activity start/settlement; differences become observed facts. */
    snapshot?: (context: GoalContext) => Readonly<Record<string, number>>;
    /** Planner policy. `select` is owned by UtilityAI. */
    planner?: Omit<PlannerOptions, "select">;
}

export interface UtilityCandidate {
    readonly goal: Goal;
    readonly score: number;
}
