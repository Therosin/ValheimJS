import type { PluginContext, PluginShape } from "./index";

/**
 * Class-style plugins.
 *
 * ```ts
 * export default class Greeter {
 *     description = "welcomes players";
 *     exports = { greet: (name: string) => `hi ${name}` };
 *
 *     setup(ctx: PluginContext) {
 *         this.on("tick", () => {});
 *     }
 *
 *     onPlayerJoin(ctx: PluginContext, player: { name: string }) {
 *         ctx.host.api.chat.send(`Welcome ${player.name}`);
 *     }
 * }
 * ```
 *
 * The instance gets `ctx`, `state`, `exports`, `host` and a small local event bus
 * (`on`/`once`/`off`/`emit`) before `setup` runs. Lifecycle methods that exist as hooks
 * (`onServerTick`, `onChat`, `onPlayerJoin`, `onPlayerSpawned`, `onDamage`, `onNet`, …) are
 * forwarded to the host with the instance as `this`.
 */

export type PluginConstructor<T> = new () => T;
export type AnyRecord = Record<string, unknown>;

interface Bus {
    handlers: Map<string, Set<(...args: unknown[]) => void>>;
    once: Set<(...args: unknown[]) => void>;
}

const buses = new WeakMap<object, Bus>();

function busOf(target: object): Bus {
    let bus = buses.get(target);
    if (!bus) {
        bus = { handlers: new Map(), once: new Set() };
        buses.set(target, bus);
    }
    return bus;
}

function installInstanceApi<T extends object>(instance: T): T {
    const anyInstance = instance as unknown as AnyRecord;

    anyInstance.on = function (event: string, handler: (...args: unknown[]) => void) {
        const bus = busOf(instance);
        let set = bus.handlers.get(event);
        if (!set) {
            set = new Set();
            bus.handlers.set(event, set);
        }
        set.add(handler);
        return handler;
    };

    anyInstance.off = function (event: string, handler: (...args: unknown[]) => void) {
        busOf(instance).handlers.get(event)?.delete(handler);
    };

    anyInstance.once = function (event: string, handler: (...args: unknown[]) => void) {
        const wrapper = (...args: unknown[]) => {
            busOf(instance).handlers.get(event)?.delete(wrapper);
            handler(...args);
        };
        const on = anyInstance.on as (name: string, fn: (...args: unknown[]) => void) => unknown;
        return on(event, wrapper);
    };

    anyInstance.emit = function (event: string, ...args: unknown[]) {
        const set = busOf(instance).handlers.get(event);
        if (!set) {
            return;
        }
        for (const handler of set) {
            handler(...args);
        }
    };

    return instance;
}

/** Hook names the host dispatches; forwarded from instance methods of the same name. */
const HOOKS = [
    "onServerTick",
    "onChat",
    "onPlayerJoin",
    "onPlayerSpawned",
    "onPlayerLeave",
    "onPlayerDespawned",
    "onDamage",
    "onNet",
    "onNetFrame"
] as const;

/**
 * Adapts a class instance (or a plain object) to the shape the host dispatches.
 * `setup(ctx)` runs on load; `teardown()` on unload.
 */
export function adaptPlugin<T extends object>(instance: T): PluginShape {
    installInstanceApi(instance);
    const anyInstance = instance as unknown as AnyRecord;
    const shape: AnyRecord = { ...anyInstance };

    shape.version = typeof anyInstance.version === "string" ? anyInstance.version : shape.version;
    shape.exports = anyInstance.exports ?? {};
    shape.description = anyInstance.description ?? "";

    const setup = anyInstance.setup ?? anyInstance.onLoad;
    shape.onLoad = function (this: unknown, ctx: PluginContext) {
        anyInstance.ctx = ctx;
        anyInstance.host = ctx.host;
        anyInstance.state = ctx.state;
        if (typeof setup === "function") {
            (setup as (ctx: PluginContext) => void).call(instance, ctx);
        }
    };

    const teardown = anyInstance.teardown ?? anyInstance.onUnload;
    shape.onUnload = function (this: unknown, ctx: PluginContext) {
        if (typeof teardown === "function") {
            (teardown as (ctx: PluginContext) => void).call(instance, ctx);
        }
        const bus = buses.get(instance);
        bus?.handlers.clear();
        bus?.once.clear();
    };

    for (const hook of HOOKS) {
        const method = anyInstance[hook];
        if (typeof method !== "function") {
            continue;
        }
        shape[hook] = function (this: unknown, ctx: PluginContext, payload: unknown) {
            return (method as (ctx: PluginContext, payload: unknown) => unknown).call(instance, ctx, payload);
        };
    }

    return shape as unknown as PluginShape;
}
