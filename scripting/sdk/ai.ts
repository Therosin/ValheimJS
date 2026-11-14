import { context } from "./runtime";
import type {
    AiActionOutcome,
    AiContext,
    AiInstantiateOptions,
    AiLoadOptions,
    AiStatus,
    AiTickResult
} from "./api/types";

export type { AiActionOutcome, AiContext, AiInstantiateOptions, AiLoadOptions, AiStatus, AiTickResult } from "./api/types";

/** Subscriber for blackboard state changes: key, new value, old value, channel. */
export type BlackboardChangeHandler = (
    key: string,
    value: unknown,
    oldValue: unknown,
    channel: string | null
) => void;

export type BlackboardSignalHandler = (
    name: string,
    data: unknown,
    channel: string | null
) => void;

/** Per-plugin reactive blackboard. `channel` isolates independent state and subscribers. */
export const ai = {
    board: {
        get<T = unknown>(key: string, channel?: string): T | null {
            return context().host.api.ai.boardGet(key, channel ?? null) as T | null;
        },
        set(key: string, value: unknown, channel?: string): void {
            context().host.api.ai.boardSet(key, value, channel ?? null);
        },
        has(key: string, channel?: string): boolean {
            return context().host.api.ai.boardHas(key, channel ?? null);
        },
        snapshot<T = Record<string, unknown>>(channel?: string): T {
            return context().host.api.ai.boardSnapshot(channel ?? null) as T;
        },
        emit(name: string, data?: unknown, channel?: string): void {
            context().host.api.ai.boardEmit(name, data ?? null, channel ?? null);
        },
        /** Empty `key` observes every key. Returns a subscription id. */
        onChange(key: string, handler: BlackboardChangeHandler, channel?: string): number {
            return context().host.api.ai.boardOnChange(
                key,
                handler as (...args: unknown[]) => unknown,
                channel ?? null
            );
        },
        /** Empty `name` observes every signal. Returns a subscription id. */
        onSignal(name: string, handler: BlackboardSignalHandler, channel?: string): number {
            return context().host.api.ai.boardOnSignal(
                name,
                handler as (...args: unknown[]) => unknown,
                channel ?? null
            );
        },
        off(subscription: number): boolean {
            return context().host.api.ai.boardOff(subscription);
        }
    },
    /** Enable host-side tracing of tree nodes, actions, conditions and imports for this plugin. */
    debug(enabled: boolean): boolean {
        return context().host.api.ai.debug(enabled);
    },
    /** Compiles YAML text; imports come from `options.imports` keyed by alias, never from disk. */
    compile(yaml: string, options: AiLoadOptions = {}): string {
        return context().host.api.ai.compile(yaml, options);
    },
    /** Creates an independent execution state for a compiled tree. */
    instantiate(tree: string, options: AiInstantiateOptions = {}): string {
        return context().host.api.ai.instantiate(tree, options) as string;
    },
    /** Advances one instance. Defaults to a 50 ms step; NPCs should pass their tick interval. */
    tick(instance: string, deltaTime = 0.05): AiTickResult {
        return context().host.api.ai.tick(instance, deltaTime) as AiTickResult;
    },
    /** Deep copy of the instance state. */
    state(instance: string): Record<string, unknown> {
        return context().host.api.ai.state(instance) as Record<string, unknown>;
    },
    /** Replaces one top-level state value. */
    set(instance: string, key: string, value: unknown): void {
        context().host.api.ai.set(instance, key, value);
    },
    status(instance: string): AiStatus {
        return context().host.api.ai.status(instance) as AiStatus;
    },
    /** Stops the instance; active `execAfter` hooks run with `interrupted: true`. */
    cancel(instance: string): void {
        context().host.api.ai.cancel(instance);
    },
    /** Clears execution progress; `resetState` restores declared defaults. */
    reset(instance: string, resetState = false): void {
        context().host.api.ai.reset(instance, resetState);
    },
    /** Drops one instance without running hooks. */
    release(instance: string): boolean {
        return context().host.api.ai.release(instance);
    },
    /** Drops a compiled tree and every instance using it. */
    unload(tree: string): boolean {
        return context().host.api.ai.unload(tree);
    }
};
