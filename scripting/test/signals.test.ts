import { beforeEach, describe, expect, test } from "bun:test";
import { signals } from "../sdk/signals";

// The host injects `__vjs_context` around every invocation; tests install a fake at that boundary.
const runtime = globalThis as unknown as { __vjs_context?: unknown };

interface Fake {
    calls: unknown[];
    values: Map<string, boolean>;
    handlers: Array<() => void>;
}

function install(): Fake {
    const calls: unknown[] = [];
    const values = new Map<string, boolean>();
    const handlers: Array<() => void> = [];
    const api = {
        create: (ms: number) => {
            calls.push(["create", ms]);
            return 7;
        },
        observe: (id: number, name: string, predicate: () => boolean) => {
            calls.push(["observe", id, name]);
            values.set(name, predicate());
        },
        on: (id: number, event: string, handler: () => void) => {
            calls.push(["on", id, event]);
            handlers.push(handler);
            return 11;
        },
        once: (id: number, event: string, handler: () => void) => calls.push(["once", id, event]),
        off: (...args: unknown[]) => calls.push(["off", ...args]),
        is: (_id: number, name: string) => values.get(name) === true,
        changed: (_id: number, name: string) => name === "changed",
        snapshot: (_id: number) => ({ flag: values.get("flag") === true }),
        remove: (...args: unknown[]) => calls.push(["remove", ...args]),
        reset: (...args: unknown[]) => calls.push(["reset", ...args]),
        clear: (...args: unknown[]) => calls.push(["clear", ...args]),
        dispose: (...args: unknown[]) => calls.push(["dispose", ...args])
    };

    runtime.__vjs_context = { name: "signals", host: { api: { signals: api } } };
    return { calls, values, handlers };
}

beforeEach(() => {
    runtime.__vjs_context = undefined;
});

describe("signals facade", () => {
    test("creates an observer with the default and explicit interval", () => {
        const first = install();
        signals();
        expect(first.calls[0]).toEqual(["create", 250]);

        runtime.__vjs_context = undefined;
        const second = install();
        signals({ intervalMs: 50 });
        expect(second.calls[0]).toEqual(["create", 50]);
    });

    test("defines conditions and stores the predicate result", () => {
        const fake = install();
        const s = signals();
        s.observe("flag", () => true);
        expect(fake.calls[1]).toEqual(["observe", 7, "flag"]);
        expect(fake.values.get("flag")).toBe(true);
    });

    test("on returns an unsubscribe closure bound to the subscription id", () => {
        const fake = install();
        const s = signals();
        const off = s.on("flag:Enter", () => {});
        expect(fake.calls[1]).toEqual(["on", 7, "flag:Enter"]);
        expect(fake.handlers.length).toBe(1);
        off();
        expect(fake.calls[2]).toEqual(["off", 7, 11]);
    });

    test("once delegates without a subscription id", () => {
        const fake = install();
        signals().once("flag:Exit", () => {});
        expect(fake.calls[1]).toEqual(["once", 7, "flag:Exit"]);
    });

    test("exposes sampled values and snapshots", () => {
        const fake = install();
        const s = signals();
        fake.values.set("a", true);
        expect(s.is("a")).toBe(true);
        expect(s.changed("changed")).toBe(true);
        expect(s.snapshot()).toEqual({ flag: false });
    });

    test("forwards lifecycle operations to the host", () => {
        const fake = install();
        const s = signals();
        s.remove("a");
        s.reset("a");
        s.clear();
        s.dispose();
        expect(fake.calls.slice(1)).toEqual([
            ["remove", 7, "a"],
            ["reset", 7, "a"],
            ["clear", 7],
            ["dispose", 7]
        ]);
    });
});
