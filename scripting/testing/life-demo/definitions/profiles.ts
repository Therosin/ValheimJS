import type {
    UtilityAdjustments,
    UtilityConsideration,
    UtilityFeedbackRule
} from "../../../sdk/utility";
import type { VillagerProfile } from "./types";

const BOB: VillagerProfile = {
    stats: {
        Intellect: 0.4,
        Social: 0.9,
        Curiosity: 0.3,
        Ambition: 0.4,
        Patience: 0.8,
        Predictability: 0.85,
        Hunter: 0.4,
        Cook: 0.6,
        Crafter: 0.8,
        Explorer: 0.2
    },
    needs: {
        energy: 0.8,
        social: 0.75,
        curiosity: 0.35
    }
};

const ASTRID: VillagerProfile = {
    stats: {
        Intellect: 0.7,
        Social: 0.4,
        Curiosity: 0.9,
        Ambition: 0.8,
        Patience: 0.5,
        Predictability: 0.65,
        Hunter: 0.8,
        Cook: 0.4,
        Crafter: 0.3,
        Explorer: 0.9
    },
    needs: {
        energy: 0.85,
        social: 0.25,
        curiosity: 0.85
    }
};

export function profileFor(name: string): VillagerProfile {
    return name.trim().toLowerCase() === "astrid" ? ASTRID : BOB;
}

/** Shared utility math. Activity definitions supply contextual factors, not weights. */
export const considerations: Readonly<Record<string, UtilityConsideration>> = {
    social: ({ factors, stats }) => (factors.social ?? 0) * stats.Social * 30,
    novelty: ({ factors, stats }) => (factors.novelty ?? 0) * stats.Curiosity * 30,
    exploration: ({ factors, stats }) => (factors.exploration ?? 0) * stats.Explorer * 30,
    cooking: ({ factors, stats }) => (factors.cooking ?? 0) * stats.Cook * 20,
    crafting: ({ factors, stats }) => (factors.crafting ?? 0) * stats.Crafter * 30,
    hunting: ({ factors, stats }) => (factors.hunting ?? 0) * stats.Hunter * 30,
    risk: ({ factors, stats }) => -(factors.risk ?? 0) * (1 - stats.Patience) * 25,
    ambition: ({ factors, stats }) => (factors.ambition ?? 0) * stats.Ambition * 10,
    effort: ({ factors, stats }) => -(factors.effort ?? 0) * (1 - stats.Patience) * 20,
    distance: ({ factors, stats }) => -(factors.distance ?? 0) * (1 - stats.Explorer) * 20
};

/** Demo-specific feedback. These relationships are not framework defaults. */
export const feedback: readonly UtilityFeedbackRule[] = [
    {
        id: "wilderness-danger",
        adjust: (context): UtilityAdjustments => {
            const attackedAway = context.events.some(
                event => event.name === "damaged" && (event.observed.awayFromHome ?? 0) >= 10
            );
            if (!attackedAway) return {};

            return {
                Explorer: -0.02 * (1 - (context.stats.Explorer ?? 0.5))
            };
        }
    },
    {
        id: "forage-discovery",
        adjust: (context): UtilityAdjustments =>
            context.goal.id === "forage" && (context.observed.distance ?? 0) > 4
                ? { Explorer: 0.005, Curiosity: 0.002 }
                : {}
    },
    {
        id: "away-experience",
        adjust: (context): UtilityAdjustments => {
            if (context.status === "preempted") return {};
            if (context.goal.id !== "forage" && context.goal.id !== "explore" && context.goal.id !== "inspect") {
                return {};
            }

            return (context.observed.awayFromHome ?? 0) >= 12 ? { Explorer: 0.002 } : {};
        }
    },
    {
        id: "home-experience",
        adjust: (context): UtilityAdjustments => {
            if (context.status === "preempted") return {};
            if (context.goal.id !== "rest" && context.goal.id !== "socialize" && context.goal.id !== "seekHome") {
                return {};
            }

            return (context.observed.awayFromHome ?? 0) <= 8 ? { Explorer: -0.001 } : {};
        }
    },
    {
        id: "social-meeting",
        adjust: (context): UtilityAdjustments =>
            context.goal.id === "socialize" && (context.observed.distance ?? 0) > 0
                ? { Social: 0.01 }
                : {}
    },
    {
        id: "hunt-outcome",
        adjust: (context): UtilityAdjustments => {
            if (context.goal.id !== "hunt") return {};

            const injury = Math.max(0, -(context.observed.healthDelta ?? 0));
            const maxHealth = Math.max(1, context.before.maxHealth);
            const injuryCost = injury / maxHealth;
            const loot = Math.max(0, context.observed.loot ?? 0);
            const expected = Math.max(1, context.expected?.satisfies?.food ?? 1);
            const reward = Math.min(1.5, loot / expected);
            const damageEvents = context.events.filter(event => event.name === "damaged").length;
            const durationCost = Math.min(0.35, context.durationMs / 120_000);
            const net = reward * 0.4 - injuryCost * 2 - durationCost - damageEvents * 0.03;

            if (net > 0.1) return { Hunter: 0.004, Ambition: 0.001 };
            if (net < -0.1) return { Hunter: -0.003, Patience: 0.002 };
            return {};
        }
    },
    {
        id: "rest-recovery",
        adjust: (context): UtilityAdjustments =>
            context.goal.id === "rest" && (context.observed.staminaDelta ?? 0) > 0
                ? { Patience: 0.005 }
                : {}
    }
];
