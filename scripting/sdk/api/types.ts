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
    ready: boolean;
    /** True while the body is in water/swimming. */
    swimming: boolean;
    /** Native auto-combat state; engaged bodies report `paused`/`combat` for their intent. */
    combat: { engaged: boolean; threatId: string | null };
    self: NpcStats | null;
    action: NpcAction | null;
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

export type NpcActionStatus = "idle" | "running" | "paused" | "arrived" | "failed" | "cancelled";

export interface NpcAction {
    /** Monotonic per-NPC id; increments on every issued intent. */
    seq: number;
    kind: "idle" | "stop" | "moveTo" | "wait" | "lookAt" | "face" | "pick" | string;
    status: NpcActionStatus;
    reason: string | null;
    targetId: string | null;
    target: NpcPos | null;
    /** 0..1 for a running moveTo, 1 once it arrives; null for other actions. */
    progress: number | null;
}

export interface ActionResult {
    /** True only when the full request succeeded. */
    ok: boolean;
    /** `partial` when only part of the request could be applied; `count` is then the applied amount. */
    reason: string | null;
    /** Number of items affected; for `partial` results this is less than requested. */
    count?: number;
}

export interface FoodSlot {
    prefab: number;
    name: string;
    remaining: number;
    total: number;
    health: number;
    stamina: number;
    eitr: number;
    regen: number;
}

export interface StatusEffect {
    name: string;
    remaining: number;
    duration: number;
}

export interface NpcStats {
    health: number;
    maxHealth: number;
    healthRatio: number;
    stamina: number;
    maxStamina: number;
    staminaRatio: number;
    eitr: number;
    maxEitr: number;
    eitrRatio: number;
    food: FoodSlot[];
    statusEffects: StatusEffect[];
    /** No active food slot; an engine fact, not a hunger policy. */
    hungry: boolean;
}

export interface InventoryItem {
    slot: { x: number; y: number };
    prefab: string;
    name: string;
    stack: number;
    quality: number;
    durability: number;
    maxDurability: number;
    /** Categorical wear state; tools only. */
    condition: "none" | "high" | "mid" | "low" | "broken";
    /** Native tool capabilities, normalized for policy. */
    chop: number;
    pickaxe: number;
    toolTier: number;
    weight: number;
    type: "food" | "mead" | "weapon" | "tool" | "armor" | "ammo" | "material" | "misc";
    equipped: boolean;
    food: { health: number; stamina: number; eitr: number; burnTime: number; regen: number } | null;
}

export interface NpcEquipment {
    right: InventoryItem | null;
    left: InventoryItem | null;
    helmet: InventoryItem | null;
    chest: InventoryItem | null;
    legs: InventoryItem | null;
    shoulder: InventoryItem | null;
    utility: InventoryItem | null;
    trinket: InventoryItem | null;
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
    /** Pickables: harvestable. Characters: alive. Other objects: true. */
    ready: boolean;
    /** Current native noise radius emitted by a character; 0 for objects. */
    noise: number;
    /** Native hearing rule: within the NPC hear range and within the target's noise radius. */
    audible: boolean;
    /** Pickables: the yielded item is food. */
    food: boolean;
    enemy: boolean;
    isPlayer: boolean;
    isNpc: boolean;
    isTamed: boolean;
    isDead: boolean;
    level: number;
    isObject: boolean;
    /** Minimum tool tier for harvestable world objects; 0 when not applicable. */
    toolTier?: number;
    /** Stable lower-case tags projected from prefab/item data (`wood`, `food`, `container`...). */
    tags?: string[];
    /** Verbs this target supports for a scripted body. */
    verbs?: string[];
    /** Capabilities required from the actor (`chop`, `pickaxe`, `player`). */
    requires?: string[];
    /** Prefab/item names this target can yield. */
    gives?: string[];
    /** True when the owning NPC currently satisfies `requires`. */
    usable?: boolean;
    health: number;
    maxHealth: number;
    /** Seconds since first contact (in range). */
    firstSeen: number;
    /** Seconds since the entry was last in range; -1 when it has never been in range since first contact. */
    lastSeen: number;
    seenCount: number;
}

/** World objects use the same record shape as entities; `kind` carries the object class. */
export type NpcObject = NpcSensed;

/** Edge event from the native damage callback on this NPC's body. */
export interface NpcDamageEvent {
    attackerId: string;
    attackerName: string;
    faction: string;
    pos: NpcPos;
    direction: { x: number; z: number };
    damage: number;
    health: number;
    maxHealth: number;
    isPlayer: boolean;
    isNpc: boolean;
    isTamed: boolean;
}

/** What one target object supports for a scripted body. */
export interface NpcAffordances {
    id: string;
    kind: string;
    name: string;
    hoverName: string | null;
    hoverText: string | null;
    supported: boolean;
    /** The interaction needs a player/UI; the NPC cannot perform it. */
    playerOnly: boolean;
    reason: string | null;
    verbs: string[];
    /** Expected item prefab names when this target is harvested or taken. */
    gives: string[];
    /** Tool/damage capabilities needed to use the target. */
    requires: string[];
    /** Minimum tool tier for harvestable targets. */
    toolTier: number;
    distance: number;
    inRange: boolean;
    /** Native stations with queued output, e.g. cooking slots ready to take. */
    readyCount?: number;
    /** Generic station state: idle, cooking, done, full. */
    state?: string | null;
}

export interface NpcInteractionResult {
    ok: boolean;
    reason: string | null;
    verb: string;
    count: number;
}

/** One reason a recipe cannot be crafted right now. */
export interface CraftBlock {
    code: string;
    item?: string;
    need?: number;
    have?: number;
    prefab?: string;
    requiredLevel?: number;
    haveLevel?: number;
}

export interface CraftMissingItem {
    item: string;
    /** Shortfall to source. */
    amount: number;
}

export interface CraftMissingStation {
    prefab: string;
    requiredLevel: number;
}

export interface CraftMissing {
    ingredients: CraftMissingItem[];
    station: CraftMissingStation | null;
}

export interface CraftCheck {
    ok: boolean;
    /** Sourceable shortfalls and station requirement. */
    missing: CraftMissing;
    /** Why the recipe is currently blocked. */
    blockedBy: CraftBlock[];
}

export interface CraftResult extends CraftCheck {
    count: number;
}

export interface NpcInteractOptions {
    verb?: "pick" | "use" | "open" | "close" | "toggle" | "store" | "take" | "addOre" | "addFuel" | string;
    prefab?: string;
    name?: string;
    slot?: { x: number; y: number };
    count?: number;
    repeat?: boolean;
    alt?: boolean;
}

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
    audible?: boolean;
    enemy?: boolean;
    players?: boolean;
    /** Include entries remembered after leaving range. */
    remembered?: boolean;
    max?: number;
}

/** Declarative query accepted by `npc.findObject` / `npc.findObjects`. */
export interface NpcFindQuery {
    kind?: string;
    prefab?: string;
    name?: string;
    /** Array means any-of; `{ anyOf }`/`{ allOf }` narrow explicitly. */
    tags?: string[] | { anyOf?: string[]; allOf?: string[] };
    verbs?: string[] | { anyOf?: string[]; allOf?: string[] };
    requires?: string[] | { anyOf?: string[]; allOf?: string[] };
    where?: {
        kind?: string;
        prefab?: string;
        name?: string;
        tags?: string[] | { anyOf?: string[]; allOf?: string[] };
        verbs?: string[] | { anyOf?: string[]; allOf?: string[] };
        requires?: string[] | { anyOf?: string[]; allOf?: string[] };
        ready?: boolean;
        usable?: boolean;
        visible?: boolean;
        inRange?: boolean;
        enemy?: boolean;
        isPlayer?: boolean;
        isNpc?: boolean;
        isTamed?: boolean;
        isDead?: boolean;
        isObject?: boolean;
        food?: boolean;
        distance?: number | { lte?: number; gte?: number; eq?: number };
    };
    orderBy?: "distance" | "health" | "level" | "seenCount";
    take?: number;
    max?: number;
    skip?: number;
    radius?: number;
    includeRemembered?: boolean;
    exclude?: string[];
}

export interface NpcPerceptionOptions {
    radius?: number;
    /** Hearing radius; 0 uses `radius`. */
    hearing?: number;
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

/** Options for `ui.define`. */
export interface UiDefineOptions {
    /** Name used in compile errors. */
    source?: string;
    /** YAML text keyed by the alias declared in `imports:`. */
    imports?: Record<string, string>;
}

/** Options for `ui.open`. */
export interface UiOpenOptions {
    /** Which host canvas receives the panel; default "window". */
    mount?: "overlay" | "window" | "popup";
    /** Block game input while this panel is open. */
    modal?: boolean;
    /** Resolves `{ bind: "key" }` values. */
    get?: (key: string) => unknown;
    /** Writes values from inputs/controls back to plugin state. */
    set?: (key: string, value: unknown) => void;
    /** Receives button/input/window events with a scoped node path. */
    onEvent?: (path: string, event: string, value?: unknown) => void;
}
