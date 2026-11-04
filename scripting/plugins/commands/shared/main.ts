import { ChatCommand, ChatCommands, definePlugin, type ChatCommandContext } from "../../../sdk/index";

class HelloCommand extends ChatCommand {
    constructor() {
        super({ name: "hello", namespace: "demo", aliases: ["hello"], description: "Sends a private greeting.", roles: [],
            help: query => query.trim().toLowerCase() === "name"
                ? 'name="Some Player" chooses the greeting name; it must be at most 80 characters.'
                : '!hello [name="Some Player"] [message]\nExample: !hello name="Test Player" welcome home' });
    }
    async validate(ctx: ChatCommandContext) {
        if (ctx.options.name !== undefined && ctx.options.name.length > 80) throw new Error("The name must be at most 80 characters.");
    }
    async execute(ctx: ChatCommandContext) {
        ctx.user.sendMessage(`Hello, ${ctx.options.name ?? ctx.user.name}!${ctx.text ? " " + ctx.text : ""}`);
    }
}

export default definePlugin({ setup() { ChatCommands.registerCommand(new HelloCommand()); } });
