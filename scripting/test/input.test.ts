import { beforeEach, describe, expect, test } from "bun:test";
import { input } from "../sdk/input";

function install() {
    const calls: unknown[] = [];
    const api = {
        bind: (...args: unknown[]) => calls.push(["bind", ...args]),
        unbind: (...args: unknown[]) => calls.push(["unbind", ...args]),
        on: (...args: unknown[]) => calls.push(["on", ...args]),
        off: (...args: unknown[]) => calls.push(["off", ...args]),
        clear: () => calls.push(["clear"]),
        state: (action: string) => ({ isPressed: action === "jump", wasPressedThisFrame: false, wasReleasedThisFrame: false }),
        rawState: () => null,
        handledAny: () => true
    };

    (globalThis as { __vjs_context?: unknown }).__vjs_context = { name: "keys", host: { api: { input: api } } };
    return calls;
}

beforeEach(() => {
    (globalThis as { __vjs_context?: unknown }).__vjs_context = undefined;
});

describe("input facade", () => {
    test("maps actions to the host bindings", () => {
        const calls = install();
        input.bind("jump", "Space");
        input.unbind("jump");
        expect(calls).toEqual([["bind", "jump", "Space"], ["unbind", "jump"]]);
    });

    test("wraps handlers so a truthy result consumes input", () => {
        const calls = install();
        input.on("jump", "press", () => true);
        const registered = calls[0] as unknown[];
        expect(registered[0]).toBe("on");
        expect(registered[1]).toBe("jump");
        expect(registered[2]).toBe("press");

        const handler = registered[3] as (action: string, phase: string) => unknown;
        expect(handler("jump", "press")).toBe(true);
    });

    test("exposes state and clears host registrations", () => {
        const calls = install();
        expect(input.state("jump")?.isPressed).toBe(true);
        expect(input.state("walk")?.isPressed).toBe(false);
        expect(input.handledAny()).toBe(true);
        input.clear();
        expect(calls[calls.length - 1]).toEqual(["clear"]);
    });
});
