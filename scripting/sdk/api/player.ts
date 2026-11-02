export interface PlayerStats {
    name: string;
    health: number;
    maxHealth: number;
    stamina: number;
    maxStamina: number;
    eitr: number;
    maxEitr: number;
    position: { x: number; y: number; z: number };
}

/**
 * Authoritative player state. Server-side only in practice: `uid` comes from `players.list()`,
 * and calls are no-ops while the player has not spawned yet.
 */
export interface PlayerApi {
    /** null when that uid has no spawned player right now. */
    stats(uid: number): PlayerStats | null;
    exists(uid: number): boolean;
    setHealth(uid: number, value: number): void;
    setGodMode(uid: number, enabled: boolean): void;
    teleport(uid: number, x: number, y: number, z: number): void;
    /** Skill names match the game's (`Swords`, `Run`, `Woodcutting`, …). */
    raiseSkill(uid: number, skill: string, factor?: number): void;
}
