import { context } from "./runtime";
import type { RpcMeta } from "./api/types";

/** Receives a routed event's data and where it came from. */
export type RpcListener<T> = (data: T, meta: RpcMeta) => void;

/** Answers a request synchronously; the returned value is serialised and sent back. */
export type RpcResponder<T, R> = (data: T, meta: RpcMeta) => R;

/**
 * A named channel over Valheim's routed RPC. A channel is a logical namespace, not a separate
 * socket. Registration, dispatch and cleanup live in the host; each listener or responder is
 * one host callback, so multiple listeners on the same event are independent.
 */
export class RpcChannel {
    constructor(readonly name: string) {}

    /** Notification listener. Returns an unsubscribe function. */
    on<T>(event: string, handler: RpcListener<T>): () => void {
        const api = context().host.api.net;
        const id = api.on(this.name, event, false, (data, meta) => {
            handler(data as T, meta as RpcMeta);
        });
        return () => api.off(id);
    }

    /** One responder per channel/event on each peer. Returns an unsubscribe function. */
    handle<T, R>(event: string, handler: RpcResponder<T, R>): () => void {
        const api = context().host.api.net;
        const id = api.on(this.name, event, true, (data, meta) => handler(data as T, meta as RpcMeta));
        return () => api.off(id);
    }

    /** Fire-and-forget notification; broadcasts when `target` is omitted. */
    emit<T>(event: string, data: T, target?: number): void {
        const api = context().host.api.net;
        api.emit(target === undefined ? api.everybody() : target, this.name, event, JSON.stringify(data));
    }

    /** Asks one peer for a reply. Rejects on timeout, missing responder or handler failure. */
    request<T, R>(target: number, event: string, data: T, timeoutMs = 30000): Promise<R> {
        const api = context().host.api.net;
        return new Promise<R>((resolve, reject) => {
            api.request(target, this.name, event, JSON.stringify(data), timeoutMs, (error, reply) => {
                if (typeof error === "string" && error.length > 0) {
                    reject(new Error(error));
                    return;
                }

                try {
                    resolve(JSON.parse(String(reply)) as R);
                } catch (failure) {
                    reject(failure);
                }
            });
        });
    }
}

export const Rpc = {
    channel: (name: string): RpcChannel => new RpcChannel(name),
    available: (): boolean => context().host.api.net.available(),
    serverPeer: (): number => context().host.api.net.serverPeer()
};
