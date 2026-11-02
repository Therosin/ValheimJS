import { definePlugin } from "../../sdk/index";

const FILE = "status.json";

export default definePlugin({
    version: "1.0.0",
    onLoad(ctx) {
        const publish = () => {
            const world = ctx.host.api.world.snapshot();
            ctx.host.api.fs.write(FILE, JSON.stringify({
                ...world,
                players: ctx.host.api.players.list(),
                objects: ctx.host.api.zdo.count(),
                updated: Date.now()
            }, null, 2));
        };

        publish();
        setInterval(publish, 30_000);
    }
});
