/**
 * Hand-written shapes for structured values that cross the bridge as JSON. The generated API
 * declarations refer to these names; the C# side supplies the matching JSON.
 */

/** `game.snapshot()` — read live; nulls/zeros are normal during startup. */
export interface GameSnapshot {
    world: string | null;
    peers: number;
    day: number;
    dayFraction: number;
    timeOfDay: "day" | "night" | "unknown";
}

/** `input.state()` / `input.rawState()`. */
export interface KeyState {
    isPressed: boolean;
    wasPressedThisFrame: boolean;
    wasReleasedThisFrame: boolean;
}

/** Second argument passed to RPC event handlers. */
export interface RpcMeta {
    /** Routed peer id; not proof of authorisation. */
    sender: number;
    channel: string;
    event: string;
}

/** Alias kept for hook payload naming. */
export type ServerTickSnapshot = GameSnapshot;
