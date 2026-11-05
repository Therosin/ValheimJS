import { context } from "./runtime";
import type { ValheimBaseNPC } from "./api/generated";
import type { NpcView } from "./api/types";

export type { NpcView, NpcNearby } from "./api/types";

export interface NpcRegistration {
    /** Lowercase kind, `^[a-z0-9_-]{1,32}$`; also the prefab suffix. */
    kind: string;
    /** Existing vanilla prefab cloned on registration (for example `Dverger`). */
    prefab: string;
    /** Host tick interval; default 500 ms, clamped to 100..5000. */
    tickMs?: number;
}

export interface NpcSpawnOptions {
    position: { x: number; y: number; z: number };
    name?: string;
}

/**
 * Native base constructor for plugin NPC subclasses. Call once during module evaluation or
 * `setup` (a plugin context must be active):
 *
 * ```ts
 * class Wanderer extends npcBase() { onTick(view: NpcView) { this.moveTo(5, view.y, 0, false); } }
 * ```
 */
export function npcBase(): new (handle: string) => ValheimBaseNPC {
    return context().host.api.npc.base();
}

/** Scripted NPCs: spawn, perception, intents and durable state. */
export const npc = {
    available(): boolean {
        return context().host.api.npc.available();
    },
    register(ctor: new (handle: string) => ValheimBaseNPC, options: NpcRegistration): void {
        context().host.api.npc.register(
            options.kind,
            options.prefab,
            options.tickMs ?? 500,
            ctor as unknown as (...args: unknown[]) => unknown
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
    }
};
