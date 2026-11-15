import { registerAction, type ActionContext, type ActionOutcome } from "./registry";

registerAction<{ until: (context: ActionContext) => boolean }>("until", {
    tick(context, config) {
        return config.until(context) ? "success" : "running";
    }
});

registerAction<{ call: (context: ActionContext) => ActionOutcome | void }>("call", {
    tick(context, config) {
        const outcome = config.call(context);
        if (typeof outcome === "string") return outcome;
        if (typeof outcome === "object" && outcome !== null) return outcome;
        return "success";
    }
});
