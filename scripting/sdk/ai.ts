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

/** Operation passed to blackboard middleware/interceptors. Resolve once the callback returns. */
export interface BoardOperationContext {
    readonly kind: "change" | "signal";
    readonly key: string;
    readonly value: unknown;
    readonly oldValue: unknown;
    readonly channel: string | null;
    readonly scope: "local" | "shared";
    isChange(key?: string): boolean;
    isEvent(name?: string): boolean;
    get<T = unknown>(key: string): T | null;
    has(key: string): boolean;
    set(key: string, value: unknown): void;
    emit(name: string, data?: unknown): void;
    /** Interceptor only. */
    cancel(): void;
    /** Interceptor only. */
    replace(value: unknown): void;
}

export type BoardMiddlewareHandler = (context: BoardOperationContext) => void;

/** A one-shot unregister handle returned by board subscriptions and memberships. */
export class BoardSubscription {
    private off: (() => void) | null;

    constructor(off: () => void) {
        this.off = off;
    }

    get closed(): boolean {
        return this.off === null;
    }

    dispose(): void {
        const off = this.off;
        this.off = null;
        off?.();
    }

    /** Alias for callers that prefer explicit unregister naming. */
    unregister(): void {
        this.dispose();
    }
}

const LOCAL_ROOT = "@local";
const SHARED_ROOT = "@shared";

/**
 * One blackboard scope. Local scopes are owned by this plugin; shared scopes are world-shared and
 * addressed by name. Subscriptions and middleware are plugin-scoped and released on unload.
 */
export class BoardScope {
    readonly name: string | null;
    readonly shared: boolean;
    private readonly channel: string | null;

    constructor(shared = false, name: string | null = null) {
        this.shared = shared;
        this.name = name?.trim() || null;
        this.channel = this.name === null
            ? (shared ? SHARED_ROOT : LOCAL_ROOT)
            : `${shared ? SHARED_ROOT : LOCAL_ROOT}/${this.name}`;
    }

    get<T = unknown>(key: string): T | null {
        return context().host.api.ai.boardGet(key, this.channel) as T | null;
    }

    set(key: string, value: unknown): void {
        context().host.api.ai.boardSet(key, value, this.channel);
    }

    has(key: string): boolean {
        return context().host.api.ai.boardHas(key, this.channel);
    }

    snapshot<T = Record<string, unknown>>(): T {
        return context().host.api.ai.boardSnapshot(this.channel) as T;
    }

    emit(name: string, data?: unknown): void {
        context().host.api.ai.boardEmit(name, data ?? null, this.channel);
    }

    /** Empty `key` observes every key in this scope. */
    onChange(key: string, handler: BlackboardChangeHandler): BoardSubscription {
        const id = context().host.api.ai.boardOnChange(
            key,
            handler as (...args: unknown[]) => unknown,
            this.channel
        );
        return new BoardSubscription(() => { context().host.api.ai.boardOff(id); });
    }

    /** Empty `name` observes every signal in this scope. */
    onSignal(name: string, handler: BlackboardSignalHandler): BoardSubscription {
        const id = context().host.api.ai.boardOnSignal(
            name,
            handler as (...args: unknown[]) => unknown,
            this.channel
        );
        return new BoardSubscription(() => { context().host.api.ai.boardOff(id); });
    }

    /** Observe committed operations. */
    use(handler: BoardMiddlewareHandler): BoardSubscription {
        const id = context().host.api.ai.boardUse(
            handler as (...args: unknown[]) => unknown,
            this.channel,
            false
        );
        return new BoardSubscription(() => { context().host.api.ai.boardOff(id); });
    }

    /** Observe and transform/cancel operations before they commit. */
    intercept(handler: BoardMiddlewareHandler): BoardSubscription {
        const id = context().host.api.ai.boardUse(
            handler as (...args: unknown[]) => unknown,
            this.channel,
            true
        );
        return new BoardSubscription(() => { context().host.api.ai.boardOff(id); });
    }

    join(): BoardMembership {
        if (this.name === null) throw new Error("A board membership requires a named scope.");
        return new BoardMembership(this);
    }

    /** Drops this local channel after its entity/subscribers are gone. Shared scopes are not removable. */
    release(): boolean {
        if (this.shared || this.name === null) return false;
        return context().host.api.ai.boardDrop(this.channel);
    }
}

/**
 * Scoped participation in a shared board. Tracks subscriptions made through the membership and
 * releases them together, which keeps join/leave lifecycle out of individual behaviours.
 */
export class BoardMembership {
    readonly scope: BoardScope;
    private readonly subscriptions = new Set<BoardSubscription>();
    private closed = false;

    constructor(scope: BoardScope) {
        this.scope = scope;
    }

    get name(): string | null {
        return this.scope.name;
    }

    get<T = unknown>(key: string): T | null {
        this.requireOpen();
        return this.scope.get<T>(key);
    }

    set(key: string, value: unknown): void {
        this.requireOpen();
        this.scope.set(key, value);
    }

    has(key: string): boolean {
        this.requireOpen();
        return this.scope.has(key);
    }

    emit(name: string, data?: unknown): void {
        this.requireOpen();
        this.scope.emit(name, data);
    }

    onChange(key: string, handler: BlackboardChangeHandler): BoardSubscription {
        return this.track(this.scope.onChange(key, handler));
    }

    onSignal(name: string, handler: BlackboardSignalHandler): BoardSubscription {
        return this.track(this.scope.onSignal(name, handler));
    }

    use(handler: BoardMiddlewareHandler): BoardSubscription {
        return this.track(this.scope.use(handler));
    }

    intercept(handler: BoardMiddlewareHandler): BoardSubscription {
        return this.track(this.scope.intercept(handler));
    }

    dispose(): void {
        if (this.closed) return;
        this.closed = true;
        for (const subscription of this.subscriptions) subscription.dispose();
        this.subscriptions.clear();
    }

    /** Alias for callers that prefer the object-shaped registration naming. */
    unregister(): void {
        this.dispose();
    }

    private track(subscription: BoardSubscription): BoardSubscription {
        this.requireOpen();
        this.subscriptions.add(subscription);
        return subscription;
    }

    private requireOpen(): void {
        if (this.closed) throw new Error("This board membership has been disposed.");
    }
}

/** Per-plugin reactive blackboard: local root, isolated local channels, shared world channels. */
export const ai = {
    board: {
        /** A local scope owned by this plugin; `name` isolates one entity/behaviour. */
        local(name?: string): BoardScope {
            return new BoardScope(false, name ?? null);
        },
        /** A world-shared scope; every plugin resolves the same name to the same board channel. */
        shared(name?: string): BoardScope {
            return new BoardScope(true, name ?? null);
        },
        /** Joins a shared channel with a disposable membership that owns its subscriptions. */
        join(name: string): BoardMembership {
            return new BoardScope(true, name).join();
        },
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
