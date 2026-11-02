import { definePlugin, log } from "../../../sdk/index";

// Loaded only by a dedicated server process.
export default definePlugin({
    version: "1.0.0",
    onLoad(ctx) {
        log(`session (server) up; side=${ctx.host.api.env.side()} dedicated=${ctx.host.api.env.dedicated()}`);
    },
    onPlayerJoin(ctx, player) {
        log(`+ ${player.name} (${player.uid})`);
    },
    onPlayerLeave(ctx, player) {
        log(`- ${player.name} (${player.uid})`);
    }
});
