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

/** `npc.perception(entity)` and the object passed to NPC hooks. */
export interface NpcView {
    id: string;
    kind: string;
    x: number;
    y: number;
    z: number;
    health: number;
    maxHealth: number;
    isAlive: boolean;
    faction: string;
    isTamed: boolean;
    level: number;
    home: NpcPos;
    intent: NpcIntent | null;
    environment: NpcEnvironment | null;
    nearby: NpcSensed[];
    remembered: NpcSensed[];
    objects: NpcSensed[];
}

export interface NpcPos {
    x: number;
    y: number;
    z: number;
}

export interface NpcIntent {
    kind: "idle" | "stop" | "moveTo" | "wait" | "lookAt" | "face" | string;
    status: "idle" | "running" | "arrived" | "failed";
    reason: string | null;
    targetId: string | null;
    target: NpcPos | null;
}

export interface NpcEnvironment {
    day: number;
    isDay: boolean;
    biome: string;
    weather: string | null;
}

/** One recognized entity in the NPC's perception store. */
export interface NpcSensed {
    id: string;
    name: string;
    /** ValheimJS NPC kind when sensed.isNpc, otherwise empty. */
    kind: string;
    prefab: string | null;
    faction: string;
    pos: NpcPos;
    rel: NpcPos;
    distance: number;
    bearing: number;
    visible: boolean;
    inRange: boolean;
    enemy: boolean;
    isPlayer: boolean;
    isNpc: boolean;
    isTamed: boolean;
    isDead: boolean;
    level: number;
    isObject: boolean;
    health: number;
    maxHealth: number;
    /** Seconds since the entity first entered the store. */
    firstSeen: number;
    /** Seconds since last visible; -1 when never seen inside the view cone. */
    lastSeen: number;
    seenCount: number;
}

/** World objects use the same record shape as entities; `kind` carries the object class. */
export type NpcObject = NpcSensed;

/** Filter accepted by `npc.sensed`. `dir` defaults to the NPC's facing. */
export interface NpcSenseFilter {
    id?: string;
    radius?: number;
    dir?: { x: number; z: number };
    /** Half-angle in degrees around `dir`; default 180 (all directions). */
    angle?: number;
    /** Tokens: player, npc, creature, tamed, enemy, dead, or an exact kind/prefab. */
    kinds?: string[];
    visible?: boolean;
    enemy?: boolean;
    players?: boolean;
    /** Include entries remembered after leaving range. */
    remembered?: boolean;
    max?: number;
}

export interface NpcPerceptionOptions {
    radius?: number;
    max?: number;
    viewRange?: number;
    /** Half-angle in degrees; 90 is the game's front hemisphere. */
    viewAngle?: number;
    lineOfSight?: boolean;
    includePlayers?: boolean;
    includeCreatures?: boolean;
    includeTamed?: boolean;
    includeNpcs?: boolean;
    includeDead?: boolean;
    /** Enables the world-object scan; 0 disables it. */
    objectRadius?: number;
    objectMax?: number;
}

/** `ai.tick()` status. */
export type AiStatus = "success" | "failure" | "running" | "cancelled";

/** Result of one `ai.tick()` call. */
export interface AiTickResult {
    status: AiStatus;
    /** Value the action returned as `output`; never written to state unless the node declares `out`. */
    output: unknown;
}

/** Value an AI action may return; `undefined`/`null` counts as success. */
export type AiActionOutcome =
    | "success"
    | "failure"
    | "running"
    | { status?: "success" | "failure" | "running"; output?: unknown };

/**
 * Callback context. `args` and `state` are copies: mutating `state` writes back to the instance
 * after the callback returns; `args` is read-only in effect.
 */
export interface AiContext {
    args: Record<string, unknown>;
    state: Record<string, unknown>;
    /** The `this` value supplied to `ai.instantiate`. */
    this: unknown;
    deltaTime: number;
    /** True for `execAfter` runs caused by cancel, reset or a jump. */
    interrupted: boolean;
}

export interface AiLoadOptions {
    /** Source name in parse errors; imports are never read from disk by the JS bridge. */
    source?: string;
    /** YAML text keyed by the alias declared in the tree's `imports:` block. */
    imports?: Record<string, string>;
    /** Receives state-merge warnings at compile time. */
    warn?: (message: string) => void;
}

export interface AiInstantiateOptions {
    /** Exposed as `ctx.this` and as the callback's JS `this`. */
    this?: unknown;
    /** Overrides/additions applied after the tree's declared state defaults. */
    state?: Record<string, unknown>;
    /** Runs `execBefore`/`exec`/`execAfter` snippets; the host has no JS interpreter of its own. */
    exec?: (code: string, ctx: AiContext) => unknown;
    action?: (name: string, ctx: AiContext) => AiActionOutcome;
    condition?: (name: string, ctx: AiContext) => boolean;
    /** Returns [0, 1); defaults to the host's generator. */
    random?: () => number;
    maxJumpsPerTick?: number;
}
