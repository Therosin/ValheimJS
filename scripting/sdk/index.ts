/**
 * ValheimJS plugin SDK.
 *
 * A bundle is a plain script evaluated by the host inside one shared Jint engine. It only has
 * to export a descriptor; `definePlugin(...)` produces one. Everything after that — context,
 * class instantiation, hook discovery, timers, dependency exports and teardown — belongs to the
 * host, not to this file.
 */

import type { PluginContext } from "./runtime";
import type { ChatPayload, DamagePayload, PlayerPayload, ServerTickPayload } from "./hooks";

export type { Api, ValheimBaseNPC, ValheimPlayer } from "./api/generated";
export type { GameSnapshot, KeyState, RpcMeta, ServerTickSnapshot } from "./api/types";
export type {
    ChatPayload,
    DamagePayload,
    PlayerPayload,
    ServerTickPayload
} from "./hooks";
export type { Collection, Db as Database, Doc } from "./database";
export { database, Db } from "./database";
export { input, type KeyboardHandler, type KeyboardPhase } from "./input";
export { Rpc, RpcChannel } from "./net";
export { Sync } from "./sync";
export { signals, type Signals, type SignalsOptions } from "./signals";
export {
    npc,
    npcBase,
    type ActionResult,
    type FoodSlot,
    type InventoryItem,
    type NpcAction,
    type NpcAffordances,
    type NpcDamageEvent,
    type NpcEnvironment,
    type NpcEquipment,
    type NpcInteractOptions,
    type NpcInteractionResult,
    type NpcObject,
    type NpcPerceptionOptions,
    type NpcPos,
    type NpcRegistration,
    type NpcSenseFilter,
    type NpcSensed,
    type NpcSpawnOptions,
    type NpcStats,
    type NpcView,
    type StatusEffect
} from "./npc";
export { ai, type AiActionOutcome, type AiContext, type AiInstantiateOptions, type AiLoadOptions, type AiStatus, type AiTickResult, type BlackboardChangeHandler, type BlackboardSignalHandler, type BoardMiddlewareHandler, type BoardOperationContext, BoardMembership, BoardScope, BoardSubscription } from "./ai";
export {
    Plan,
    Planner,
    registerAction,
    ActionRegistry,
    actions,
    type ActionContext,
    type ActionHandler,
    type ActionOutcome,
    type ActionResultLike,
    type Goal,
    type GoalContext,
    type GoalExitReason,
    type GoalStatus,
    type InteractStep,
    type InventoryActions,
    type PlanOptions,
    type PlanStatus,
    type PlannerActor,
    type PlannerOptions,
    type RetryOptions,
    type Step,
    type StepOutcome,
    type Vec3Like
} from "./planning";
export type { CraftBlock, CraftCheck, CraftResult } from "./api/types";
export type {
    ConditionRef,
    CraftCondition,
    CraftConditionContext,
    CraftConditionResult,
    RecipeDefinition
} from "./crafting";
export { ui, type UiDefineOptions, type UiOpenOptions } from "./ui";
export { draw, type DrawOptions, type DrawPoint } from "./draw";
export { all, any, cooldown, delay, never, not, once, predicates, stable, type Predicate } from "./predicates";
export { ChatCommand, ChatCommands, type ChatCommandContext, type ChatCommandUser } from "./commands";
export { AccountService, PermissionService } from "./accounts";
export type { Account } from "./api/types";
export type { ChatCommandDefinition } from "./api/types";
export type { SyncEntity } from "./api/types";
export { fs, game, players, world } from "./access";
export { assets, importText, importYaml, importToml } from "./assets";
export type {
    Handler,
    HostApiSurface,
    Json,
    LogLevel,
    PluginContext,
    PluginPaths
} from "./runtime";
export { authorityOnly, authorityHandler, clientOnly, clientHandler, context, error, log, serverOnly, serverHandler, use, warn } from "./runtime";

/** Hook payloads, keyed by the hook names the host dispatches. */
export interface PluginHooks {
    onLoad?(ctx: PluginContext): void;
    onUnload?(ctx: PluginContext): void;
    onServerTick?(ctx: PluginContext, world: ServerTickPayload): void;
    onChat?(ctx: PluginContext, payload: ChatPayload): void;
    onPlayerJoin?(ctx: PluginContext, payload: PlayerPayload): void;
    onPlayerLeave?(ctx: PluginContext, payload: PlayerPayload): void;
    onPlayerSpawned?(ctx: PluginContext, payload: PlayerPayload): void;
    onPlayerDespawned?(ctx: PluginContext, payload: PlayerPayload): void;
    onPlayerReady?(ctx: PluginContext, payload: PlayerPayload): void;
    onPlayerUnready?(ctx: PluginContext, payload: PlayerPayload): void;
    onDamage?(ctx: PluginContext, payload: DamagePayload): void;
}

/**
 * Object-form plugin. Any function property that is not lifecycle is a hook and is called as
 * `(ctx, payload)`.
 */
export interface ObjectPlugin extends PluginHooks {
    setup?(ctx: PluginContext): void;
    teardown?(ctx: PluginContext): void;
    version?: string;
    description?: string;
    exports?: Record<string, unknown>;
    [hook: string]: unknown;
}

/**
 * Class-form plugin base. The host constructs the class, attaches `ctx`, discovers methods as
 * hooks and calls `setup`. Hook methods are invoked as `(payload)` with `this` bound to the
 * instance.
 */
export abstract class JsPlugin {
    /** Attached by the host before `setup` runs. */
    ctx!: PluginContext;

    version = "0.0.0";

    /** What other plugins may reach through `ctx.dep(name)`. */
    exports: Record<string, unknown> = {};

    /** Runs once, after dependencies loaded. */
    setup(): void {}

    /** Runs before the plugin is dropped (reload or shutdown). Best effort. */
    teardown(): void {}
}

/** Descriptor the bundle assigns to `globalThis.__vjs_plugin`. */
export type PluginExport =
    | { readonly __vjsPlugin: "class"; readonly ctor: new () => object }
    | { readonly __vjsPlugin: "object"; readonly value: Record<string, unknown> };

export function definePlugin<T extends object>(pluginClass: new () => T): PluginExport;
export function definePlugin<T extends ObjectPlugin>(plugin: T): PluginExport;
export function definePlugin(target: unknown): PluginExport {
    if (typeof target === "function") {
        return { __vjsPlugin: "class", ctor: target as new () => object };
    }

    if (target !== null && typeof target === "object") {
        return { __vjsPlugin: "object", value: target as Record<string, unknown> };
    }

    throw new Error("definePlugin needs a class or a plain object");
}
