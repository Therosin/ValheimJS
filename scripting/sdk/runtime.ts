import type { Api } from "./api/generated";

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

export type LogLevel = "info" | "warn" | "error";

export interface PluginPaths {
    /** Plugin root (`ValheimJS/`). */
    root: string;
    /** Root `plugins/` folder. */
    plugins: string;
    /** This plugin's own folder (bundle + manifest). */
    plugin: string;
    /** This plugin's sandboxed data folder (`fs` is confined here). */
    data: string;
    /** This plugin's config folder; put config.ini here. */
    config: string;
    /** Root `logs/` folder. */
    logs: string;
    /** `logs/<name>.log`. */
    logFile: string;
}

export interface HostApiSurface {
    name: string;
    dir: string;
    paths: PluginPaths;
    log(level: LogLevel, message: string): void;
    /** Unix epoch milliseconds. */
    now(): number;
    /** Value from this plugin's config.ini, else its manifest; null when unset. */
    config(key: string): string | null;
    storage: {
        get(key: string): string | null;
        set(key: string, value: string): void;
    };
    info(): { name: string; version: string; api: string; dir: string };
    /** Game API installed by the C# binding classes. */
    api: Api;
}

export interface PluginContext {
    name: string;
    dir: string;
    paths: PluginPaths;
    host: HostApiSurface;
    /** Per-plugin scratch state; lives as long as the plugin is loaded. */
    state: Record<string, unknown>;
    /** Public surface other plugins may call through `dep(name)`. */
    exports: Record<string, unknown>;
    /** Another loaded plugin's `exports`. Throws if it is not loaded. */
    dep<T extends Record<string, unknown> = Record<string, unknown>>(name: string): T;
}

type ContextGlobal = { __vjs_context?: PluginContext | null };

/**
 * Each plugin bundle contains its own copy of this module and is evaluated under its own
 * context, so this is the plugin that owns the SDK, not whoever happens to be on the JS stack.
 * That is what keeps an exported function's `database()`/`fs()`/`host.api` in the provider's
 * scope when another plugin calls it.
 */
const owner = (globalThis as ContextGlobal).__vjs_context ?? null;

/**
 * The context that owns this bundle. During entry evaluation it is the evaluating plugin; host
 * hooks, timers and `await` continuations restore the same context. Unit tests that install a
 * context after importing the SDK fall back to the live global.
 */
export function context(): PluginContext {
    const active = owner ?? (globalThis as ContextGlobal).__vjs_context;
    if (!active) {
        throw new Error("no plugin context is active — call this during entry evaluation, setup, a hook, or a host callback");
    }
    return active;
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

/** Consume another plugin's exports. Dependencies are loaded first. */
export function use<X extends Record<string, unknown> = Record<string, unknown>>(
    pluginName: string,
    consumer: (exports: X, ctx: PluginContext) => void
): void {
    const active = context();
    consumer(active.dep<X>(pluginName), active);
}

export type Handler<P = unknown, R = void> = (ctx: PluginContext, payload: P) => R;

/** Execute immediately on a dedicated-server process; no deferred work. */
export function serverOnly<T>(callback: (ctx: PluginContext) => T): T | undefined {
    const ctx = context();
    if (ctx.host.api.game.side() === "server") return callback(ctx);
}

/** Execute immediately on a client process, including a player host. */
export function clientOnly<T>(callback: (ctx: PluginContext) => T): T | undefined {
    const ctx = context();
    if (ctx.host.api.game.side() === "client") return callback(ctx);
}

/** Execute only with confirmed live world authority; skips before a session exists. */
export function authorityOnly<T>(callback: (ctx: PluginContext) => T): T | undefined {
    const ctx = context();
    if (ctx.host.api.game.isServer()) return callback(ctx);
}

/** Wrap an object-form handler for the dedicated-server process. */
export function serverHandler<P = unknown, R = void>(handler: Handler<P, R>): Handler<P, R | undefined> {
    return (ctx, payload) => {
        if (ctx.host.api.game.side() === "server") {
            return handler(ctx, payload);
        }
    };
}

/** Wrap an object-form handler for a client process, including a player host. */
export function clientHandler<P = unknown, R = void>(handler: Handler<P, R>): Handler<P, R | undefined> {
    return (ctx, payload) => {
        if (ctx.host.api.game.side() === "client") {
            return handler(ctx, payload);
        }
    };
}

/** Recheck live world authority on every event invocation. */
export function authorityHandler<P = unknown, R = void>(handler: Handler<P, R>): Handler<P, R | undefined> {
    return (ctx, payload) => {
        if (ctx.host.api.game.isServer()) return handler(ctx, payload);
    };
}
