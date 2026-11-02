import { definePlugin, log, serverOnly, type PluginContext, type PlayerPayload } from "../../sdk/index";

interface Visit {
    id: string;
    name: string;
    visits: number;
    firstSeen: number;
    lastSeen: number;
}

const now = (): number => Date.now();

export default definePlugin(
    class Greeter {
        description = "welcomes players and tracks visits";
        version = "1.1.0";

        exports = {
            greet(name: string): string {
                return `hello ${name}`;
            }
        };

        setup(ctx: PluginContext): void {
            log(`greeter ready; data at ${ctx.paths.data}`);
        }

        onPlayerJoin = serverOnly((ctx: PluginContext, player: PlayerPayload): void => {
            // SteamID is stable across renames and reconnects; the uid is not.
            const key = ctx.host.api.players.steamId(player.uid) ?? String(player.uid);
            const visits = ctx.db.use<Visit>("visits");
            const record = visits.upsert(
                key,
                () => ({ id: key, name: player.name, visits: 0, firstSeen: now(), lastSeen: now() }),
                (doc) => ({ name: player.name, visits: doc.visits + 1, lastSeen: now() })
            );

            log(`${player.name} joined (uid ${player.uid}, visits ${record.visits + 1})`);
            ctx.host.api.chat.send(
                record.visits === 0
                    ? `Welcome to the server, ${player.name}!`
                    : `Welcome back, ${player.name}! Visit #${record.visits + 1}`
            );
        });

        onPlayerLeave = serverOnly((_ctx: PluginContext, player: PlayerPayload): void => {
            log(`${player.name} left`);
        });
    }
);
