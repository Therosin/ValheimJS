import { definePlugin, log } from "../../sdk/index";

export default definePlugin({
    version: "0.0.2",
    onLoad(ctx) {
        log(`chatlog watching ${JSON.stringify(ctx.host.api.world.snapshot())}`);
    },
    onServerTick(ctx, world: { world: string | null; peers: number; day: number }) {
        ctx.state.ticks = ((ctx.state.ticks as number) ?? 0) + 1;
        if ((ctx.state.ticks as number) % 10 === 0) {
            log(`heartbeat #${ctx.state.ticks} world=${world.world} peers=${world.peers} day=${world.day}`);
        }
    },
    onChat(ctx, payload: { channel: string; senderId: number; text: string }) {
        log(`[${payload.channel}] ${payload.senderId}: ${payload.text}`);
        ctx.state.lastChat = payload;
    }
});
