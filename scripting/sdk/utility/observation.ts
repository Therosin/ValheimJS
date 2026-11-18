import type { NpcView } from "../api/types";
import type { GoalDescription } from "../planning";

export function observeView(view: NpcView): Record<string, number> {
    return {
        health: view.health,
        maxHealth: view.maxHealth,
        healthRatio: view.maxHealth > 0 ? view.health / view.maxHealth : 0,
        stamina: view.self?.stamina ?? 0,
        staminaRatio: view.self?.staminaRatio ?? 0,
        food: view.self?.food.length ?? 0,
        x: view.x,
        y: view.y,
        z: view.z,
        awayFromHome: horizontal(view, view.home),
        combat: view.combat.engaged ? 1 : 0,
        isDay: view.environment?.isDay ? 1 : 0,
        nearbyPlayers: view.nearby.filter(entry => entry.isPlayer && !entry.isDead).length
    };
}

export function transitionFacts(
    before: NpcView,
    after: NpcView,
    durationMs: number
): Record<string, number> {
    const beforeFacts = observeView(before);
    const afterFacts = observeView(after);
    return {
        healthDelta: afterFacts.health - beforeFacts.health,
        staminaDelta: afterFacts.stamina - beforeFacts.stamina,
        foodDelta: afterFacts.food - beforeFacts.food,
        distance: Math.hypot(after.x - before.x, after.z - before.z),
        awayFromHome: afterFacts.awayFromHome,
        duration: durationMs / 1000
    };
}

export function snapshotDelta(
    before: Readonly<Record<string, number>>,
    after: Readonly<Record<string, number>>
): Record<string, number> {
    const result: Record<string, number> = {};
    for (const key of new Set([...Object.keys(before), ...Object.keys(after)])) {
        const start = before[key];
        const end = after[key];
        if (typeof start === "number" && typeof end === "number") {
            result[key] = end - start;
        } else if (typeof end === "number") {
            result[key] = end;
        }
    }

    return result;
}

export function requirements(description: GoalDescription | null): readonly string[] {
    if (description?.requires === undefined) {
        return [];
    }

    return [...new Set(description.requires.filter(need => typeof need === "string" && need.length > 0))];
}

export function attemptKey(goalId: string, missing: readonly string[]): string {
    return `${goalId}\u0000${[...missing].sort().join("\u0000")}`;
}

function horizontal(left: NpcView, right: { x: number; z: number }): number {
    return Math.hypot(left.x - right.x, left.z - right.z);
}
