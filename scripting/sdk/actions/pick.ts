import { registerAction } from "./registry";

registerAction<{ pick: string }>("pick", {
    tick(context, config) {
        if (context.awaiting) {
            const settlement = context.takeSettled();
            if (settlement === null) return "running";
            return settlement.status === "arrived"
                ? "success"
                : { status: "failure", reason: settlement.reason ?? settlement.status };
        }

        const result = context.issue("pick", () => context.actor.pick(config.pick));
        return result !== undefined && !result.ok
            ? { status: "failure", reason: result.reason ?? "rejected" }
            : "running";
    }
});

registerAction<{ stop: true }>("stop", {
    tick(context) {
        context.actor.stop();
        return "success";
    }
});

registerAction<{ idle: true }>("idle", {
    tick(context) {
        context.actor.idle();
        return "success";
    }
});
