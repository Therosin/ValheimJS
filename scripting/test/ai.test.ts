import { beforeEach, describe, expect, test } from "bun:test";
import { ai } from "../sdk/ai";
import type { AiContext, AiInstantiateOptions } from "../sdk/api/types";

// The host injects `__vjs_context` around every invocation; tests install a fake at that boundary.
const runtime = globalThis as unknown as { __vjs_context?: unknown };

interface Fake {
    calls: unknown[];
}

function install(): Fake {
    const calls: unknown[] = [];
    const api = {
        compile: (...args: unknown[]) => {
            calls.push(["compile", ...args]);
            return "tree1";
        },
        instantiate: (...args: unknown[]) => {
            calls.push(["instantiate", ...args]);
            return "brain1";
        },
        tick: (...args: unknown[]) => {
            calls.push(["tick", ...args]);
            return { status: "running", output: null };
        },
        state: (...args: unknown[]) => {
            calls.push(["state", ...args]);
            return { attempts: 2 };
        },
        set: (...args: unknown[]) => calls.push(["set", ...args]),
        status: (...args: unknown[]) => {
            calls.push(["status", ...args]);
            return "success";
        },
        cancel: (...args: unknown[]) => calls.push(["cancel", ...args]),
        reset: (...args: unknown[]) => calls.push(["reset", ...args]),
        release: (...args: unknown[]) => {
            calls.push(["release", ...args]);
            return true;
        },
        unload: (...args: unknown[]) => {
            calls.push(["unload", ...args]);
            return true;
        }
    };

    runtime.__vjs_context = { name: "ai", host: { api: { ai: api } } };
    return { calls };
}

beforeEach(() => {
    runtime.__vjs_context = undefined;
});

describe("ai facade", () => {
    test("compiles YAML and forwards import options", () => {
        const fake = install();
        const tree = ai.compile("root:\n  action: x\n", {
            source: "brain.yaml",
            imports: { child: "root:\n  action: y\n" },
            warn: () => {}
        });
        expect(tree).toBe("tree1");
        expect(fake.calls[0][0]).toBe("compile");
        expect(fake.calls[0][1]).toBe("root:\n  action: x\n");
        expect(fake.calls[0][2]).toMatchObject({ source: "brain.yaml", imports: { child: "root:\n  action: y\n" } });
    });

    test("forwards callbacks and host options to instantiate", () => {
        const fake = install();
        const host = { tag: "npc" };
        const options: AiInstantiateOptions = {
            this: host,
            state: { extra: 1 },
            action: (name: string, ctx: AiContext) => (name === "move" && ctx.state.extra ? "running" : "success"),
            condition: () => true,
            random: () => 0.5,
            maxJumpsPerTick: 8
        };
        expect(ai.instantiate("tree1", options)).toBe("brain1");
        expect(fake.calls[0][0]).toBe("instantiate");
        expect(fake.calls[0][1]).toBe("tree1");
        expect(fake.calls[0][2]).toBe(options);
    });

    test("ticks with an explicit or default delta and returns the host result", () => {
        const fake = install();
        expect(ai.tick("brain1", 0.5)).toEqual({ status: "running", output: null });
        expect(fake.calls[0]).toEqual(["tick", "brain1", 0.5]);
        ai.tick("brain1");
        expect(fake.calls[1]).toEqual(["tick", "brain1", 0.05]);
    });

    test("reads, writes and reports state and status", () => {
        const fake = install();
        expect(ai.state("brain1")).toEqual({ attempts: 2 });
        ai.set("brain1", "extra", 9);
        expect(ai.status("brain1")).toBe("success");
        expect(fake.calls).toEqual([
            ["state", "brain1"],
            ["set", "brain1", "extra", 9],
            ["status", "brain1"]
        ]);
    });

    test("forwards lifecycle operations with the reset default", () => {
        const fake = install();
        ai.cancel("brain1");
        ai.reset("brain1");
        ai.reset("brain1", true);
        expect(ai.release("brain1")).toBe(true);
        expect(ai.unload("tree1")).toBe(true);
        expect(fake.calls).toEqual([
            ["cancel", "brain1"],
            ["reset", "brain1", false],
            ["reset", "brain1", true],
            ["release", "brain1"],
            ["unload", "tree1"]
        ]);
    });
});
