import type { GameSnapshot } from "./api/types";

export interface ChatPayload {
    /** "normal" | "shout" | "whisper" | "ping" */
    channel: string;
    senderId: number;
    text: string;
}

export interface PlayerPayload {
    uid: number;
    name: string;
}

export interface DamagePayload {
    /** Display name of whoever took the hit. */
    target: string;
    targetIsPlayer: boolean;
    /** Display name of the attacker; empty for environmental damage. */
    attacker: string;
    attackerIsPlayer: boolean;
    /** Damage applied by this hit (after resistances). */
    damage: number;
    targetHealth: number;
    targetMaxHealth: number;
    /** "Slash" | "Pierce" | "Blunt" | "Fire" | … */
    hitType: string;
}

export type ServerTickPayload = GameSnapshot;
