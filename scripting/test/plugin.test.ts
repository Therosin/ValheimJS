import { describe, expect, test } from "bun:test";
import { definePlugin, JsPlugin } from "../sdk/index";

describe("plugin descriptor", () => {
    test("object plugins keep their value for host-side discovery", () => {
        const plugin = { onChat: () => {}, exports: { ping: "pong" } };
        expect(definePlugin(plugin)).toEqual({ __vjsPlugin: "object", value: plugin });
    });

    test("class plugins export the constructor only", () => {
        class Greeter extends JsPlugin {
            greet() {
                return "hi";
            }
        }

        const descriptor = definePlugin(Greeter);
        expect(descriptor.__vjsPlugin).toBe("class");
        if (descriptor.__vjsPlugin !== "class") throw new Error("unreachable");
        expect(new descriptor.ctor()).toBeInstanceOf(Greeter);
    });

    test("rejects anything else", () => {
        expect(() => definePlugin(null as unknown as object)).toThrow("class or a plain object");
    });
});
