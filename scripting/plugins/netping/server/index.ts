import { definePlugin, log, Messaging } from "../../../sdk/index";

interface TimeRequest { day: number }
interface TimeReply { side: string; peers: number }

export default definePlugin({
    version: "1.1.0",
    onLoad(ctx) {
        // Event from a client.
        Messaging.handle<{ from: string }>("hello", (envelope) => {
            log(`client ${envelope.sender} says ${JSON.stringify(envelope.data)}`);
        });

        // Request/reply every 20 s, awaited inside an async timer.
        setInterval(() => {
            void (async () => {
                try {
                    const reply = await Messaging.request<TimeRequest, TimeReply>(
                        "server-time",
                        { day: ctx.host.api.world.day() },
                        { timeoutMs: 5_000 }
                    );
                    log(`reply from ${reply.side}: peers=${reply.peers}`);
                } catch (error) {
                    ctx.state.timeouts = ((ctx.state.timeouts as number) ?? 0) + 1;
                    log(`no reply (${ctx.state.timeouts}): ${String(error).slice(0, 60)}`);
                }
            })();
        }, 20_000);
    }
});
