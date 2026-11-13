import { beforeEach, describe, expect, test } from "bun:test";
import { ui } from "../sdk/ui";

const runtime = globalThis as unknown as { __vjs_context?: unknown };

function install() {
    const calls: unknown[] = [];
    const api = {
        define: (...args: unknown[]) => {
            calls.push(["define", ...args]);
            return "doc1";
        },
        open: (...args: unknown[]) => {
            calls.push(["open", ...args]);
            return "panel1";
        },
        close: (...args: unknown[]) => calls.push(["close", ...args]),
        invalidate: (...args: unknown[]) => calls.push(["invalidate", ...args])
    };
    runtime.__vjs_context = { name: "ui", host: { api: { ui: api } } };
    return { calls };
}

beforeEach(() => {
    runtime.__vjs_context = undefined;
});

describe("ui facade", () => {
    test("forwards panels, imports and open callbacks", () => {
        const fake = install();
        const options = {
            mount: "overlay" as const,
            modal: true,
            get: (key: string) => key,
            set: () => {},
            onEvent: () => {}
        };
        expect(ui.define("panel: { type: panel }", { source: "x.yaml", imports: { widgets: "components: {}" } })).toBe("doc1");
        expect(ui.open("doc1", options)).toBe("panel1");
        expect(fake.calls[0]).toEqual([
            "define",
            "panel: { type: panel }",
            { source: "x.yaml", imports: { widgets: "components: {}" } }
        ]);
        expect(fake.calls[1]).toEqual(["open", "doc1", options]);
    });

    test("forwards lifecycle operations", () => {
        const fake = install();
        ui.close("panel1");
        ui.invalidate("panel1");
        expect(fake.calls).toEqual([
            ["close", "panel1"],
            ["invalidate", "panel1"]
        ]);
    });
});
