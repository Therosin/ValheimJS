/**
 * ValheimJS plugin SDK.
 *
 * The host evaluates each bundle as a plain script inside one shared Jint engine, so
 * imports are resolved at build time by Bun and the bundle only sets
 * globalThis.__vjs_plugin. Everything is synchronous by design — this runs on the
 * game's main thread.
 */

import type { Api } from "./api/index";
import { dispatchFrames, dispatchToHandlers } from "./networking";
import { adaptPlugin } from "./instance";
import { Messaging } from "./messaging";
import { createDatabase, type DatabaseApi } from "./database";
import type { Envelope } from "./messaging";

function safeDecodeEnvelope(raw: string): Envelope | null {
    try {
        const parsed = JSON.parse(raw) as Partial<Envelope>;
        return parsed && parsed.v === 1 && typeof parsed.channel === "string" ? (parsed as Envelope) : null;
    } catch {
        return null;
    }
}

export type { Api } from "./api/index";
export type { ChatPayload, DamagePayload, NetPayload, PlayerPayload, ServerTickPayload } from "./hooks";
export { Networking } from "./networking";
export { Messaging, defineWire, decodeEnvelope } from "./messaging";
export type { Envelope, EnvelopeHandler, EnvelopeKind, RequestOptions, WireSchema } from "./messaging";
export { decodeFrame, diffEntity, encodeFrame, quantise } from "./sync";
export type { SyncEntity } from "./sync";
export type { NetHandler, NetMessage } from "./networking";
export { defineCommands } from "./commands";
export type { CommandSpec, CommandResult } from "./commands";
export type { FsApi, PlayersApi, WorldApi, WorldSnapshot, ZdoApi, ChatApi } from "./api/index";

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

export type LogLevel = "info" | "warn" | "error";

export interface HostApi {
    name: string;
    dir: string;
    log(level: LogLevel, message: string): void;
    /** Unix epoch milliseconds; monotonic enough for scheduling. */
    now(): number;
    /** Raw value from this plugin's manifest.ini. */
    config(key: string): string | null;
    storage: {
        get(key: string): string | null;
        set(key: string, value: string): void;
    };
    info(): { name: string; version: string; api: string; dir: string };
    /** Game API installed by the C# binding modules (fs, world, ...). */
    api: Api;
}

export interface PluginPaths {
    /** server_plugins root. */
    root: string;
    /** server_plugins/plugins */
    plugins: string;
    /** This plugin's own folder (bundle + manifest). */
    plugin: string;
    /** This plugin's sandboxed data folder (fs.* is confined here). */
    data: string;
    /** This plugin's config folder; put config.ini here. */
    config: string;
    /** server_plugins/logs */
    logs: string;
    /** server_plugins/logs/<name>.log */
    logFile: string;
}

export interface PluginContext {
    name: string;
    dir: string;
    host: HostApi;
    /** This plugin's own document database (`<data>/db/<collection>/*.json`). */
    db: import("./database").Database;
    /** Where this plugin lives and what it may touch. */
    paths: PluginPaths;
    /** Per-plugin scratch state; lives as long as the plugin is loaded. */
    state: Record<string, unknown>;
    /** Public surface other plugins may call through `dep(name)`. */
    exports: Record<string, unknown>;
    /** Another loaded plugin's `exports`. Throws if it is not loaded. */
    dep<T extends Record<string, unknown> = Record<string, unknown>>(name: string): T;
}

export interface PluginShape {
    version?: string;
    exports?: Record<string, unknown>;
    onLoad?(ctx: PluginContext): void;
    onUnload?(ctx: PluginContext): void;
    /** Fired ~1 Hz by the server; keep it cheap — main thread. */
    onServerTick?(ctx: PluginContext, world: import("./hooks").ServerTickPayload): void;
    onChat?(ctx: PluginContext, payload: import("./hooks").ChatPayload): void;
    onPlayerJoin?(ctx: PluginContext, payload: import("./hooks").PlayerPayload): void;
    onPlayerLeave?(ctx: PluginContext, payload: import("./hooks").PlayerPayload): void;
    /** The character exists: `player.*` / `inventory.*` are usable from here on. */
    onPlayerSpawned?(ctx: PluginContext, payload: import("./hooks").PlayerPayload): void;
    onPlayerDespawned?(ctx: PluginContext, payload: import("./hooks").PlayerPayload): void;
    /** A hit was applied. Read-only: the damage already happened. */
    onDamage?(ctx: PluginContext, payload: import("./hooks").DamagePayload): void;
    /** A message arrived from the other process; dispatched by `Networking`. */
    onNet?(ctx: PluginContext, payload: import("./hooks").NetPayload): void;
    /** A binary frame arrived; dispatched by `Networking.handleFrame`. */
    onNetFrame?(ctx: PluginContext, payload: import("./hooks").NetPayload): void;
    /** Custom hooks can be dispatched by other plugins; keep the escape hatch typed as unknown. */
    [hook: string]: unknown;
}

/**
 * The host activates a context around each hook call, but promise continuations run later, when
 * no call is active. Every plugin is bundled separately, so remembering the last context seen by
 * *this* bundle is safe and keeps `log()` working across `await`.
 */
let pluginContext: PluginContext | null = null;

/** Context of this plugin (valid inside hooks, timers, and after `await`). */
export function context(): PluginContext {
    const active = typeof __vjs_active === "object" ? __vjs_active : null;
    const ctx = active ?? pluginContext;
    if (!ctx) {
        throw new Error("no plugin context is active — call this inside a hook or timer");
    }
    return ctx;
}

export function log(message: string): void {
    context().host.log("info", message);
}

export function warn(message: string): void {
    context().host.log("warn", message);
}

export function error(message: string): void {
    context().host.log("error", message);
}

/**
 * Accepts either a plugin object or a plugin **class**. A class is instantiated, given a local
 * event bus (`this.on/emit`), `ctx`/`state`/`host`, and its `setup(ctx)` is called on load.
 * Hooks declared as methods are forwarded; `exports` becomes the plugin's public surface.
 */
export function definePlugin<T extends PluginShape>(plugin: T): T;
export function definePlugin<T extends object>(pluginClass: new () => T): PluginShape;
export function definePlugin(target: unknown): PluginShape {
    if (typeof target === "function") {
        const instance = new (target as new () => object)();
        return decorate(adaptPlugin(instance));
    }

    return decorate(installBus(target as PluginShape));
}

/** Consume another plugin's exports. Runs now; dependencies are loaded before your setup. */
export function use<X extends Record<string, unknown> = Record<string, unknown>>(
    pluginName: string,
    consumer: (exports: X, ctx: PluginContext) => void
): void {
    consumer(context().dep<X>(pluginName), context());
}

function installBus(plugin: PluginShape): PluginShape {
    const handlers = new Map<string, Set<(...args: unknown[]) => void>>();

    (plugin as unknown as Record<string, unknown>).on = (event: string, handler: (...args: unknown[]) => void) => {
        let set = handlers.get(event);
        if (!set) {
            set = new Set();
            handlers.set(event, set);
        }
        set.add(handler);
        return handler;
    };
    (plugin as unknown as Record<string, unknown>).off = (event: string, handler: (...args: unknown[]) => void) => {
        handlers.get(event)?.delete(handler);
    };
    (plugin as unknown as Record<string, unknown>).emit = (event: string, ...args: unknown[]) => {
        handlers.get(event)?.forEach((handler) => handler(...args));
    };

    if (typeof plugin.onNetFrame !== "function") {
        plugin.onNetFrame = (ctx, payload) => {
            dispatchFrames(ctx, payload as unknown as { channel: string; sender: number; data: unknown });
        };
    }

    return plugin;
}

const NOT_HOOKS = new Set(["on", "off", "once", "emit", "exports", "use", "setup", "teardown"]);

/** Attaches `ctx.db` once per context; every plugin goes through `definePlugin`. */
function attachDatabase(ctx: PluginContext): void {
    if (!ctx.db) {
        ctx.db = createDatabase(ctx.host.api.db);
    }
}

function decorate(plugin: PluginShape): PluginShape {
    // Capture the context per hook so continuations after `await` still resolve it.
    const mutable = plugin as unknown as Record<string, unknown>;
    for (const key of Object.keys(mutable)) {
        const value = mutable[key];
        if (typeof value !== "function" || NOT_HOOKS.has(key)) {
            continue;
        }

        const handler = value as (ctx: PluginContext, payload: unknown) => unknown;
        mutable[key] = function (ctx: PluginContext, payload: unknown) {
            pluginContext = ctx;
            attachDatabase(ctx);
            return handler.call(this, ctx, payload);
        };
    }

    // Wire cross-process messages into `Networking` handlers without the author writing onNet.
    if (typeof plugin.onNet !== "function") {
        plugin.onNet = (ctx, payload) => {
            dispatchToHandlers(ctx, payload as unknown as { channel: string; sender: number; data: unknown });
            const raw = (payload as { data?: unknown }).data;
            if (typeof raw === "string") {
                const envelope = safeDecodeEnvelope(raw);
                if (envelope) {
                    Messaging.dispatch(envelope);
                }
            }
        };
    }

    if (typeof plugin.onNetFrame !== "function") {
        plugin.onNetFrame = (ctx, payload) => {
            dispatchFrames(ctx, payload as unknown as { channel: string; sender: number; data: unknown });
        };
    }

    return plugin;
}

export type Handler<P = unknown> = (ctx: PluginContext, payload: P) => void;

/** Runs the handler only where this process holds authority (dedicated server or host). */
export function serverOnly<P = unknown>(handler: Handler<P>): Handler<P> {
    return (ctx, payload) => {
        if (ctx.host.api.env.isServer()) {
            handler(ctx, payload);
        }
    };
}

/** Runs the handler only in a process with a local player/UI (any non-dedicated process). */
export function clientOnly<P = unknown>(handler: Handler<P>): Handler<P> {
    return (ctx, payload) => {
        if (!ctx.host.api.env.dedicated()) {
            handler(ctx, payload);
        }
    };
}

// setTimeout/setInterval/clearTimeout/clearInterval are host globals (declared above).
