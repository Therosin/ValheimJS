import { context } from "./runtime";
import type { ValheimBaseNPC } from "./api/generated";
import type {
    NpcIntent,
    NpcObject,
    NpcPerceptionOptions,
    NpcSenseFilter,
    NpcSensed,
    NpcView
} from "./api/types";

export type {
    NpcEnvironment,
    NpcIntent,
    NpcObject,
    NpcPerceptionOptions,
    NpcPos,
    NpcSenseFilter,
    NpcSensed,
    NpcView
} from "./api/types";

export interface NpcRegistration {
    /** Lowercase kind, `^[a-z0-9_-]{1,32}$`; also the prefab suffix. */
    kind: string;
    /** Existing vanilla prefab cloned on registration (for example `Dverger`). */
    prefab: string;
    /** Host tick interval; default 500 ms, clamped to 100..5000. */
    tickMs?: number;
    /** Applied to the cloned prefab's Character faction, for example "Players" for companions. */
    faction?: string;
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
    available(): boolean {
        return context().host.api.npc.available();
    },
    register(ctor: new (handle: string) => ValheimBaseNPC, options: NpcRegistration): void {
        context().host.api.npc.register(
            options.kind,
            options.prefab,
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
