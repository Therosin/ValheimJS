import { definePlugin, JsPlugin, log } from "../../../sdk/index";

class Client extends JsPlugin {
    setup() { log(`client initialized after ${this.ctx.exports.label}`); }
    teardown() { log("client stopped before shared"); }
}

export default definePlugin(Client);
