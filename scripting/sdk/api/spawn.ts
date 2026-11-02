/**
 * Object spawning over prefabs that already exist in the game.
 *
 * The object is registered with the game's own networking, so clients see it, it saves with the
 * world, and it can be destroyed again. Handles are session-scoped: persist your own ids if you
 * need to find the object after a restart.
 */
export interface SpawnApi {
    /** Returns a handle, or null when the prefab is unknown / not networked. */
    create(prefab: string, x: number, y: number, z: number): string | null;
    destroy(handle: string): void;
    /** Data stored on the object's own ZDO: replicated by the game and saved with the world. */
    get(handle: string, key: string): string | null;
    set(handle: string, key: string, value: string): void;
    alive(handle: string): boolean;
    /** Prefab names matching a substring (limit 0 = all). */
    prefabs(query?: string, limit?: number): string[];
    exists(prefab: string): boolean;
}
