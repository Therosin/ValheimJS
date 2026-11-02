import { context } from "./index";
import { Networking } from "./networking";

/**
 * Typed messaging between plugins and processes.
 *
 * The system owns the envelope: `kind`, `channel`, `from`, `ts`, `id`. You supply a channel and
 * a payload, and the type parameter describes the payload:
 *
 * ```ts
 * interface BuyRequest { item: string; amount: number }
 * interface BuyReply { ok: boolean; balance: number }
 *
 * // fire and forget
 * Messaging.send<BuyRequest>("shop:buy", { item: "Coins", amount: 10 });
 *
 * // ask, and await the answer
 * const reply = await Messaging.request<BuyRequest, BuyReply>("shop:buy", { item: "Coins", amount: 10 });
 *
 * // receive
 * Messaging.handle<BuyRequest>("shop:buy", (envelope) => {
 *     Messaging.reply<BuyReply>(envelope, { ok: true, balance: 120 });
 * });
 * ```
 *
 * Replies are awaitable: the host pumps the engine's continuation queue on every tick, so a
 * promise settled by a later network message resumes on the next tick.
 */

export type EnvelopeKind = "event" | "request" | "reply";

export interface Envelope<T = unknown> {
    v: 1;
    kind: EnvelopeKind;
    channel: string;
    /** Peer uid that sent it — filled by the system on receipt. */
    sender?: number;
    /** Plugin that sent it — filled by the system. */
    from: string;
    /** System timestamp. */
    ts: number;
    /** Sending process. Broadcasts loop back to the sender, so handlers skip their own. */
    origin?: string;
    /** Claimed authority of the sender; checked against the real server peer on receipt. */
    role?: "server" | "client";
    /** Correlation id on requests/replies — filled by the system. */
    id?: number;
    data: T;
}

export type EnvelopeHandler<T = unknown> = (envelope: Envelope<T>) => void;

export interface RequestOptions {
    /** Milliseconds before the pending promise is abandoned (default 5 000). */
    timeoutMs?: number;
    /** Peer uid to ask. Omit to broadcast (every listener gets it, any answer is accepted). */
    target?: number;
}

interface Pending {
    resolve: (value: unknown) => void;
    reject: (reason: unknown) => void;
    timer: number;
}

let nextId = 1;
const pending = new Map<number, Pending>();

function sendEnvelope<T>(envelope: Envelope<T>): void {
    Networking.sendRaw(envelope.channel, JSON.stringify(envelope));
}

function baseEnvelope(kind: EnvelopeKind): Pick<Envelope, "v" | "kind" | "from" | "ts" | "origin" | "role"> {
    const ctx = context();
    return {
        v: 1,
        kind,
        from: ctx.name,
        ts: Date.now(),
        origin: ctx.host.api.env.instance(),
        role: ctx.host.api.env.isServer() ? "server" : "client"
    };
}

export const Messaging = {
    /** Fire an event. */
    send<T = unknown>(channel: string, data: T): void {
        sendEnvelope<T>({ ...baseEnvelope("event"), channel, data });
    },

    /**
     * Ask the other side something. Resolves with the reply payload; rejects if no answer arrives
     * within `timeoutMs`. Awaiting it yields to the host, so the reply can arrive on a later tick.
     */
    request<T = unknown, R = unknown>(channel: string, data: T, options: RequestOptions = {}): Promise<R> {
        const timeoutMs = options.timeoutMs ?? 5_000;
        const id = nextId++;

        const envelope: Envelope<T> = { ...baseEnvelope("request"), channel, id, data };

        const { promise, resolve, reject } = Promise.withResolvers<R>();

        const timer = setTimeout(() => {
            pending.delete(id);
            reject(new Error(`no reply on '${channel}' within ${timeoutMs}ms`));
        }, timeoutMs) as unknown as number;

        pending.set(id, { resolve: (value) => resolve(value as R), reject, timer });

        // The reply lands on this channel, so this process must be able to receive it. Sending
        // without listening is why replies vanished silently before.
        context().host.api.net.listen(channel);

        if (typeof options.target === "number" && options.target !== 0) {
            Networking.sendTo(options.target, channel, envelope);
        } else {
            sendEnvelope(envelope);
        }

        return promise;
    },

    /** Answer a request envelope. Replies are addressed to the asking peer, never broadcast. */
    reply<R = unknown>(request: Envelope, data: R): void {
        if (request.id === undefined) {
            return;
        }

        const envelope: Envelope<R> = {
            ...baseEnvelope("reply"),
            channel: request.channel,
            id: request.id,
            data
        };

        if (typeof request.sender === "number" && request.sender !== 0) {
            Networking.sendTo(request.sender, request.channel, envelope);
            return;
        }

        // No known requester: replying is meaningless, and broadcasting would echo forever.
        context().host.log("warn", `dropping reply on '${request.channel}': unknown sender`);
    },

    /** Receive events and requests on a channel. Replies are routed to `request`, not here. */
    handle<T = unknown>(channel: string, handler: EnvelopeHandler<T>): void {
        Networking.handleRaw(channel, (raw, message) => {
            const envelope = decodeEnvelope<T>(raw);
            if (!envelope || envelope.kind === "reply") {
                return;
            }

            // RPC broadcasts are delivered back to the sender; a plugin should not hear itself.
            const ctx = context();
            if (envelope.origin && envelope.origin === ctx.host.api.env.instance()) {
                return;
            }

            // A message claiming server authority must actually come from the server peer.
            if (envelope.role === "server") {
                const serverPeer = ctx.host.api.net.serverPeer();
                if (serverPeer !== 0 && message.sender !== serverPeer) {
                    ctx.host.log("warn", `dropped spoofed server message on '${channel}' from ${message.sender}`);
                    return;
                }
            }

            handler({ ...envelope, sender: message.sender });
        });
    },

    /** Called by the host for every incoming payload; resolves pending requests. */
    dispatch(envelope: Envelope): void {
        if (envelope.kind !== "reply" || envelope.id === undefined) {
            return;
        }

        const entry = pending.get(envelope.id);
        if (!entry) {
            return;
        }

        pending.delete(envelope.id);
        clearTimeout(entry.timer);
        entry.resolve(envelope.data);
    }
};

export function decodeEnvelope<T = unknown>(raw: string): Envelope<T> | null {
    try {
        const parsed = JSON.parse(raw) as Partial<Envelope<T>>;
        if (parsed && parsed.v === 1 && typeof parsed.channel === "string" && typeof parsed.kind === "string") {
            return parsed as Envelope<T>;
        }
    } catch {
        // not an envelope: a frame or a foreign payload
    }
    return null;
}

/**
 * Channel-typed facade, so a plugin declares its wire schema once:
 *
 * ```ts
 * const wire = defineWire<{
 *     "shop:buy": { request: BuyRequest; reply: BuyReply };
 *     "shop:stock": { event: StockUpdate };
 * }>();
 *
 * await wire.request("shop:buy", { item: "Coins", amount: 1 });
 * wire.send("shop:stock", { coins: 500 });
 * wire.handle("shop:stock", (envelope) => …);
 * ```
 */
export interface WireSchema {
    [channel: string]:
        | { request: unknown; reply: unknown; event?: unknown }
        | { event: unknown; request?: never; reply?: never };
}

type RequestOf<S> = S extends { request: infer R } ? R : never;
type ReplyOf<S> = S extends { reply: infer R } ? R : never;
type EventOf<S> = S extends { event: infer E } ? E : never;

export function defineWire<S extends WireSchema>() {
    return {
        send<K extends keyof S & string>(channel: K, data: EventOf<S[K]>): void {
            Messaging.send(channel, data);
        },
        request<K extends keyof S & string>(
            channel: K,
            data: RequestOf<S[K]>,
            options?: RequestOptions
        ): Promise<ReplyOf<S[K]>> {
            return Messaging.request<RequestOf<S[K]>, ReplyOf<S[K]>>(channel, data, options);
        },
        handle<K extends keyof S & string>(
            channel: K,
            handler: (envelope: Envelope<EventOf<S[K]> & RequestOf<S[K]>>) => void
        ): void {
            Messaging.handle(channel, handler as EnvelopeHandler<EventOf<S[K]> & RequestOf<S[K]>>);
        },
        reply<K extends keyof S & string>(request: Envelope, data: ReplyOf<S[K]>): void {
            Messaging.reply(request, data);
        }
    };
}
