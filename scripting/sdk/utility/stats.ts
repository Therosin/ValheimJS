import type { UtilityAdjustments, UtilityStats } from "./types";

/** Normalized preference scores. Plugins may add their own affinity keys. */
export const defaultUtilityStats: Readonly<UtilityStats> = Object.freeze({
    Intellect: 0.5,
    Social: 0.5,
    Curiosity: 0.5,
    Ambition: 0.5,
    Patience: 0.5,
    Predictability: 0.5,
    Hunter: 0.25,
    Cook: 0.25,
    Crafter: 0.25,
    Explorer: 0.25
});

export function normalizeStats(stats: Partial<UtilityStats>): UtilityStats {
    const normalized: UtilityStats = {};
    for (const [key, value] of Object.entries(stats)) {
        if (typeof value === "number" && Number.isFinite(value)) {
            normalized[key] = clamp01(value);
        }
    }

    return normalized;
}

export function adjustStats(
    stats: UtilityStats,
    adjustments: UtilityAdjustments,
    maxStep: number
): { next: UtilityStats; applied: UtilityAdjustments; changed: boolean } {
    const next = { ...stats };
    const applied: Record<string, number> = {};
    let changed = false;

    for (const [key, value] of Object.entries(adjustments)) {
        if (typeof value !== "number" || !Number.isFinite(value)) {
            continue;
        }

        const delta = clamp(value, -maxStep, maxStep);
        if (delta === 0) {
            continue;
        }

        const current = next[key] ?? 0.5;
        const updated = clamp01(current + delta);
        if (updated === current) {
            continue;
        }

        next[key] = updated;
        applied[key] = updated - current;
        changed = true;
    }

    return { next, applied, changed };
}

export function clamp(value: number, minimum: number, maximum: number): number {
    return Math.max(minimum, Math.min(maximum, value));
}

export function clamp01(value: number): number {
    return clamp(value, 0, 1);
}
