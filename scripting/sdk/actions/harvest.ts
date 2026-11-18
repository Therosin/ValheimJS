import { registerAction } from "./registry";
import type { NpcAffordances } from "../api/types";

export interface HarvestStep {
    target: string;
    approach?: boolean | { distance?: number; run?: boolean };
}

interface HarvestConfig {
    harvest: HarvestStep;
}

/**
 * Repeats the native harvest verb until the target disappears. Each call is one hit; the
 * interaction owns tool, range and stamina checks, so this action only handles approach and
 * target lifecycle.
 */
registerAction<HarvestConfig>("harvest", {
    tick(context, config) {
        const step = config.harvest;
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

        const entry = context.sense(step.target);
        if (entry === null) {
            context.log(`harvest target ${step.target} is gone`);
            return context.local.harvestStarted === true
                ? "success"
                : { status: "failure", reason: "target-missing" };
        }

        const affordances = context.actor.affordances?.(step.target) as NpcAffordances | undefined;
        if (affordances !== undefined && !affordances.supported) {
            if (
                context.local.harvestStarted === true &&
                (affordances.reason === "not-loaded" ||
                    affordances.reason === "target-missing" ||
                    affordances.reason === "not-interactable")
            ) {
                return "success";
            }

            context.log(`harvest target ${step.target} unsupported: ${affordances.reason ?? "unknown"}`);
            return { status: "failure", reason: affordances.reason ?? "unsupported" };
        }

        if (enabled && entry.distance > distance) {
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

        if (context.actor.interact === undefined) {
            return { status: "failure", reason: "unsupported" };
        }

        if (context.local.harvestStarted !== true) {
            context.log(`harvesting ${step.target}`);
        }

        const result = context.actor.interact(step.target, { verb: "harvest" });
        if (result.ok) {
            context.local.harvestStarted = true;
            return "running";
        }

        if (
            context.local.harvestStarted === true &&
            (result.reason === "target-missing" ||
                result.reason === "not-loaded" ||
                result.reason === "not-harvestable")
        ) {
            return "success";
        }

        context.log(`harvest ${step.target} failed: ${result.reason ?? "unknown"}`);
        return { status: "failure", reason: result.reason ?? "harvest-failed" };
    }
});
