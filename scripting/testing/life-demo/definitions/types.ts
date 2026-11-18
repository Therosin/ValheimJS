import type { NpcPos, NpcSensed, NpcView } from "../../../sdk/api/types";
import type { UtilityStats } from "../../../sdk/utility";
import type { BehaviorOutcome } from "../lib/behaviour";
import type { VillagerActor, VillagerBoard, VillagerNeeds } from "../lib/actions";

export type VillagerActivity =
    | "wander"
    | "inspect"
    | "socialize"
    | "forage"
    | "explore"
    | "seekHome"
    | "return"
    | "rest"
    | "flee"
    | "eat";

/** The part of the villager runtime the activity definitions depend on. */
export interface VillagerContext {
    readonly id: string;
    readonly actor: VillagerActor;
    readonly needs: VillagerNeeds;
    readonly home: NpcPos;
    readonly sheltered: boolean;
    readonly homesick: number;
    readonly board: VillagerBoard;
    readonly log?: (message: string) => void;
    distanceFromHome(view: NpcView): number;
    hasFood(): boolean;
    hasFoodSource(view: NpcView): boolean;
    nearbyPlayer(view: NpcView): NpcSensed | null;
    unknownObject(view: NpcView): NpcSensed | null;
    threat(view: NpcView): NpcSensed | null;
    findShelter(view: NpcView): NpcSensed | null;
    startActivity(activity: VillagerActivity, treeId: string, extraState?: Record<string, unknown>): void;
    tickActivity(deltaTime?: number): BehaviorOutcome | "cancelled";
    endActivity(reason: "success" | "failure" | "preempted"): void;
    satisfy(effects: Readonly<Record<string, number>>): void;
    publish(key: string, value: unknown): void;
}

export interface VillagerProfile {
    readonly stats: Partial<UtilityStats>;
    readonly needs: Partial<VillagerNeeds>;
}
