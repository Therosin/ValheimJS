import { authorityOnly, definePlugin, JsPlugin, log, npc, npcBase, players, type NpcView } from "../../../sdk/index";

/** Class factory: the native constructor only exists while a plugin context is active. */
function makeWanderer() {
    const Base = npcBase();
    return class Wanderer extends Base {
        private target?: { x: number; y: number; z: number };

        onSpawn(view: NpcView) {
            this.target = { x: view.x + 5, y: view.y, z: view.z };
            log(`spawned ${this.id} at ${Math.round(view.x)},${Math.round(view.z)}`);
        }

        onRehydrate(view: NpcView) {
            this.target = { x: view.x + 5, y: view.y, z: view.z };
            log(`rehydrated ${this.id}`);
        }

        onTick(view: NpcView) {
            const target = this.target ?? { x: view.x + 5, y: view.y, z: view.z };
            this.target = target;
            this.moveTo(target.x, target.y, target.z, false);
        }
    };
}

class Demo extends JsPlugin {
    setup() {
        if (!("npc" in this.ctx.host.api)) return;
        npc.register(makeWanderer(), { kind: "wanderer", prefab: "Dverger", tickMs: 500 });
    }

    onServerTick() {
        if (!("npc" in this.ctx.host.api) || !npc.available()) return;

        const now = Date.now();
        if (now - Number(this.ctx.state.lastLog ?? 0) >= 5000) {
            this.ctx.state.lastLog = now;
            const api = this.ctx.host.api;
            const online = "players" in api && "online" in api.players ? api.players.online() : [];
            const local = "players" in api && "local" in api.players ? api.players.local() : null;
            const people = [...online, ...(local ? [local] : [])];
            const playerText = people.map((player) => `${player.name}@${player.x.toFixed(1)},${player.y.toFixed(1)},${player.z.toFixed(1)}`);
            const positions = npc.handles("wanderer").map((entity) => `${entity.id}@${entity.x.toFixed(1)},${entity.y.toFixed(1)},${entity.z.toFixed(1)}`);
            log(`players ${people.length} [${playerText.join(" ")}] entities ${positions.length} [${positions.join(" ")}]`);
        }

        if (this.ctx.state.attempted) return;
        authorityOnly(() => {
            const online = "players" in this.ctx.host.api ? this.ctx.host.api.players.online() : [];
            const player = online[0];
            if (!player) return;
            this.ctx.state.attempted = true;
            // Another instance may already exist (reload/restart): adopt it, never duplicate.
            if (npc.handles("wanderer").length > 0) return;
            const position = { x: Math.round(player.x) + 3, y: player.y, z: player.z };
            const entity = npc.spawn("wanderer", { position, name: "Wanderer" });
            log(entity ? `spawn ${entity.id}` : "spawn unavailable");
        });
    }
}

export default definePlugin(Demo);
