import { definePlugin } from "../../sdk/index";

export default definePlugin({
    version: "1.0.0",
    onDamage(ctx, hit) {
        if (!hit.attackerIsPlayer || !hit.targetIsPlayer || hit.damage < 1) {
            return;
        }

        ctx.host.api.chat.send(
            `${hit.attacker} hit ${hit.target} for ${hit.damage.toFixed(0)} ` +
            `(${hit.targetHealth.toFixed(0)}/${hit.targetMaxHealth.toFixed(0)} hp)`
        );
    }
});
