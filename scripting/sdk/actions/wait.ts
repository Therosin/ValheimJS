import { position, registerAction } from "./registry";
import type { Vec3Like } from "./registry";

registerAction<{ wait: number }>("wait", {
    tick(context, config) {
        if (context.awaiting) {
            const settlement = context.takeSettled();
            if (settlement === null) return "running";
            return settlement.status === "arrived"
                ? "success"
                : { status: "failure", reason: settlement.reason ?? settlement.status };
        }

        const result = context.issue("wait", () => context.actor.wait(config.wait));
        return result !== undefined && !result.ok
            ? { status: "failure", reason: result.reason ?? "rejected" }
            : "running";
    }
});

interface LookAtConfig {
    lookAt: Vec3Like;
}

registerAction<LookAtConfig>("lookAt", {
    tick(context, config) {
        if (context.awaiting) {
            const settlement = context.takeSettled();
            if (settlement === null) return "running";
            return settlement.status === "arrived"
                ? "success"
                : { status: "failure", reason: settlement.reason ?? settlement.status };
        }

        const target = position(config.lookAt);
        const result = context.issue("lookAt", () => context.actor.lookAt(target.x, target.y, target.z));
        return result !== undefined && !result.ok
            ? { status: "failure", reason: result.reason ?? "rejected" }
            : "running";
    }
});

interface FaceConfig {
    face: string | Vec3Like;
}

registerAction<FaceConfig>("face", {
    tick(context, config) {
        if (context.awaiting) {
            const settlement = context.takeSettled();
            if (settlement === null) return "running";
            return settlement.status === "arrived"
                ? "success"
                : { status: "failure", reason: settlement.reason ?? settlement.status };
        }

        if (typeof config.face === "string") {
            const result = context.issue("face", () => context.actor.face(config.face as string));
            return result !== undefined && !result.ok
                ? { status: "failure", reason: result.reason ?? "rejected" }
                : "running";
        }

        const target = position(config.face);
        const result = context.issue("lookAt", () => context.actor.lookAt(target.x, target.y, target.z));
        return result !== undefined && !result.ok
            ? { status: "failure", reason: result.reason ?? "rejected" }
            : "running";
    }
});
