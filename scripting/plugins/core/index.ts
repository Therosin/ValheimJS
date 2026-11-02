import { definePlugin, log } from "../../sdk/index";

let calls = 0;

export default definePlugin({
    version: "0.1.0",
    exports: {
        greet(name: string): string {
            calls += 1;
            return `hello ${name} (call #${calls})`;
        },
        get callCount(): number {
            return calls;
        }
    },
    onLoad(ctx) {
        log(`core loaded as '${ctx.name}', dir=${ctx.dir}`);
    }
});
