import type { UtilityStats } from "./types";

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
