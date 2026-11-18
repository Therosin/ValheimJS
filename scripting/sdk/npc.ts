import { context } from "./runtime";
import type { Inventory, ValheimBaseNPC } from "./api/generated";
import {
    canCraft as checkCraft,
    craft as runCraft,
    defineRecipe as registerRecipe,
    registerCondition as addCondition,
    type ConditionRef,
    type CraftCondition,
    type CraftConditionContext,
    type CraftConditionResult,
    type RecipeDefinition
} from "./crafting";
import type {
    ActionResult,
    CraftCheck,
    CraftResult,
    FoodSlot,
    InventoryItem,
    NpcAction,
    NpcAffordances,
    NpcDamageEvent,
    NpcEquipment,
    NpcFindQuery,
    NpcInteractOptions,
    NpcInteractionResult,
    NpcObject,
    NpcPerceptionOptions,
    NpcSenseFilter,
    NpcSensed,
    NpcStats,
    NpcView,
    StatusEffect
} from "./api/types";

export type {
    ActionResult,
    CraftCheck,
    CraftBlock,
    CraftResult,
    FoodSlot,
    InventoryItem,
    NpcAction,
    NpcAffordances,
    NpcDamageEvent,
    NpcEnvironment,
    NpcEquipment,
    NpcInteractOptions,
    NpcInteractionResult,
    NpcObject,
    NpcPerceptionOptions,
    NpcPos,
    NpcSenseFilter,
    NpcSensed,
    NpcStats,
    NpcView,
    StatusEffect
} from "./api/types";
export type {
    ConditionRef,
    CraftCondition,
    CraftConditionContext,
    CraftConditionResult,
    RecipeDefinition
} from "./crafting";

export interface NpcRegistration {
    /** Lowercase kind, `^[a-z0-9_-]{1,32}$`; also the prefab suffix. */
    kind: string;
    /** Body source: `"human"` (default) upgrades a Player clone to Humanoid; another prefab clones that Character. */
    body?: string;
    /** Host tick interval; default 500 ms, clamped to 100..5000. */
    tickMs?: number;
    /** Applied to the cloned prefab's Character faction, for example "Players" for companions. */
    faction?: string;
    /** Prefab names placed in the NPC inventory on first spawn; the first weapon found is equipped. */
    items?: string[];
    /**
     * Opt-in native auto-combat: when damaged by a non-player (or by a player while tamed), the
     * body fights back until the threat is gone. Default false: the script's intent wins.
     */
    autoEngage?: boolean;
    /** Open closed doors directly ahead while moving. Default true. */
    autoOpenDoors?: boolean;
    /** Per-kind perception tuning; defaults preserve the classic 20 m/8-entity scan. */
    perception?: NpcPerceptionOptions;
}

export interface NpcSpawnOptions {
    position: { x: number; y: number; z: number };
    name?: string;
}

/**
 * Native base constructor for plugin NPC subclasses. `npcBase()` needs an active plugin context,
 * so call it from `setup` through a factory rather than at module scope:
 *
 * ```ts
 * function makeWanderer() {
 *     const Base = npcBase();
 *     return class Wanderer extends Base {
 *         onTick(view: NpcView) { this.moveTo(5, view.y, 0, false); }
 *     };
 * }
 * ```
 */
export function npcBase(): new (handle: string) => ValheimBaseNPC {
    return context().host.api.npc.base();
}

/** Scripted NPCs: spawn, perception store, intents, memory and durable state. */
export const npc = {
    /** Enable host-side NPC tracing: lifecycle, new senses and settled intent failures. */
    debug(enabled: boolean): boolean {
        return context().host.api.npc.debug(enabled);
    },
    available(): boolean {
        return context().host.api.npc.available();
    },
    register(ctor: new (handle: string) => ValheimBaseNPC, options: NpcRegistration): void {
        context().host.api.npc.register(
            options.kind,
            options.body ?? "human",
            options.tickMs ?? 500,
            ctor as unknown as (...args: unknown[]) => unknown,
            options
        );
    },
    spawn(kind: string, options: NpcSpawnOptions): ValheimBaseNPC | null {
        const { x, y, z } = options.position;
        return context().host.api.npc.spawn(kind, x, y, z, options.name ?? null);
    },
    despawn(entity: ValheimBaseNPC): boolean {
        return context().host.api.npc.despawn(entity);
    },
    handles(kind: string): ValheimBaseNPC[] {
        return context().host.api.npc.handles(kind);
    },
    perception(entity: ValheimBaseNPC): NpcView | null {
        return context().host.api.npc.perception(entity);
    },
    /** Generic inventory accessor for one of this plugin's NPCs. */
    inventory(entity: ValheimBaseNPC): Inventory {
        return context().host.api.npc.inventory(entity);
    },
    /** Generic inventory accessor for a world container by ZDO handle. */
    container(handle: string): Inventory {
        return context().host.api.npc.container(handle);
    },
    /** Live body stats: health, stamina, food, status. */
    stats(entity: ValheimBaseNPC): NpcStats | null {
        return context().host.api.npc.stats(entity);
    },
    /** Current native action and its last outcome. */
    action(entity: ValheimBaseNPC): NpcAction | null {
        return context().host.api.npc.action(entity);
    },
    /** Requests a native pick action against a pickable ZDO. */
    pick(entity: ValheimBaseNPC, target: string): ActionResult {
        return context().host.api.npc.pick(entity, target);
    },
    /** Inspects what a target object supports for this NPC. */
    affordances(entity: ValheimBaseNPC, target: string): NpcAffordances {
        return context().host.api.npc.affordances(entity, target) as NpcAffordances;
    },
    /** Performs one supported interaction against a target object. */
    interact(entity: ValheimBaseNPC, target: string, options: NpcInteractOptions = {}): NpcInteractionResult {
        return context().host.api.npc.interact(entity, target, options) as NpcInteractionResult;
    },
    /** Starts a native attack against a live character in weapon range. */
    attack(entity: ValheimBaseNPC, target: string, secondary = false): ActionResult {
        return context().host.api.npc.attack(entity, target, secondary);
    },
    /** Jumps once. */
    jump(entity: ValheimBaseNPC): ActionResult {
        return context().host.api.npc.jump(entity);
    },
    /** Registers a recipe alias and its plugin conditions. */
    defineRecipe(id: string, definition: RecipeDefinition = {}): void {
        registerRecipe(id, definition);
    },
    /** Registers a pure condition predicate used by recipe definitions. */
    registerCondition(name: string, handler: CraftCondition): void {
        addCondition(name, handler);
    },
    /** Native recipe requirements plus registered plugin conditions. */
    canCraft(entity: ValheimBaseNPC, recipe: string): CraftCheck {
        return checkCraft(entity, recipe);
    },
    /** Craft one recipe: checks, consumes and produces into inventory or on the ground. */
    craft(entity: ValheimBaseNPC, recipe: string): CraftResult {
        return runCraft(entity, recipe);
    },
    /** Reads one core-owned per-NPC board value. */
    boardGet(entity: ValheimBaseNPC, key: string): unknown {
        return context().host.api.npc.boardGet(entity, key);
    },
    /** Snapshot of the core-owned per-NPC board. */
    boardSnapshot(entity: ValheimBaseNPC): Record<string, unknown> {
        return context().host.api.npc.boardSnapshot(entity) as Record<string, unknown>;
    },
    /** Subscribes to core NPC board changes; empty key observes all keys. */
    boardOnChange(
        entity: ValheimBaseNPC,
        key: string,
        handler: (key: string, value: unknown) => void
    ): number {
        return context().host.api.npc.boardOnChange(entity, key, handler as (...args: unknown[]) => unknown);
    },
    /** Removes a core NPC board subscription. */
    boardOff(id: number): boolean {
        return context().host.api.npc.boardOff(id);
    },
    /**
     * Queries the perception store. Entities stay recognized after leaving range; pass
     * `remembered: true` to include them with their last-known position.
     */
    sensed(entity: ValheimBaseNPC, filter: NpcSenseFilter = {}): NpcSensed[] {
        return context().host.api.npc.sensed(entity, filter) as NpcSensed[];
    },
    /** Persistent plugin-owned data for one NPC (survives restarts). */
    memory(entity: ValheimBaseNPC): Record<string, unknown> {
        return context().host.api.npc.memory(entity) as Record<string, unknown>;
    },
    setMemory(entity: ValheimBaseNPC, value: Record<string, unknown>): boolean {
        return context().host.api.npc.setMemory(entity, value);
    }
};
