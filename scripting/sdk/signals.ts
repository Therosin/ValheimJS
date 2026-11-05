import { context } from "./runtime";

export interface SignalsOptions {
    /** Sampling period in milliseconds. Defaults to 250. */
    intervalMs?: number;
}

/**
 * Host-sampled observed conditions. Conditions are facts the host re-reads on an interval;
 * `name:Enter` fires on false→true, `name:Exit` on true→false, and the bare `name` on later true
 * samples. Handlers are plugin-scoped and removed on unload.
 *
 * Composition: build a predicate from `predicates.ts` (`all`, `any`, `not`, `stable`, `delay`,
 * `cooldown`, `once`) and observe it as one named condition. Layering works because derived
 * predicates read the inputs' latest sampled values; a defined input sampled later in the same
 * pass appears one interval late, so prefer composing the raw facts rather than condition names.
 */
export interface Signals {
    /** Defines or replaces a condition. The predicate runs at the sampling interval. */
    observe(name: string, predicate: () => boolean): void;
    /** Subscribes to `name`, `name:Enter` or `name:Exit`. Returns an unsubscribe function. */
    on(event: string, handler: () => void): () => void;
    /** Subscribes for exactly one invocation. */
    once(event: string, handler: () => void): void;
    /** Last sampled value; never evaluates. */
    is(name: string): boolean;
    /** True when the last sample changed the value; reading does not clear it. */
    changed(name: string): boolean;
    /** Independent copy of every condition and its last value. */
    snapshot(): Record<string, boolean>;
    remove(name: string): void;
    reset(name: string): void;
    clear(): void;
    dispose(): void;
}

/** Creates a plugin-scoped observer. Dispose it explicitly, or let plugin unload clean it up. */
export function signals(options: SignalsOptions = {}): Signals {
    const api = context().host.api.signals;
    const id = api.create(options.intervalMs ?? 250);

    const is = (name: string): boolean => api.is(id, name);

    return {
        observe(name, predicate) {
            api.observe(id, name, () => predicate() === true);
        },
        on(event, handler) {
            const subscription = api.on(id, event, () => handler());
            return () => api.off(id, subscription);
        },
        once(event, handler) {
            api.once(id, event, () => handler());
        },
        is,
        changed: name => api.changed(id, name),
        snapshot() {
            return api.snapshot(id) as Record<string, boolean>;
        },
        remove: name => api.remove(id, name),
        reset: name => api.reset(id, name),
        clear: () => api.clear(id),
        dispose: () => api.dispose(id)
    };
}
