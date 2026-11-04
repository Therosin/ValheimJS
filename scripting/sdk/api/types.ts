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
/** Partial transient state carried by the native binary sync path. */
export interface SyncEntity {
    id: number;
    x?: number;
    y?: number;
    z?: number;
    rotY?: number;
    flags?: number;
}
export interface ChatCommandDefinition {
    name: string;
    namespace?: string | null;
    description?: string;
    /** Static help or synchronous contextual help, requested with !help command [query]. */
    help?: string | ((query: string) => string);
    /** Explicit routes without !, for example "hello" or "tools:hello". */
    aliases?: string[];
    /** Requires any listed role, before validation and again before execution. */
    roles?: string[];
    /** Requires every listed permission. Explicit account denials take precedence. */
    permissions?: string[];
}

/** Host-owned account snapshot. Mutating this object never changes the stored account. */
export interface Account {
    id: string;
    platformId: string;
    knownName: string;
    createdAt: number;
    lastSeenAt: number;
    profile: { displayName: string; locale: string };
}
