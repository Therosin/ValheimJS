import { afterEach, expect, test } from "bun:test";
import { ChatCommand, ChatCommands, type ChatCommandContext } from "../sdk/commands";
import type { PluginContext } from "../sdk/runtime";

const globals = globalThis as { __vjs_context?: PluginContext };
afterEach(() => { delete globals.__vjs_context; });
test("ChatCommands adapts a class instance to the native registry and private reply token", async () => {
    const calls: unknown[][] = [];
    globals.__vjs_context = { host: { api: { ChatCommands: {
        registerCommand: (...args: unknown[]) => { calls.push(["register", ...args]); return 7; },
        unregisterCommand: (id: number) => calls.push(["unregister", id]),
        reply: (...args: unknown[]) => calls.push(["reply", ...args]),
        hasRole: (_id: string, role: string) => role === "admin",
        hasPermission: (_id: string, permission: string) => permission === "chat.use",
        list: () => [{ name: "hello" }]
    } } } } as unknown as PluginContext;
    class Hello extends ChatCommand {
        readonly greeting = "hello";
        constructor() { super({ name: "hello", namespace: "demo", aliases: ["hi"], roles: ["admin"] }); }
        async validate(ctx: ChatCommandContext) { if (ctx.options.invalid) throw new Error("invalid input"); }
        async execute(ctx: ChatCommandContext) {
            expect(ctx.user.hasRole("admin")).toBe(true);
            expect(ctx.user.hasPermission("chat.use")).toBe(true);
            expect(ctx.user.account).toBeNull();
            ctx.user.sendMessage(`${this.greeting} ${ctx.user.name}: ${ctx.text}`);
        }
    }
    expect(ChatCommands.registerCommand(new Hello())).toBe(7);
    expect(JSON.parse(calls[0][1] as string).roles).toEqual(["admin"]);
    const validate = calls[0][2] as (value: unknown) => Promise<void>;
    const execute = calls[0][3] as (value: unknown) => Promise<void>;
    const invocation = { invocationId: "token", command: "demo:hello", options: {}, text: 'text key="literal"', user: { peerId: "9007199254740993", name: "User", platformId: null } };
    await validate(invocation);
    await execute(invocation);
    expect(calls[1]).toEqual(["reply", "token", 'hello User: text key="literal"']);
    await expect(validate({ ...invocation, options: { invalid: "true" } })).rejects.toThrow("invalid input");
    ChatCommands.unregisterCommand(7);
    expect(calls[2]).toEqual(["unregister", 7]);
    expect(ChatCommands.list()).toEqual([{ name: "hello" }]);
});

test("command help supports static text or a contextual callback with class this", () => {
    const registrations: unknown[][] = [];
    const callbacks: Array<(query: string) => string> = [];
    const removed: number[] = [];
    globals.__vjs_context = { host: { api: { ChatCommands: {
        registerCommand: (...args: unknown[]) => { registrations.push(args); return registrations.length; },
        setHelp: (_id: number, callback: (query: string) => string) => callbacks.push(callback),
        unregisterCommand: (id: number) => removed.push(id)
    } } } } as unknown as PluginContext;
    class StaticHelp extends ChatCommand {
        constructor() { super({ name: "static", help: "Use the static command." }); }
        execute() {}
    }
    class ContextualHelp extends ChatCommand {
        readonly prefix = "topic";
        constructor() {
            super({ name: "dynamic", help(query) { return `${(this as unknown as ContextualHelp).prefix}: ${query}`; } });
        }
        execute() {}
    }
    ChatCommands.registerCommand(new StaticHelp());
    ChatCommands.registerCommand(new ContextualHelp());
    expect(JSON.parse(registrations[0][0] as string).help).toBe("Use the static command.");
    expect(JSON.parse(registrations[1][0] as string).help).toBeUndefined();
    expect(callbacks[0]('name="some value"')).toBe('topic: name="some value"');
    expect(removed).toEqual([]);
    globals.__vjs_context.host.api.ChatCommands.setHelp = () => { throw new Error("registration failed"); };
    expect(() => ChatCommands.registerCommand(new ContextualHelp())).toThrow("registration failed");
    expect(removed).toEqual([3]);
});
