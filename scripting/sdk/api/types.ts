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

/** `game.peerList()` — connected peers with routed id and identity. */
export interface PeerInfo {
    /** Routed peer id; use it for `Rpc` targets. */
    uid: number;
    /** Player name as published by the peer. */
    name: string;
    /** Platform/player id when available; kept as text to avoid 64-bit precision loss. */
    playerId: string;
    server: boolean;
    ready: boolean;
}

/** `world.charactersNear()` — live character snapshot, nearest first. */
export interface WorldCharacter {
    /** Stable ZDO id (`user:counter`) or null when the object has no ZDO. */
    id: string | null;
    prefab: string | null;
    name: string;
    x: number;
    y: number;
    z: number;
    distance: number;
    health: number;
    maxHealth: number;
    isPlayer: boolean;
    isDead: boolean;
    isTamed: boolean;
}

/** `world.containersNear()` — container snapshot, nearest first. */
export interface WorldContainer {
    /** Stable ZDO id (`user:counter`) or null when the object has no ZDO. */
    id: string | null;
    prefab: string | null;
    x: number;
    y: number;
    z: number;
    distance: number;
    /** Width x height inventory slots. */
    slots: number;
    /** Items currently stored; contents require the container inventory API. */
    items: number;
}
