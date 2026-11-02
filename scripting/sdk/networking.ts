import { context, type PluginContext } from "./index";

/** What arrives from the other process. */
export interface NetMessage {
    channel: string;
    sender: number;
    /** Parsed payload — the sender's JSON. */
    data: unknown;
}

export type NetHandler = (message: NetMessage) => void;

interface Registration {
    channel: string;
    handler: NetHandler;
}

const handlers = new Map<string, Set<NetHandler>>();
const rawHandlers = new Map<string, Set<(raw: string, message: NetMessage) => void>>();
const frameHandlers = new Map<string, Set<(frame: string, sender: number) => void>>();

function key(plugin: string, channel: string): string {
    return `${plugin}\u0000${channel}`;
}

function listeners(plugin: string, channel: string): Set<NetHandler> {
    const composite = key(plugin, channel);
    let set = handlers.get(composite);
    if (!set) {
        set = new Set();
        handlers.set(composite, set);
    }
    return set;
}

/**
 * Messaging between server and clients.
 *
 * ```ts
 * Networking.handle("hello", (message) => log(JSON.stringify(message.data)));
 * Networking.send("hello", { from: ctx.name });
 * ```
 */
export const Networking = {
    /** Listen on a channel and register a handler. Safe to call in every load. */
    handle(channel: string, handler: NetHandler): void {
        const ctx = context();
        listeners(ctx.name, channel).add(handler);
        ctx.host.api.net.listen(channel);
    },

    /** Alias for `handle`. */
    on(channel: string, handler: NetHandler): void {
        Networking.handle(channel, handler);
    },

    off(channel: string, handler: NetHandler): void {
        listeners(context().name, channel).delete(handler);
    },

    /** Broadcast to every peer (no-op for a client's own process). */
    send(channel: string, data: unknown): void {
        context().host.api.net.send(channel, JSON.stringify(data ?? null));
    },

    /**
     * Broadcast an already-encoded payload. Use this for binary frames so they are not
     * JSON-wrapped on the way out.
     */
    sendRaw(channel: string, payload: string): void {
        context().host.api.net.send(channel, payload);
    },

    /** Send to one peer uid. */
    sendTo(uid: number, channel: string, data: unknown): void {
        context().host.api.net.sendTo(uid, channel, JSON.stringify(data ?? null));
    },

    /** Alias for `send` with an optional target uid. */
    emit(channel: string, data: unknown, targetUid?: number): void {
        if (typeof targetUid === "number") {
            Networking.sendTo(targetUid, channel, data);
            return;
        }
        Networking.send(channel, data);
    },

    /**
     * Send a compact frame (base64 from `encodeFrame`). The host decodes it and puts the raw
     * bytes in a ZPackage, so the wire carries bytes, not base64.
     */
    sendFrame(channel: string, frame: string): void {
        context().host.api.net.sendFrame(channel, frame);
    },

    sendFrameTo(uid: number, channel: string, frame: string): void {
        context().host.api.net.sendFrameTo(uid, channel, frame);
    },

    /** Receive frames; `frame` is the base64 produced on the other side. */
    handleFrame(channel: string, handler: (frame: string, sender: number) => void): void {
        const ctx = context();
        const composite = key(ctx.name, channel);
        let set = frameHandlers.get(composite);
        if (!set) {
            set = new Set();
            frameHandlers.set(composite, set);
        }
        set.add(handler);
        ctx.host.api.net.listenFrame(channel);
    },

    /** Receive the raw payload string (frames, custom encodings). */
    handleRaw(channel: string, handler: (raw: string, message: NetMessage) => void): void {
        const ctx = context();
        const composite = key(ctx.name, channel);
        let set = rawHandlers.get(composite);
        if (!set) {
            set = new Set();
            rawHandlers.set(composite, set);
        }
        set.add(handler);
        ctx.host.api.net.listen(channel);
    },

    channels(): string[] {
        return context().host.api.net.channels();
    }
};

/** Called by `definePlugin`'s generated `onNetFrame` hook. */
export function dispatchFrames(ctx: PluginContext, payload: { channel: string; sender: number; data: unknown }): void {
    const set = frameHandlers.get(key(ctx.name, payload.channel));
    if (!set) {
        return;
    }

    const frame = typeof payload.data === "string" ? payload.data : "";
    for (const handler of set) {
        try {
            handler(frame, payload.sender);
        } catch (error) {
            ctx.host.log("error", `frame handler for '${payload.channel}' threw: ${String(error)}`);
        }
    }
}

/** Called by `definePlugin`'s generated `onNet` hook. */
export function dispatchToHandlers(ctx: PluginContext, payload: { channel: string; sender: number; data: unknown }): void {
    let data: unknown = payload.data;
    if (typeof data === "string") {
        try {
            data = JSON.parse(data);
        } catch {
            data = null;
        }
    }

    const message: NetMessage = { channel: payload.channel, sender: payload.sender, data };

    // Typed and raw handlers are independent: Messaging registers only raw, so a missing typed
    // set must not stop delivery.
    const set = handlers.get(key(ctx.name, payload.channel));
    if (set) {
        for (const handler of set) {
            try {
                handler(message);
            } catch (error) {
                ctx.host.log("error", `net handler for '${payload.channel}' threw: ${String(error)}`);
            }
        }
    }

    const rawSet = rawHandlers.get(key(ctx.name, payload.channel));
    if (!rawSet) {
        return;
    }

    const raw = typeof payload.data === "string" ? payload.data : JSON.stringify(payload.data ?? null);
    for (const handler of rawSet) {
        try {
            handler(raw, message);
        } catch (error) {
            ctx.host.log("error", `raw net handler for '${payload.channel}' threw: ${String(error)}`);
        }
    }
}
