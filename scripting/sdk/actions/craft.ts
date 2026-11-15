import { registerAction } from "./registry";

registerAction<{ craft: string }>("craft", {
    tick(context, config) {
        if (context.craft === null) {
            return { status: "failure", reason: "unsupported" };
        }

        const result = context.craft(config.craft);
        return result.ok
            ? "success"
            : {
                status: "failure",
                reason: result.reason ?? result.reasons[0]?.code ?? "craft-failed"
            };
    }
});
