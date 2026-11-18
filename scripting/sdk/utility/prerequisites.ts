import type { Goal, GoalCandidate, GoalDescription, GoalExitReason } from "../planning";
import { attemptKey, requirements } from "./observation";

type PendingPrerequisite = {
    readonly goalId: string;
    readonly providerId: string;
};

/**
 * Bounded prerequisite resolver over the existing goal list. It never creates a goal: a provider
 * is another known candidate, and the original goal is resumed through normal selection.
 */
export class PrerequisiteResolver {
    private pending: PendingPrerequisite | null = null;
    private resumeId: string | null = null;
    private readonly attempts = new Map<string, number>();
    private readonly retryAt = new Map<string, number>();

    constructor(
        private readonly maxAttempts: number,
        private readonly cooldownMs: number,
        private readonly debug?: (message: string) => void
    ) {}

    reset(): void {
        this.pending = null;
        this.resumeId = null;
        this.attempts.clear();
        this.retryAt.clear();
    }

    filter(candidates: readonly GoalCandidate[], now: number): GoalCandidate[] {
        return candidates.filter(candidate => (this.retryAt.get(candidate.goal.id) ?? 0) <= now);
    }

    select(
        candidates: readonly GoalCandidate[],
        now: number,
        description: (goal: Goal) => GoalDescription | null,
        pick: (candidates: readonly GoalCandidate[]) => GoalCandidate
    ): GoalCandidate | null {
        let pool = this.filter(candidates, now);
        if (pool.length === 0) {
            return null;
        }

        if (this.resumeId !== null) {
            const resumedId = this.resumeId;
            this.resumeId = null;
            const resumed = pool.find(candidate => candidate.goal.id === resumedId);
            if (resumed !== undefined) {
                const missing = requirements(description(resumed.goal));
                if (missing.length === 0) {
                    this.clearAttempts(resumed.goal.id);
                    this.debug?.(`[prereq] resume ${resumed.goal.id}`);
                    return resumed;
                }

                const provider = this.provider(pool, resumed.goal.id, missing, description);
                if (provider !== null && this.recordAttempt(resumed.goal.id, missing)) {
                    this.debug?.(
                        `[prereq] ${resumed.goal.id} needs ${missing.join(",")} -> ${provider.goal.id}`
                    );
                    this.pending = { goalId: resumed.goal.id, providerId: provider.goal.id };
                    return provider;
                }

                this.debug?.(`[prereq] ${resumed.goal.id} needs ${missing.join(",")} exhausted`);
                this.retryAt.set(resumed.goal.id, now + this.cooldownMs);
                pool = pool.filter(candidate => candidate.goal.id !== resumed.goal.id);
            }
        }

        while (pool.length > 0) {
            const chosen = pick(pool);
            const missing = requirements(description(chosen.goal));
            if (missing.length === 0) {
                this.clearAttempts(chosen.goal.id);
                return chosen;
            }

            const provider = this.provider(pool, chosen.goal.id, missing, description);
            if (provider !== null && this.recordAttempt(chosen.goal.id, missing)) {
                this.debug?.(`[prereq] ${chosen.goal.id} needs ${missing.join(",")} -> ${provider.goal.id}`);
                this.pending = { goalId: chosen.goal.id, providerId: provider.goal.id };
                return provider;
            }

            this.debug?.(`[prereq] ${chosen.goal.id} needs ${missing.join(",")} exhausted`);
            this.retryAt.set(chosen.goal.id, now + this.cooldownMs);
            pool = pool.filter(candidate => candidate !== chosen);
        }

        return null;
    }

    settled(goalId: string, reason: GoalExitReason, now: number): void {
        if (this.pending !== null && this.pending.providerId === goalId) {
            const pending = this.pending;
            this.pending = null;
            if (reason === "success") {
                this.resumeId = pending.goalId;
                this.debug?.(`[prereq] provider ${goalId} solved ${pending.goalId}`);
            } else {
                this.retryAt.set(pending.goalId, now + this.cooldownMs);
                this.debug?.(`[prereq] provider ${goalId} failed; retry ${pending.goalId} later`);
            }
        }

        if (reason === "success") {
            this.clearAttempts(goalId);
            this.retryAt.delete(goalId);
        }
    }

    private provider(
        candidates: readonly GoalCandidate[],
        goalId: string,
        needs: readonly string[],
        description: (goal: Goal) => GoalDescription | null
    ): GoalCandidate | null {
        let best: GoalCandidate | null = null;
        for (const candidate of candidates) {
            if (candidate.goal.id === goalId) {
                continue;
            }

            const providerDescription = description(candidate.goal);
            const provides = providerDescription?.provides ?? [];
            if (!needs.some(need => provides.includes(need))) {
                continue;
            }

            if (requirements(providerDescription).length > 0) {
                continue;
            }

            if (best === null || candidate.score > best.score) {
                best = candidate;
            }
        }

        return best;
    }

    private recordAttempt(goalId: string, missing: readonly string[]): boolean {
        const key = attemptKey(goalId, missing);
        const next = (this.attempts.get(key) ?? 0) + 1;
        this.attempts.set(key, next);
        return next <= this.maxAttempts;
    }

    private clearAttempts(goalId: string): void {
        const prefix = `${goalId}\u0000`;
        for (const key of this.attempts.keys()) {
            if (key.startsWith(prefix)) {
                this.attempts.delete(key);
            }
        }
    }
}
