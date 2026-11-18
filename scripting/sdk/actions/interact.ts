import { registerAction } from "./registry";
import type { NpcInteractOptions } from "../api/types";

interface InteractStep extends NpcInteractOptions {
    target: string;
    approach?: boolean | { distance?: number; run?: boolean };
}

interface InteractConfig {
    interact: InteractStep;
}

registerAction<InteractConfig>("interact", {
    tick(context, config) {
        const step = config.interact;
        const approach = step.approach ?? true;
        const enabled = approach !== false;
        const options = typeof approach === "object" ? approach : {};
        const distance = options.distance ?? 3;

        if (context.awaiting) {
            const settlement = context.takeSettled();
            if (settlement === null) return "running";
            if (settlement.status !== "arrived") {
                return { status: "failure", reason: settlement.reason ?? settlement.status };
            }
        }

        if (context.actor.interact === undefined) {
            return { status: "failure", reason: "unsupported" };
        }

        if (enabled) {
            const entry = context.sense(step.target);
            if (entry === null) {
                return { status: "failure", reason: "target-missing" };
            }

            if (entry.distance > distance) {
                const attempts = (context.local.attempts as number | undefined) ?? 0;
                if (attempts >= 3) {
                    return { status: "failure", reason: "out-of-range" };
                }

                context.local.attempts = attempts + 1;
                const result = context.issue("moveTo", () =>
                    context.actor.moveTo(entry.pos.x, entry.pos.y, entry.pos.z, options.run ?? true));
                return result !== undefined && !result.ok
                    ? { status: "failure", reason: result.reason ?? "rejected" }
                    : "running";
            }
        }

        const result = context.actor.interact(step.target, step);
        if (result.ok) return "success";

        if (result.reason === "not-owner" || result.reason === "not-loaded") {
            const retries = (context.local.retries as number | undefined) ?? 0;
            if (retries < 30) {
                context.local.retries = retries + 1;
                return "running";
            }
        }

        if (enabled && result.reason === "out-of-range") {
            const attempts = (context.local.attempts as number | undefined) ?? 0;
            if (attempts < 3) {
                context.local.attempts = attempts + 1;
                const again = context.sense(step.target);
                if (again !== null) {
                    const moved = context.issue("moveTo", () =>
                        context.actor.moveTo(again.pos.x, again.pos.y, again.pos.z, options.run ?? true));
                    return moved !== undefined && !moved.ok
                        ? { status: "failure", reason: moved.reason ?? "rejected" }
                        : "running";
                }
            }
        }

        return { status: "failure", reason: result.reason ?? "interaction-failed" };
    }
});
