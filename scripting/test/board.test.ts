import { afterEach, describe, expect, test } from "bun:test";
import { ai } from "../sdk/ai";

const calls: Array<[string, ...unknown[]]> = [];

function installContext(): void {
    const aiApi = {
        boardGet: (_key: string, _channel: string | null) => null,
        boardSet: (_key: string, _value: unknown, channel: string | null) => {
            calls.push(["set", channel]);
        },
        boardHas: (_key: string, _channel: string | null) => false,
        boardSnapshot: (_channel: string | null) => ({}),
        boardEmit: (_name: string, _data: unknown, channel: string | null) => {
            calls.push(["emit", channel]);
        },
        boardOnChange: (_key: string, _handler: unknown, channel: string | null) => {
            calls.push(["change", channel]);
            return 1;
        },
        boardOnSignal: (_name: string, _handler: unknown, channel: string | null) => {
            calls.push(["signal", channel]);
            return 2;
        },
        boardUse: (_handler: unknown, channel: string | null, interceptor: boolean) => {
            calls.push(["use", channel, interceptor]);
            return 3;
        },
        boardDrop: (channel: string | null) => {
            calls.push(["drop", channel]);
            return true;
        },
        boardOff: (id: number) => {
            calls.push(["off", id]);
            return true;
        }
    };

    Object.defineProperty(globalThis, "__vjs_context", {
        value: { host: { api: { ai: aiApi } } },
        configurable: true
    });
}

afterEach(() => {
    Reflect.deleteProperty(globalThis, "__vjs_context");
    calls.length = 0;
});

describe("board scope SDK", () => {
    test("encodes local, shared and membership lifetimes", () => {
        installContext();

        const local = ai.board.local("npc:1");
        const shared = ai.board.shared("village:oak");
        const membership = ai.board.join("village:oak");

        local.set("activity", "wander");
        shared.emit("alarm", true);
        expect(calls).toContainEqual(["set", "@local/npc:1"]);
        expect(calls).toContainEqual(["emit", "@shared/village:oak"]);

        membership.onChange("", () => {});
        membership.onSignal("alarm", () => {});
        expect(calls).toContainEqual(["change", "@shared/village:oak"]);
        expect(calls).toContainEqual(["signal", "@shared/village:oak"]);
        membership.dispose();
        expect(calls.filter(call => call[0] === "off")).toEqual([
            ["off", 1],
            ["off", 2]
        ]);

        const intercept = local.intercept(() => {});
        intercept.dispose();
        expect(calls).toContainEqual(["use", "@local/npc:1", true]);
        expect(calls).toContainEqual(["off", 3]);

        expect(local.release()).toBe(true);
        expect(calls).toContainEqual(["drop", "@local/npc:1"]);
        expect(shared.release()).toBe(false);
    });
});
