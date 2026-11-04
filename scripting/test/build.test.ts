import { afterEach, expect, test } from "bun:test";
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, existsSync, rmSync, copyFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const roots: string[] = [];
afterEach(() => {
    for (const root of roots.splice(0)) {
        if (root.startsWith(join(tmpdir(), "valheimjs-build-"))) rmSync(root, { recursive: true, force: true });
    }
});

test("builds independent entries and removes deleted branch output", async () => {
    const root = mkdtempSync(join(tmpdir(), "valheimjs-build-"));
    roots.push(root);
    copyFileSync(resolve(import.meta.dir, "../build.ts"), join(root, "build.ts"));
    const source = join(root, "plugins", "sample");
    for (const side of ["shared", "server", "client"]) {
        mkdirSync(join(source, side), { recursive: true });
        writeFileSync(join(source, side, "main.ts"), `export default {__vjsPlugin:'object', value:{exports:{branch:'${side}'}}};`);
    }
    const build = async () => {
        const child = Bun.spawn([process.execPath, "run", join(root, "build.ts")], { cwd: root, stdout: "pipe", stderr: "pipe" });
        const [exit, error] = await Promise.all([child.exited, new Response(child.stderr).text()]);
        expect(error).toBe("");
        expect(exit).toBe(0);
    };
    await build();
    const output = join(root, "dist", "sample");
    for (const side of ["shared", "server", "client"]) {
        const code = readFileSync(join(output, `${side}.js`), "utf8");
        expect(code).not.toMatch(/^\s*(import|export)\s/m);
        const target: { __vjs_plugin?: { value: { exports: { branch: string } } } } = {};
        new Function("globalThis", code)(target);
        expect(target.__vjs_plugin?.value.exports.branch).toBe(side);
    }
    expect(readFileSync(join(output, "manifest.ini"), "utf8")).toContain("apiVersion=1");
    expect(readFileSync(join(output, "manifest.ini"), "utf8")).not.toContain("entry=");
    rmSync(join(source, "client", "main.ts"));
    await build();
    expect(existsSync(join(output, "client.js"))).toBe(false);
    expect(existsSync(join(output, "shared.js"))).toBe(true);
});
