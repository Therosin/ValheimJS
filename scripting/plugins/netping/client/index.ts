import { definePlugin, log, Messaging } from "../../../sdk/index";

interface TimeRequest { day: number }
interface TimeReply { side: string; peers: number }

export default definePlugin({
    version: "1.1.0",
    onLoad(ctx) {
        Messaging.handle<TimeRequest>("server-time", (envelope) => {
            log(`server asks: day ${envelope.data.day}`);
            Messaging.reply<TimeReply>(envelope, {
                side: ctx.host.api.env.side(),
                peers: ctx.host.api.env.peers()
            });
            Messaging.send("hello", { from: ctx.name });
        });
    }
});
