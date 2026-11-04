/**
 * Smallest useful object-form plugin: an export, a setup hook and a cheap heartbeat.
 * Everything it touches (`log`, `ctx.state`, `ctx.host.now`) is host-provided.
 */
import { definePlugin, log, type PluginContext } from "../../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    exports: {
        greet: (name: string) => `hello ${name}`
    },

    setup(ctx: PluginContext) {
        log(`hello ready in ${ctx.paths.data}`);
    },

    onServerTick(ctx: PluginContext) {
        const ticks = Number(ctx.state.ticks ?? 0) + 1;
        ctx.state.ticks = ticks;
        if (ticks % 10 === 0) {
            log(`tick ${ticks} at ${ctx.host.now()}`);
        }
    }
});
