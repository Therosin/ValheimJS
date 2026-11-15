import { position, registerAction } from "./registry";
import type { Vec3Like } from "./registry";

interface MoveConfig {
    moveTo: Vec3Like;
    run?: boolean;
}

registerAction<MoveConfig>("moveTo", {
    tick(context, config) {
        if (context.awaiting) {
            const settlement = context.takeSettled();
            if (settlement === null) return "running";
            return settlement.status === "arrived"
                ? "success"
                : { status: "failure", reason: settlement.reason ?? settlement.status };
        }

        const target = position(config.moveTo);
        const result = context.issue("moveTo", () =>
            context.actor.moveTo(target.x, target.y, target.z, config.run ?? false));
        return result !== undefined && !result.ok
            ? { status: "failure", reason: result.reason ?? "rejected" }
            : "running";
    }
});

interface FollowConfig {
    follow: {
        target: string;
        distance?: number;
        run?: boolean;
    };
}

registerAction<FollowConfig>("follow", {
    tick(context, config) {
        const follow = config.follow;
        if (context.awaiting) {
            const settlement = context.takeSettled();
            if (settlement === null) return "running";
            if (settlement.status !== "arrived") {
                return { status: "failure", reason: settlement.reason ?? settlement.status };
            }
        }

        const entry = context.sense(follow.target);
        if (entry === null) return { status: "failure", reason: "target-lost" };
        if (entry.distance <= (follow.distance ?? 4)) return "success";

        const result = context.issue("moveTo", () =>
            context.actor.moveTo(entry.pos.x, entry.pos.y, entry.pos.z, follow.run ?? true));
        return result !== undefined && !result.ok
            ? { status: "failure", reason: result.reason ?? "rejected" }
            : "running";
    }
});
