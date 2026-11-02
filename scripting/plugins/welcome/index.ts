import { definePlugin, defineCommands, log } from "../../sdk/index";

export default definePlugin({
    version: "0.1.0",
    ...defineCommands("!", {
        ping: { description: "health check", run: () => "pong" },
        online: { description: "how many players are connected", run: (ctx) => `${ctx.host.api.players.count()} online` },
        day: { description: "current in-game day", run: (ctx) => `day ${ctx.host.api.world.day()}` }
    }),
    onPlayerJoin(ctx, player) {
        log(`${player.name} joined (uid ${player.uid})`);
        ctx.host.api.chat.send(`Welcome ${player.name}! Type !help for commands.`);
    },
    onPlayerLeave(ctx, player) {
        log(`${player.name} left (uid ${player.uid})`);
    }
});
