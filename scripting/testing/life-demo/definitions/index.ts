import { UtilityAI, type UtilityStatsStore } from "../../../sdk/utility";
import { createActivities } from "./activities";
import { considerations, feedback, profileFor } from "./profiles";
import type { VillagerActivity, VillagerContext } from "./types";

export { profileFor } from "./profiles";
export type { VillagerActivity, VillagerContext, VillagerProfile } from "./types";

export interface VillagerAIOptions {
    context: VillagerContext;
    name: string;
    trees: Record<VillagerActivity, string>;
    store?: UtilityStatsStore;
    random?: () => number;
    debug?: boolean;
    log?: (message: string) => void;
}

export function createVillagerAI(options: VillagerAIOptions): UtilityAI {
    const profile = profileFor(options.name);
    return new UtilityAI({
        actor: options.context.actor,
        goals: createActivities(options.context, options.trees),
        stats: profile.stats,
        considerations,
        feedback,
        snapshot: () => {
            const inventory = options.context.actor.getInventory();
            const loot = inventory.items
                .filter(item =>
                    item.prefab === "RawMeat" ||
                    item.prefab === "LeatherScraps" ||
                    item.prefab === "TrophyBoar"
                )
                .reduce((total, item) => total + item.stack, 0);
            return {
                food: inventory.items.filter(item => item.type === "food").length,
                wood: inventory.count("Wood"),
                loot
            };
        },
        store: options.store,
        random: options.random,
        debug: options.debug ? message => options.log?.(`[utility] ${message}`) : undefined,
        planner: { log: options.log, replanMs: 1_000 }
    });
}
