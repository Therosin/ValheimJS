import { definePlugin, log, Messaging } from "../../sdk/index";

function later(value: number, ms: number): Promise<number> {
    return new Promise((resolve) =>
        setTimeout(() => {
            log("asynccheck: timer fired");
            resolve(value);
        }, ms)
    );
}

export default definePlugin({
    version: "1.0.0",
    async onLoad() {
        log("asynccheck: before await");
        const value = await later(41, 100);
        log(`asynccheck: awaited ${value + 1}`);

        try {
            await Messaging.request("asynccheck:nobody", { ping: 1 }, { timeoutMs: 250 });
            log("asynccheck: unexpected reply");
        } catch (error) {
            log(`asynccheck: request failed as expected (${String(error).slice(0, 40)}…)`);
        }
    }
});
