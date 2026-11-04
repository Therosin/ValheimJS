import { afterEach, describe, expect, test } from "bun:test";
import {
    authorityHandler, authorityOnly, clientHandler, clientOnly,
    serverHandler, serverOnly, type PluginContext
} from "../sdk/runtime";

const globals = globalThis as { __vjs_context?: PluginContext | null };
afterEach(() => { delete globals.__vjs_context; });

function install(side: "server" | "client") {
    let ready = false;
    let authority = false;
    const ctx = {
        host: { api: { game: {
            side: () => side,
            sessionReady: () => ready,
            isServer: () => ready && authority
        } } }
    } as unknown as PluginContext;
    globals.__vjs_context = ctx;
    return { ctx, session: (server: boolean) => { ready = true; authority = server; }, leave: () => { ready = false; authority = false; } };
}

describe("process gates", () => {
    test.each(["client", "server"] as const)("immediate gates return matching results on %s", side => {
        const { ctx } = install(side);
        const calls: string[] = [];
        expect(serverOnly(active => { expect(active).toBe(ctx); calls.push("server"); return 11; })).toBe(side === "server" ? 11 : undefined);
        expect(clientOnly(active => { expect(active).toBe(ctx); calls.push("client"); return 22; })).toBe(side === "client" ? 22 : undefined);
        expect(calls).toEqual([side]);
        expect(authorityOnly(() => 33)).toBeUndefined();
    });

    test("hosting and leaving a world changes authority without switching process gates", () => {
        const session = install("client");
        const calls: string[] = [];
        const onClient = clientHandler<number>((ctx, payload) => { expect(ctx).toBe(session.ctx); calls.push(`client:${payload}`); });
        const onServer = serverHandler(() => { calls.push("server"); });
        const onAuthority = authorityHandler(() => { calls.push("authority"); });
        onAuthority(session.ctx, undefined);
        session.session(true);
        expect(authorityOnly(() => "host")).toBe("host");
        expect(clientOnly(() => "client")).toBe("client");
        expect(serverOnly(() => "server")).toBeUndefined();
        onClient(session.ctx, 7);
        onServer(session.ctx, undefined);
        onAuthority(session.ctx, undefined);
        session.leave();
        onAuthority(session.ctx, undefined);
        expect(calls).toEqual(["client:7", "authority"]);
    });

    test("a dedicated process has no authority until a session exists", () => {
        const session = install("server");
        expect(serverOnly(() => 1)).toBe(1);
        expect(authorityOnly(() => 2)).toBeUndefined();
        session.session(true);
        expect(authorityOnly(() => 2)).toBe(2);
    });

    test("missing context is an actionable error", () => {
        expect(() => serverOnly(() => {})).toThrow("no plugin context");
    });

    test("handler wrappers preserve matching return values", () => {
        const session = install("client");
        expect(clientHandler((_ctx, value: number) => value * 2)(session.ctx, 7)).toBe(14);
        expect(serverHandler(() => "wrong")(session.ctx, undefined)).toBeUndefined();
        session.session(true);
        expect(authorityHandler(() => "host")(session.ctx, undefined)).toBe("host");
    });
});
