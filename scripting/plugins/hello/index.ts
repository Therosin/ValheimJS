import { definePlugin, log } from "../../sdk/index";

export default definePlugin({
    version: "0.0.1",
    onLoad(ctx) {
        const core = ctx.dep<{ greet(name: string): string; callCount: number }>("core");
        log(`hello depends on core → ${core.greet("world")}`);
        log(`host info: ${JSON.stringify(ctx.host.info())}`);

        let ticks = 0;
        setInterval(() => {
            ticks += 1;
            if (ticks % 5 === 0) {
                log(`tick ${ticks} (core calls: ${core.callCount})`);
            }
        }, 1000);
    },
    onTick() {
        // intentionally quiet: per-tick work is hot
    },
    onUnload(ctx) {
        log("bye");
    }
});
