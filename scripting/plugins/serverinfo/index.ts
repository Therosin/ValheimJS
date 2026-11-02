import { definePlugin, log } from "../../sdk/index";

const STATUS_FILE = "serverinfo/status.json";

export default definePlugin({
    version: "0.1.0",
    onLoad(ctx) {
        const fs = ctx.host.api.fs;
        const previous = fs.read(STATUS_FILE);
        log(`status file ${previous ? "found" : "missing"} at ${fs.root()}`);

        setInterval(() => {
            const world = ctx.host.api.world.snapshot();
            const zdo = ctx.host.api.zdo.count();
            const players = ctx.host.api.players.list();
            fs.write(STATUS_FILE, JSON.stringify({ ...world, zdo, players, updated: Date.now() }, null, 2));
            ctx.state.writes = ((ctx.state.writes as number) ?? 0) + 1;
            if ((ctx.state.writes as number) % 5 === 0) {
                log(`wrote status #${ctx.state.writes} (${world.timeOfDay}, day ${world.day}, zdo=${zdo}, players=${players.length})`);
            }
        }, 5000);
    }
});
