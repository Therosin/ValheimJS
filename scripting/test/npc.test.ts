import { beforeEach, describe, expect, test } from "bun:test";
import { npc } from "../sdk/npc";

// The host injects `__vjs_context` around every invocation; tests install a fake at that boundary.
const runtime = globalThis as unknown as { __vjs_context?: unknown };

interface Fake {
    calls: unknown[];
}

function install(): Fake {
    const calls: unknown[] = [];
    const api = {
        available: () => true,
        base: () => class {},
        register: (...args: unknown[]) => calls.push(["register", ...args]),
        spawn: (...args: unknown[]) => {
            calls.push(["spawn", ...args]);
            return null;
        },
        despawn: (...args: unknown[]) => {
            calls.push(["despawn", ...args]);
            return true;
        },
        handles: (...args: unknown[]) => {
            calls.push(["handles", ...args]);
            return [];
        },
        perception: (...args: unknown[]) => {
            calls.push(["perception", ...args]);
            return null;
        },
        sensed: (...args: unknown[]) => {
            calls.push(["sensed", ...args]);
            return [];
        },
        memory: (...args: unknown[]) => {
            calls.push(["memory", ...args]);
            return { owner: "steam:1" };
        },
        setMemory: (...args: unknown[]) => {
            calls.push(["setMemory", ...args]);
            return true;
        }
    };

    runtime.__vjs_context = { name: "npc", host: { api: { npc: api } } };
    return { calls };
}

beforeEach(() => {
    runtime.__vjs_context = undefined;
});

describe("npc facade", () => {
    test("forwards registration options and the plugin subclass", () => {
        const fake = install();
        class Wanderer {}
        npc.register(Wanderer as never, {
            kind: "wanderer",
            body: "Dverger",
            tickMs: 750,
            faction: "Players",
            perception: { radius: 24, viewAngle: 90, objectRadius: 12 }
        });
        const call = fake.calls[0] as unknown[];
        expect(call[0]).toBe("register");
        expect(call[1]).toBe("wanderer");
        expect(call[2]).toBe("Dverger");
        expect(call[3]).toBe(750);
        expect(call[4]).toBe(Wanderer);
        expect(call[5]).toMatchObject({ faction: "Players", perception: { radius: 24, objectRadius: 12 } });
    });

    test("queries the perception store with the supplied filter", () => {
        const fake = install();
        const entity = {} as never;
        expect(npc.sensed(entity, { radius: 12, dir: { x: 0, z: 1 }, angle: 45, kinds: ["player"] })).toEqual([]);
        expect(fake.calls[0]).toEqual(["sensed", entity, { radius: 12, dir: { x: 0, z: 1 }, angle: 45, kinds: ["player"] }]);
        npc.sensed(entity);
        expect(fake.calls[1]).toEqual(["sensed", entity, {}]);
    });

    test("reads and writes durable memory", () => {
        const fake = install();
        const entity = {} as never;
        expect(npc.memory(entity)).toEqual({ owner: "steam:1" });
        const memory = { owner: "steam:1", interactions: { chest: 3 } };
        expect(npc.setMemory(entity, memory)).toBe(true);
        expect(fake.calls).toEqual([
            ["memory", entity],
            ["setMemory", entity, memory]
        ]);
    });
});
