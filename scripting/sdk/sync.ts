import { context } from "./runtime";
import type { RpcMeta, SyncEntity } from "./api/types";

/** Explicit transient binary streams. The C# service owns framing, transport and cleanup. */
export const Sync = {
    on(stream: string, handler: (entities: SyncEntity[], meta: RpcMeta) => void): () => void {
        const api = context().host.api.sync;
        const id = api.on(stream, (entities, meta) => handler(entities as SyncEntity[], meta as RpcMeta));
        return () => api.off(id);
    },
    send(stream: string, entities: SyncEntity[], target?: number): void {
        const api = context().host.api;
        api.sync.send(target ?? api.net.everybody(), stream, JSON.stringify(entities));
    }
};

/** Inspection/storage helper; Sync.send transmits bytes without base64 or JSON wrapping. */
export function encodeFrame(entities: SyncEntity[]): string {
    return context().host.api.sync.encode(JSON.stringify(entities));
}
export function decodeFrame(frame: string): SyncEntity[] { return context().host.api.sync.decode(frame); }
export function diffEntity(previous: SyncEntity, next: SyncEntity, step = 0.01): SyncEntity | null {
    return context().host.api.sync.diff(JSON.stringify(previous), JSON.stringify(next), step);
}
export function quantise(value: number, step = 0.01): number { return context().host.api.sync.quantise(value, step); }
