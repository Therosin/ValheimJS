import type { NpcPos, NpcSensed, NpcView } from "../../../sdk/api/types";
import type { BehaviourActionContext, BehaviourOutcome } from "../../../sdk/planning";
import type { UtilityStats } from "../../../sdk/utility";
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

/** Aggregated cheap container facts near the villager's home. */
export interface HomeStock {
    containers: number;
    categories: Record<string, number>;
    amounts: Record<string, number>;
    prefabs: Record<string, number>;
}

/** The part of the villager runtime the activity definitions depend on. */
export interface VillagerContext {
    readonly id: string;
    readonly actor: VillagerActor;
    readonly needs: VillagerNeeds;
    readonly home: NpcPos;
    readonly homeStock: HomeStock;
    /** Persisted comfort/deficit state in -1..+1, keyed by category or item name. */
    readonly belongings: Readonly<Record<string, number>>;
    setBelongingTarget(key: string, target: number, source?: string): void;
    removeBelongingTarget(key: string): void;
    clearBelongingSource(source: string): void;
    readonly sheltered: boolean;
    readonly homesick: number;
    readonly board: VillagerBoard;
    readonly log?: (message: string) => void;
    readonly debug?: (message: string) => void;
    distanceFromHome(view: NpcView): number;
    hasFood(): boolean;
    hasFoodSource(view: NpcView): boolean;
    nearbyPlayer(view: NpcView): NpcSensed | null;
    unknownObject(view: NpcView): NpcSensed | null;
    threat(view: NpcView): NpcSensed | null;
    findShelter(view: NpcView): NpcSensed | null;
    beginActivity(activity: VillagerActivity): void;
    finishActivity(reason: "success" | "failure" | "preempted"): void;
    action(name: string, context: BehaviourActionContext): BehaviourOutcome;
    condition(name: string, context: BehaviourActionContext): boolean;
    satisfy(effects: Readonly<Record<string, number>>): void;
    publish(key: string, value: unknown): void;
}

export interface VillagerProfile {
    readonly stats: Partial<UtilityStats>;
    readonly needs: Partial<VillagerNeeds>;
}
