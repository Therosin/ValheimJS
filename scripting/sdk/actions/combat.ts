import { registerAction } from "./registry";

interface AttackConfig {
    attack: {
        target: string;
        secondary?: boolean;
    };
}

registerAction<AttackConfig>("attack", {
    tick(context, config) {
        if (context.actor.attack === undefined) {
            return { status: "failure", reason: "unsupported" };
        }

        const result = context.actor.attack(config.attack.target, config.attack.secondary ?? false);
        return result.ok
            ? "success"
            : { status: "failure", reason: result.reason ?? "attack-failed" };
    }
});

registerAction<{ jump: true }>("jump", {
    tick(context) {
        if (context.actor.jump === undefined) {
            return { status: "failure", reason: "unsupported" };
        }

        const result = context.actor.jump();
        return result.ok
            ? "success"
            : { status: "failure", reason: result.reason ?? "jump-failed" };
    }
});
