import { definePlugin, log } from "../../../sdk/index";

// Loaded only by a process with a local player/UI (including a hosting player).
export default definePlugin({
    version: "1.0.0",
    onLoad(ctx) {
        log(`session (client) up; side=${ctx.host.api.env.side()} dedicated=${ctx.host.api.env.dedicated()}`);
    }
});
