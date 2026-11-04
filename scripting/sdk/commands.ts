import { context } from "./runtime";
import type { Account, ChatCommandDefinition } from "./api/types";

export interface ChatCommandUser {
    /** Routed peer id kept as text to avoid losing 64-bit precision. */
    peerId: string;
    name: string;
    platformId: string | null;
    account: Account | null;
    hasRole(role: string): boolean;
    hasPermission(permission: string): boolean;
    sendMessage(text: string): void;
}
export interface ChatCommandContext {
    command: string;
    options: Readonly<Record<string, string>>;
    text: string;
    user: ChatCommandUser;
}

/** Command implementation only; parsing, registry, lifecycle and ownership live in C#. */
export abstract class ChatCommand {
    readonly definition: ChatCommandDefinition;
    constructor(definition: ChatCommandDefinition) {
        this.definition = { ...definition, aliases: [...(definition.aliases ?? [])], roles: [...(definition.roles ?? [])], permissions: [...(definition.permissions ?? [])] };
    }
    validate(_ctx: ChatCommandContext): void | Promise<void> {}
    abstract execute(ctx: ChatCommandContext): void | Promise<void>;
}

interface Invocation extends Omit<ChatCommandContext, "user"> {
    invocationId: string;
    user: Omit<ChatCommandUser, "sendMessage" | "hasRole" | "hasPermission">;
}

export const ChatCommands = {
    registerCommand(command: ChatCommand): number {
        const api = context().host.api.ChatCommands;
        const adapt = (value: unknown): ChatCommandContext => {
            const raw = value as Invocation;
            return { command: raw.command, options: raw.options, text: raw.text,
                user: { ...raw.user, account: raw.user.account ?? null,
                    hasRole: role => api.hasRole(raw.invocationId, role),
                    hasPermission: permission => api.hasPermission(raw.invocationId, permission),
                    sendMessage: text => api.reply(raw.invocationId, text) } };
        };
        const { help, ...definition } = command.definition;
        if (help !== undefined && typeof help !== "string" && typeof help !== "function")
            throw new Error("Command help must be text or a synchronous query callback.");
        const id = api.registerCommand(JSON.stringify({ ...definition, ...(typeof help === "string" ? { help } : {}) }),
            raw => command.validate(adapt(raw)), raw => command.execute(adapt(raw)));
        if (typeof help === "function") {
            try { api.setHelp(id, query => help.call(command, String(query))); }
            catch (error) { api.unregisterCommand(id); throw error; }
        }
        return id;
    },
    unregisterCommand(id: number): void { context().host.api.ChatCommands.unregisterCommand(id); },
    list(): ChatCommandDefinition[] { return context().host.api.ChatCommands.list(); }
};
