import type { NpcView } from "../api/types";
import type { Goal, GoalCandidate, GoalDescription } from "../planning";
import { clamp01 } from "./stats";
import type { UtilityConsideration, UtilityStats } from "./types";

export function describeGoal(
    goal: Goal,
    view: NpcView,
    now: number,
    stats: Readonly<UtilityStats>
): GoalDescription | null {
    if (goal.describe === undefined) {
        return null;
    }

    return goal.describe({ view, now, stats }) ?? {};
}

export function scoreGoal(
    goal: Goal,
    description: GoalDescription | null,
    view: NpcView,
    now: number,
    stats: Readonly<UtilityStats>,
    considerations: Readonly<Record<string, UtilityConsideration>>
): number {
    if (description === null) {
        return goal.score(view);
    }

    const factors = description.factors ?? {};
    let score = description.base ?? 0;
    for (const consideration of Object.values(considerations)) {
        score += consideration({ goal, description, factors, view, now, stats });
    }

    return score;
}

export function pickCandidate(
    candidates: readonly GoalCandidate[],
    predictability: number,
    random: () => number
): GoalCandidate {
    if (candidates.length === 1) {
        return candidates[0]!;
    }

    const sorted = [...candidates].sort((left, right) => right.score - left.score);
    const confidence = clamp01(predictability);
    if (confidence >= 1) {
        return sorted[0]!;
    }

    const slope = 4 * confidence;
    const weights = sorted.map((_candidate, index) => Math.exp(-slope * index));
    const total = weights.reduce((sum, weight) => sum + weight, 0);
    let roll = random() * total;
    for (let index = 0; index < sorted.length; index++) {
        roll -= weights[index]!;
        if (roll <= 0) {
            return sorted[index]!;
        }
    }

    return sorted[sorted.length - 1]!;
}
