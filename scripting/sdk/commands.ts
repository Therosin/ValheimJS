import type { PluginContext } from "./index";
import type { ChatPayload } from "./hooks";

export type CommandResult = string | void;

export interface CommandSpec {
    /** One line shown by the built-in `!help`. */
    description: string;
    /** Return a string to send it back to the channel. */
    run(ctx: PluginContext, args: string[], payload: ChatPayload): CommandResult;
    /** Only players the server lists as admins may run it. */
    admin?: boolean;
}

/**
 * Chat-command router. Spread the returned object into your plugin:
 *
 * ```ts
 * export default definePlugin({
 *   ...defineCommands("!", {
 *     ping: { description: "health check", run: () => "pong" }
 *   })
 * });
 * ```
 */
export function defineCommands(prefix: string, commands: Record<string, CommandSpec>) {
    return {
        onChat(ctx: PluginContext, payload: ChatPayload): void {
            const text = payload.text.trim();
            if (!text.startsWith(prefix)) {
                return;
            }

            const [name, ...args] = text.slice(prefix.length).split(/\s+/);
            const key = name.toLowerCase();
            const chat = ctx.host.api.chat;

            if (key === "help" || key === "") {
                const lines = Object.entries(commands)
                    .map(([command, spec]) => `${prefix}${command} — ${spec.description}`)
                    .join("\n");
                chat.send(lines);
                return;
            }

            const spec = commands[key];
            if (!spec) {
                return;
            }

            if (spec.admin && !ctx.host.api.players.isAdmin(payload.senderId)) {
                chat.sendTo(payload.senderId, "permission denied");
                return;
            }

            try {
                const result = spec.run(ctx, args, payload);
                if (typeof result === "string" && result.length > 0) {
                    chat.send(result);
                }
            } catch (error) {
                ctx.host.log("error", `command ${prefix}${key} failed: ${String(error)}`);
            }
        }
    };
}
