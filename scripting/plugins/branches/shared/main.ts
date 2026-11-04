import { definePlugin, serverOnly, clientOnly, authorityOnly, game, log } from "../../../sdk/index";

// Context and process gates are already available during bundle evaluation.
serverOnly(ctx => { ctx.state.process = "server"; });
clientOnly(ctx => { ctx.state.process = "client"; });

export default definePlugin({
    exports: { label: "shared code" },
    setup(ctx) {
        ctx.state.events = 0;
        log(`shared initialized on ${ctx.state.process}`);
        log(`startup side=${game().side()} session=${game().sessionReady()} authority=${game().isServer()}`);
    },
    onServerTick(ctx) {
        ctx.state.events = Number(ctx.state.events) + 1;
        const status = `side=${game().side()} session=${game().sessionReady()} authority=${game().isServer()}`;
        if (ctx.state.sessionStatus !== status) {
            ctx.state.sessionStatus = status;
            log(status);
        }
        authorityOnly(active => { active.state.hasHosted = true; });
    },
    teardown() { log("shared stopped"); }
});
