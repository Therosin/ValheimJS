import { afterEach, describe, expect, test } from "bun:test";
import { Sync, decodeFrame, diffEntity, encodeFrame, quantise } from "../lib/sync";
import type { PluginContext } from "../sdk/runtime";

const globals = globalThis as { __vjs_context?: PluginContext };
afterEach(() => { delete globals.__vjs_context; });
function install() {
    const calls: unknown[][] = [];
    const entities = [{ id: 7, x: 1.5 }];
    const api = {
        net: { everybody: () => 0 },
        sync: {
            on: (...args: unknown[]) => { calls.push(["on", ...args]); return 8; },
            off: (id: number) => calls.push(["off", id]),
            send: (...args: unknown[]) => calls.push(["send", ...args]),
            encode: (json: string) => { calls.push(["encode", json]); return "frame"; },
            decode: (frame: string) => { calls.push(["decode", frame]); return entities; },
            diff: (...args: unknown[]) => { calls.push(["diff", ...args]); return null; },
            quantise: (...args: unknown[]) => { calls.push(["quantise", ...args]); return 1.23; }
        }
    };
    globals.__vjs_context = { host: { api } } as unknown as PluginContext;
    return { calls, entities };
}
describe("native sync facade", () => {
    test("delegates codec and delta work to the host", () => {
        const { calls, entities } = install();
        expect(encodeFrame(entities)).toBe("frame");
        expect(decodeFrame("frame")).toBe(entities);
        expect(diffEntity({ id: 7 }, entities[0])).toBeNull();
        expect(quantise(1.23456)).toBe(1.23);
        expect(calls).toEqual([
            ["encode", JSON.stringify(entities)], ["decode", "frame"],
            ["diff", '{"id":7}', JSON.stringify(entities[0]), 0.01], ["quantise", 1.23456, 0.01]
        ]);
    });
    test("sends a frame through the binary service and preserves explicit targets", () => {
        const { calls, entities } = install();
        Sync.send("positions", entities);
        Sync.send("positions", entities, 42);
        expect(calls).toEqual([["send", 0, "positions", JSON.stringify(entities)], ["send", 42, "positions", JSON.stringify(entities)]]);
    });
    test("adapts callbacks and returns native unsubscription", () => {
        const { calls, entities } = install();
        let received: unknown;
        const off = Sync.on("positions", (frame, meta) => { received = [frame, meta]; });
        const callback = calls[0][2] as (frame: unknown, meta: unknown) => void;
        const meta = { sender: 42, channel: "ValheimJS:Sync", event: "positions" };
        callback(entities, meta);
        expect(received).toEqual([entities, meta]);
        off();
        expect(calls[1]).toEqual(["off", 8]);
    });
});
