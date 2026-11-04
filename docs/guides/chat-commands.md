---
title: Chat commands
description: Register private commands with validation, aliases and leading options.
---

# Chat commands

Register a command in plugin setup. A player types `!hello` in chat; ValheimJS sends it privately
to the world authority, validates it, executes it and returns replies only to that player.
Both peers need ValheimJS. Register authoritative commands in `shared/main.ts` to support both
dedicated servers and player hosts, or `server/main.ts` for dedicated servers only.

ValheimJS registers native [management commands](management.md) for users, roles and permissions.
They use the same private command transport and need no example plugin.

```ts
import { ChatCommand, ChatCommands, definePlugin } from "../../../sdk/index";
import type { ChatCommandContext } from "../../../sdk/index";

class HelloCommand extends ChatCommand {
    constructor() {
        super({ name: "hello", namespace: "demo", aliases: ["hello"],
            description: "Send a private greeting.", roles: [] });
    }

    async validate(ctx: ChatCommandContext) {
        if ((ctx.options.name?.length ?? 0) > 80) throw new Error("Name is too long.");
    }

    async execute(ctx: ChatCommandContext) {
        ctx.user.sendMessage(`Hello, ${ctx.options.name ?? ctx.user.name}! ${ctx.text}`);
    }
}

export default definePlugin({
    version: "1.0.0",
    setup() { ChatCommands.registerCommand(new HelloCommand()); }
});
```

`!demo:hello name="Some Player" welcome home` and `!hello name="Some Player" welcome home`
call the same command. The namespace is an optional category, independent of plugin identity.
Aliases are complete routes: use `hello` for `!hello` or `other:hello` for `!other:hello`.
Names and aliases are case insensitive; duplicate routes fail registration.

## Help text and contextual help

Add optional `help` alongside `name` and `description`:

```ts
super({
    name: "greet",
    description: "Send a private greeting.",
    help: '!greet [name="Some Player"] [message]'
});

// Or return help based on the text following the target command:
super({
    name: "greet",
    description: "Send a private greeting.",
    help: (query: string) => query.trim() === "name"
        ? 'Use name="Some Player" to choose the greeting name.'
        : '!greet [name="Some Player"] [message]'
});
```

Players use `!help greet` or `!help greet name`. The callback receives the remaining query as
text, preserving quotes and `key=value` text. It must return a string synchronously. It runs in
the owning plugin's context, with the command instance as `this` for regular functions. Help
never invokes the target's validation or execution methods. Missing or empty help falls back to
the description, then a no-help message.

`!help` alone lists up to 20 accessible commands. Aliases can be used as help targets, including
an optional leading `!`. Replies stay private; role/permission and custom access requirements
apply to help and filter the listing. For native commands, use `[Command(..., Help = "...")]`
or override `GetHelp(string query)`. Custom native access rules belong in `CanAccess(CommandUser)`
so dispatch and help use the same gate.

Built-in management help also accepts actions: `!help manage:roles create`,
`!help manage:permission update` and `!help manage:user grant`.

## Input and privacy

Options must immediately follow the command, before ordinary text. In the example above,
`options.name` is `Some Player` and `text` is `welcome home`. All option values are strings.
After the first ordinary word, everything else is literal text: `!hello welcome name=Sam`
has no options and keeps `welcome name=Sam` as text.

Use double quotes for spaces, with `\"` for a literal quote and `\\` for a backslash.
Option keys begin with a letter or underscore and may contain letters, digits, underscores
and hyphens. Duplicate keys, unfinished quotes and invalid routes produce private errors.
Routes contain letters, digits, underscores or hyphens, with at most one category colon.

Every input starting with `!` is consumed, including unknown and malformed commands. It never
enters public chat. Native `/` commands and ordinary chat retain their existing handling.
Replies use a private `Commands` chat line. There is no general broadcast API here.

`ctx.user` supplies a session peer id as a string, player name, nullable platform id and
`sendMessage(text)`. Identity comes from the actual connected peer, not submitted JSON.
`roles: ["admin", "moderator"]` requires either role. `permissions: ["chat.use"]` requires every
listed permission. The native PermissionService checks policy before validation and again before
execution; explicit account denials override grants. Empty lists impose no requirement.
`ctx.user.account` is the account snapshot, with live `hasRole`/`hasPermission` helpers for custom
checks. See [Accounts](accounts.md). Policy editing is not exposed to JS plugins.

For example, `roles: ["admin", "staff"]` admits either assigned role. A player's options cannot
change that requirement, and an alias uses the same gate as its primary route. A caller without
an account is denied when roles are required. An `admin` permission wildcard does not make that
account a member of every role: include `admin` explicitly when it should satisfy a role gate.
Valheim admin-list recovery access applies to the built-in management commands, not arbitrary
plugin command role requirements.

## Lifecycle and registry

`validate` completes before `execute` starts. Both may return a promise; the class instance and
its `this` are preserved. Validation errors reach the caller; execution errors produce a generic
private failure and a detailed host log. This asynchronous contract is specific to commands;
plugin setup and ordinary synchronous hooks still follow their documented lifecycle rules.

`ChatCommands.registerCommand(instance)` returns an id. Use `unregisterCommand(id)` to remove it
early, and `list()` to inspect registered definitions. The host releases all of a plugin's routes
on unload/reload and cancels active invocations. Late replies are rejected. An invocation has
30 seconds to finish, with at most four running per caller and 64 per process. Cancellation
does not undo work already performed; avoid starting work that outlives the invocation.

The SDK holds no registry. `Bindings/ChatCommandsBindings.cs` adapts callbacks into native
commands; `Core/ChatCommands.cs` owns registration and execution. `Networking/CommandService.cs`
owns private submission and replies on `valheimjs:commands`, using the shared networking service.

## Native commands

C# commands use the same registry, parser and lifecycle:

```csharp
[Command("hello", Namespace = "demo", Aliases = new[] { "hello" },
    Description = "Send a private greeting.")]
public sealed class HelloCommand : ChatCommand
{
    public override Task Execute(ChatCommandContext ctx)
        => ctx.User.SendMessage($"Hello, {ctx.User.Name}!");
}

// registry is the process-owned ValheimJS.Core.ChatCommands instance.
var scope = registry.OpenScope();
scope.Register("my-service", new HelloCommand());
// Dispose scope when the owning service stops.
```

Import `ValheimJS.Commands` for command types. Attributes declare metadata; registration remains
explicit. Constructors may instead pass a `ChatCommandDefinition` to the base class.
`Validate` defaults to a completed task. Native contexts also expose `Cancellation`; observe it
during long work. Command stages start on the Unity thread. Native code resuming after an await
must marshal Unity API access through the host's main-thread queue.

`ChatService` is separate and dormant. Its retained implementation only receives server chat
announcements; it does not participate in the command registry or command transport.
