import { describe, expect, test } from "bun:test";
import { definePlugin, Messaging } from "../sdk/index";

interface Sends {
    broadcast: { channel: string; payload: string }[];
    directed: { uid: number; channel: string; payload: string }[];
}

function fakeCtx(name: string, sends: Sends, instanceId = "inst-" + name) {
    const listened: string[] = [];
    return {
        ctx: {
            name,
            dir: "/tmp/" + name,
            paths: {} as never,
            state: {},
            exports: {},
            dep: () => ({}),
            host: {
                name,
                dir: "/tmp/" + name,
                log: () => {},
                now: () => 0,
                config: () => null,
                storage: { get: () => null, set: () => {} },
                info: () => ({ name, version: "1", api: "1", dir: "" }),
                api: {
                    env: {
                        instance: () => instanceId,
                        side: () => "server",
                        dedicated: () => true,
                        isServer: () => true,
                        isClient: () => false,
                        singlePlayer: () => false,
                        peers: () => 1
                    },
                    net: {
                        listen: (channel: string) => listened.push(channel),
                        send: (channel: string, payload: string) => sends.broadcast.push({ channel, payload }),
                        sendTo: (uid: number, channel: string, payload: string) => sends.directed.push({ uid, channel, payload }),
                        channels: () => [],
                        available: () => true,
                        side: () => "server",
                        serverPeer: () => 0,
                        listenFrame: () => {},
                        sendFrame: () => {},
                        sendFrameTo: () => {}
                    }
                }
            }
        },
        listened
    };
}

function envelope(kind: string, channel: string, id?: number, data: unknown = {}, origin = "other"): string {
    return JSON.stringify({ v: 1, kind, channel, from: "x", ts: 1, origin, id, data });
}

describe("messaging", () => {
    test("an event reaches a handler", () => {
        const sends: Sends = { broadcast: [], directed: [] };
        const received: unknown[] = [];
        const { ctx, listened } = fakeCtx("p1", sends);

        const plugin = definePlugin({
            onLoad() {
                Messaging.handle("probe", (e) => received.push(e.data));
            }
        });

        (plugin as unknown as { onLoad: (c: unknown, p: unknown) => void }).onLoad(ctx, undefined);
        expect(listened).toContain("probe");

        (plugin as unknown as { onNet: (c: unknown, p: unknown) => void }).onNet(ctx, {
            channel: "probe",
            sender: 11,
            data: envelope("request", "probe", 5, { a: 1 })
        });

        expect(received).toEqual([{ a: 1 }]);
    });

    test("a reply is addressed to the asking peer and never broadcast", () => {
        const sends: Sends = { broadcast: [], directed: [] };
        const { ctx } = fakeCtx("p2", sends);

        const plugin = definePlugin({
            onLoad() {
                Messaging.reply(
                    { v: 1, kind: "request", channel: "probe", from: "y", ts: 1, id: 9, sender: 42, data: {} },
                    { ok: true }
                );
            }
        });

        (plugin as unknown as { onLoad: (c: unknown, p: unknown) => void }).onLoad(ctx, undefined);

        expect(sends.broadcast).toHaveLength(0);
        expect(sends.directed).toHaveLength(1);
        expect(sends.directed[0].uid).toBe(42);
        expect(JSON.parse(sends.directed[0].payload).kind).toBe("reply");
    });

    test("handlers ignore replies, which is what stopped the echo loop", () => {
        const sends: Sends = { broadcast: [], directed: [] };
        const received: unknown[] = [];
        const { ctx } = fakeCtx("p3", sends);

        const plugin = definePlugin({
            onLoad() {
                Messaging.handle("probe", (e) => {
                    received.push(e.data);
                    Messaging.reply(e, { answer: true });
                });
            }
        });

        (plugin as unknown as { onLoad: (c: unknown, p: unknown) => void }).onLoad(ctx, undefined);
        const onNet = (plugin as unknown as { onNet: (c: unknown, p: unknown) => void }).onNet;

        onNet(ctx, { channel: "probe", sender: 7, data: envelope("reply", "probe", 1, { answer: true }) });
        expect(received).toHaveLength(0);
        expect(sends.directed).toHaveLength(0);

        onNet(ctx, { channel: "probe", sender: 7, data: envelope("request", "probe", 2, {}) });
        expect(received).toHaveLength(1);
        expect(sends.directed).toHaveLength(1);
        expect(sends.directed[0].uid).toBe(7);
    });

    test("request listens on its own channel so the reply can arrive", () => {
        const sends: Sends = { broadcast: [], directed: [] };
        const { ctx, listened } = fakeCtx("p5", sends);

        const plugin = definePlugin({ onLoad() {} });
        (plugin as unknown as { onLoad: (c: unknown, p: unknown) => void }).onLoad(ctx, undefined);

        void Messaging.request("reply-channel", { q: 1 }, { timeoutMs: 10 }).catch(() => {});
        expect(listened).toContain("reply-channel");
    });

    test("a plugin does not receive its own broadcast", () => {
        const sends: Sends = { broadcast: [], directed: [] };
        const received: unknown[] = [];
        const { ctx } = fakeCtx("p6", sends, "instance-6");

        const plugin = definePlugin({
            onLoad() {
                Messaging.handle("probe", (e) => received.push(e.data));
            }
        });

        (plugin as unknown as { onLoad: (c: unknown, p: unknown) => void }).onLoad(ctx, undefined);
        const onNet = (plugin as unknown as { onNet: (c: unknown, p: unknown) => void }).onNet;

        onNet(ctx, { channel: "probe", sender: 0, data: envelope("event", "probe", undefined, { me: 1 }, "instance-6") });
        expect(received).toHaveLength(0);

        onNet(ctx, { channel: "probe", sender: 9, data: envelope("event", "probe", undefined, { them: 1 }, "instance-other") });
        expect(received).toHaveLength(1);
    });

    test("a message claiming server role from a non-server peer is dropped", () => {
        const sends: Sends = { broadcast: [], directed: [] };
        const received: unknown[] = [];
        const { ctx } = fakeCtx("p7", sends);
        // pretend we are a client and the server peer is 1000
        (ctx.host.api.net as unknown as { serverPeer: () => number }).serverPeer = () => 1000;

        const plugin = definePlugin({
            onLoad() {
                Messaging.handle("probe", (e) => received.push(e.data));
            }
        });

        (plugin as unknown as { onLoad: (c: unknown, p: unknown) => void }).onLoad(ctx, undefined);
        const onNet = (plugin as unknown as { onNet: (c: unknown, p: unknown) => void }).onNet;

        const spoofed = JSON.stringify({ v: 1, kind: "event", channel: "probe", from: "x", ts: 1, origin: "srv", role: "server", data: { evil: 1 } });
        onNet(ctx, { channel: "probe", sender: 7, data: spoofed });
        expect(received).toHaveLength(0);

        const real = JSON.stringify({ v: 1, kind: "event", channel: "probe", from: "x", ts: 1, origin: "srv", role: "server", data: { ok: 1 } });
        onNet(ctx, { channel: "probe", sender: 1000, data: real });
        expect(received).toHaveLength(1);
    });

    test("request with a target is directed", () => {
        const sends: Sends = { broadcast: [], directed: [] };
        const { ctx } = fakeCtx("p4", sends);

        const plugin = definePlugin({
            onLoad(c: unknown) {
                void c;
            }
        });
        (plugin as unknown as { onLoad: (c: unknown, p: unknown) => void }).onLoad(ctx, undefined);

        void Messaging.request("probe", { q: 1 }, { target: 5, timeoutMs: 10 }).catch(() => {});
        expect(sends.directed).toHaveLength(1);
        expect(sends.broadcast).toHaveLength(0);
    });
});
