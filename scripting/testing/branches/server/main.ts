import { definePlugin, log } from "../../../sdk/index";

export default definePlugin({
    setup(ctx) { log(`server initialized after ${ctx.exports.label}`); },
    teardown() { log("server stopped before shared"); }
});
