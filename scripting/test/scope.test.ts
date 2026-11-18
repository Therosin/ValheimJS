import { afterEach, describe, expect, test } from "bun:test";
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const roots: string[] = [];

afterEach(() => {
    for (const root of roots.splice(0)) {
        if (root.startsWith(join(tmpdir(), "valheimjs-scope-"))) {
            rmSync(root, { recursive: true, force: true });
        }
    }
});

const SOURCE = `
import { context, database, definePlugin } from "../../../sdk/index";

export default definePlugin({
    exports: {
        write() {
            database().use("items").save({ id: "1", value: "x" });
        },
        who() {
            return context().name;
        }
    }
});
`;

type FakeContext = { name: string; host: { api: { db: { put: (collection: string, id: string, json: string) => void } } } };

function contextFor(name: string, calls: string[]): FakeContext {
    return {
        name,
        host: {
            api: {
                db: {
                    put: (collection, id, json) => calls.push(`${name}:${collection}:${id}:${json}`)
                }
            }
        }
    };
}

async function bundle(root: string, name: string): Promise<string> {
    const source = join(root, "plugins", name, "shared", "main.ts");
    mkdirSync(join(root, "plugins", name, "shared"), { recursive: true });
    writeFileSync(source, SOURCE, "utf8");

    const wrapper = join(root, `${name}.entry.ts`);
    writeFileSync(
        wrapper,
        `import plugin from ${JSON.stringify(source.replaceAll("\\", "/"))};\n` +
            `globalThis.__vjs_plugin = plugin;\n`,
        "utf8"
    );

    const outdir = join(root, "out", name);
    const result = await Bun.build({
        entrypoints: [wrapper],
        outdir,
        target: "browser",
        format: "iife",
        naming: "shared.js",
        minify: false,
        sourcemap: "none"
    });
    if (!result.success) {
        throw new Error(result.logs.map(log => String(log)).join("\n"));
    }

    return readFileSync(join(outdir, "shared.js"), "utf8");
}

describe("plugin scope", () => {
    test("an exported function keeps the provider's context and API", async () => {
        const root = mkdtempSync(join(tmpdir(), "valheimjs-scope-"));
        roots.push(root);
        cpSync(resolve(import.meta.dir, "../sdk"), join(root, "sdk"), { recursive: true });

        const calls: string[] = [];
        const globalObject: Record<string, unknown> = {};
        const evaluate = (code: string, ctx: FakeContext) => {
            globalObject.__vjs_context = ctx;
            new Function("globalThis", code)(globalObject);
            const descriptor = globalObject.__vjs_plugin as {
                value: { exports: { write(): void; who(): string } };
            };
            return descriptor.value.exports;
        };

        const a = evaluate(await bundle(root, "a"), contextFor("a", calls));
        const b = evaluate(await bundle(root, "b"), contextFor("b", calls));

        // The host restores the caller's context before a cross-plugin call. The provider's SDK
        // copy must still resolve its own API.
        globalObject.__vjs_context = contextFor("caller", calls);
        a.write();
        b.write();

        expect(calls).toEqual([
            `a:items:1:{"id":"1","value":"x"}`,
            `b:items:1:{"id":"1","value":"x"}`
        ]);
        expect(a.who()).toBe("a");
        expect(b.who()).toBe("b");
    });
});
