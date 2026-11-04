import { beforeEach, describe, expect, test } from "bun:test";
import { Rpc } from "../sdk/net";
import type { RpcMeta } from "../sdk/api/types";

interface Registered {
    id: number;
    channel: string;
    event: string;
    response: boolean;
    handler: (...args: unknown[]) => unknown;
}

function install() {
    const registered: Registered[] = [];
    const sent: unknown[] = [];
    let nextId = 1;
    const net = {
        available: () => true,
        everybody: () => 0,
        serverPeer: () => 7,
        on: (channel: string, event: string, response: boolean, handler: (...args: unknown[]) => unknown) => {
            const id = nextId++;
            registered.push({ id, channel, event, response, handler });
            return id;
        },
        off: (id: number) => {
            const index = registered.findIndex((entry) => entry.id === id);
            if (index >= 0) registered.splice(index, 1);
        },
        emit: (...args: unknown[]) => sent.push(args),
        request: (
            target: number,
            channel: string,
            event: string,
            dataJson: string,
            timeoutMs: number,
            callback: (error: string | null, reply: string | null) => void
        ) => {
            sent.push([target, channel, event, dataJson, timeoutMs]);
            queueMicrotask(() => callback(null, '{"value":43}'));
        }
    };

    (globalThis as { __vjs_context?: unknown }).__vjs_context = {
        name: "events",
        host: { api: { net } }
    };

    return { registered, sent };
}

beforeEach(() => {
    (globalThis as { __vjs_context?: unknown }).__vjs_context = undefined;
});

describe("RPC channel facade", () => {
    test("routes notifications to this listener and unsubscribes by id", () => {
        const { registered } = install();
        const channel = Rpc.channel("shop");
        const seen: Array<[unknown, RpcMeta]> = [];
        const off = channel.on<{ amount: number }>("stock", (data, meta) => seen.push([data, meta]));

        expect(registered.length).toBe(1);
        expect(registered[0].response).toBe(false);

        registered[0].handler({ amount: 2 }, { sender: 9, channel: "shop", event: "stock" });
        expect(seen).toEqual([[{ amount: 2 }, { sender: 9, channel: "shop", event: "stock" }]]);

        off();
        expect(registered.length).toBe(0);
    });

    test("sends notifications with the channel and JSON payload", () => {
        const { sent } = install();
        Rpc.channel("shop").emit("stock", { amount: 2 });
        expect(sent).toEqual([[0, "shop", "stock", '{"amount":2}']]);
    });

    test("answers requests through the host callback", () => {
        const { registered } = install();
        const off = Rpc.channel("shop").handle<{ value: number }, { value: number }>("ping", (data) => ({
            value: data.value + 1
        }));

        expect(registered[0].response).toBe(true);
        expect(registered[0].handler({ value: 41 }, { sender: 5, channel: "shop", event: "ping" })).toEqual({ value: 42 });
        off();
    });

    test("resolves outgoing requests from the completion callback", async () => {
        const { sent } = install();
        const reply = await Rpc.channel("shop").request<{ value: number }, { value: number }>(5, "ping", { value: 42 }, 5000);
        expect(reply).toEqual({ value: 43 });
        expect(sent[0]).toEqual([5, "shop", "ping", '{"value":42}', 5000]);
    });

    test("rejects when the host reports an error", async () => {
        install();
        const net = (globalThis as { __vjs_context?: { host: { api: { net: { request: unknown } } } } }).__vjs_context!.host.api.net;
        (net as { request: unknown }).request = (
            _target: number,
            _channel: string,
            _event: string,
            _dataJson: string,
            _timeoutMs: number,
            callback: (error: string, reply: null) => void
        ) => queueMicrotask(() => callback("Request cancelled", null));

        await expect(Rpc.channel("shop").request(5, "ping", {}, 10)).rejects.toThrow("Request cancelled");
    });
});
