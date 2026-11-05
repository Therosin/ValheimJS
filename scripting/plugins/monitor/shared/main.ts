/**
 * Layered observation: simulated sensor -> threshold -> stabilised condition -> alarm.
 * Nothing here runs on every tick; the host samples the conditions and only the edges are logged.
 */
import { all, definePlugin, log, signals, stable, type PluginContext } from "../../../sdk/index";

let heat = 0;

export default definePlugin({
    version: "1.0.0",

    setup(ctx: PluginContext) {
        setInterval(() => {
            heat = (heat + 1) % 4;
        }, 500);

        const sensor = signals({ intervalMs: 250 });
        sensor.observe("hot", () => heat >= 2);
        sensor.observe("steadyHot", stable(() => sensor.is("hot"), 2, 2));
        sensor.observe("alarm", all(() => sensor.is("steadyHot"), () => sensor.is("hot")));

        sensor.on("alarm:Enter", () => log("alarm raised"));
        sensor.on("alarm", () => {
            ctx.state.samples = Number(ctx.state.samples ?? 0) + 1;
        });
        sensor.on("alarm:Exit", () => log("alarm cleared"));

        ctx.state.dispose = () => sensor.dispose();
    },

    teardown(ctx: PluginContext) {
        const dispose = ctx.state.dispose as (() => void) | undefined;
        dispose?.();
    }
});
