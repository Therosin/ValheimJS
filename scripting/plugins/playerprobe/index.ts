import { definePlugin, log } from "../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    onPlayerSpawned(ctx, player) {
        const stats = ctx.host.api.player.stats(player.uid);
        log(`stats: ${JSON.stringify(stats)}`);

        const inv = ctx.host.api.inventory.list(player.uid);
        log(`inventory: ${inv ? inv.length : "null"} stacks, wood=${ctx.host.api.inventory.count(player.uid, "Wood")}`);

        const prefabs = ctx.host.api.spawn.prefabs("Boar", 5);
        log(`prefabs matching 'Boar': ${prefabs.join(", ")}`);

        setTimeout(() => {
            const later = ctx.host.api.inventory.list(player.uid);
            log(`inventory after sync: ${later ? later.length + " stacks" : "null"}, wood=${ctx.host.api.inventory.count(player.uid, "Wood")}`);
            const steam = ctx.host.api.players.steamId(player.uid);
            log(`steamId: ${steam ?? "none"} platformId: ${ctx.host.api.players.platformId(player.uid) ?? "none"}`);
        }, 8_000);

        if (stats) {
            // Round trip a mutation that changes nothing: the owning client should apply it.
            ctx.host.api.player.setHealth(player.uid, stats.health);
            log(`sent setHealth(${stats.health}) for ${player.name}`);
        }
    }
});
