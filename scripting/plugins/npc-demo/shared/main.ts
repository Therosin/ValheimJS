import { authorityOnly, definePlugin, JsPlugin, log, npc, npcBase, type NpcView } from "../../../sdk/index";

/** Class factory: the native constructor only exists while a plugin context is active. */
function makeWanderer() {
    const Base = npcBase();
    return class Wanderer extends Base {
        onSpawn(view: NpcView) {
            log(`spawned ${this.id} at ${Math.round(view.x)},${Math.round(view.z)}`);
        }
        onRehydrate(view: NpcView) {
            log(`rehydrated ${this.id}`);
        }
        onTick(view: NpcView) {
            this.moveTo(5, view.y, 0, false);
        }
    };
}

class Demo extends JsPlugin {
    setup() {
        if (!("npc" in this.ctx.host.api)) return;
        npc.register(makeWanderer(), { kind: "wanderer", prefab: "Dverger", tickMs: 500 });
    }

    onServerTick() {
        if (!("npc" in this.ctx.host.api) || !npc.available() || this.ctx.state.attempted) return;
        authorityOnly(() => {
            this.ctx.state.attempted = true;
            const online = "players" in this.ctx.host.api ? this.ctx.host.api.players.online() : [];
            const player = online[0];
            const position = player
                ? { x: Math.round(player.x) + 3, y: player.y, z: player.z }
                : { x: 0, y: 10, z: 0 };
            const entity = npc.spawn("wanderer", { position, name: "Wanderer" });
            log(entity ? `spawn ${entity.id}` : "spawn unavailable");
        });
    }
}

export default definePlugin(Demo);
