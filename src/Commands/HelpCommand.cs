using System;
using System.Linq;
using System.Threading.Tasks;

namespace ValheimJS.Commands;

[Command("help", Description = "List available commands or show command help.", Help = "!help command [query]\nFor example: !help demo:hello name\nUse !help alone to list commands you can access.")]
public sealed class HelpCommand : ChatCommand
{
    private readonly Core.ChatCommands _registry;
    public HelpCommand(Core.ChatCommands registry) => _registry = registry;
    public override Task Execute(ChatCommandContext ctx)
    {
        if (ctx.Options.Count != 0) return ctx.User.SendMessage("Use !help command [query].");
        if (string.IsNullOrWhiteSpace(ctx.Text))
        {
            var commands = _registry.AccessibleCommands(ctx.User).OrderBy(item => item.Namespace).ThenBy(item => item.Name).ToArray();
            var lines = commands.Take(20).Select(item => "!" + (string.IsNullOrEmpty(item.Namespace) ? "" : item.Namespace + ":") + item.Name + " — " + item.Description);
            return ctx.User.SendMessage(string.Join("\n", lines) + (commands.Length > 20 ? "\nShowing the first 20 commands." : "") + "\nUse !help command [query] for details.");
        }
        var text = ctx.Text.TrimStart();
        var boundary = 0;
        while (boundary < text.Length && !char.IsWhiteSpace(text[boundary])) boundary++;
        var route = text.Substring(0, boundary);
        var query = text.Substring(boundary).TrimStart();
        try { return ctx.User.SendMessage(_registry.Help(ctx.User, route, query)); }
        catch (ArgumentException error) { return ctx.User.SendMessage(error.Message); }
    }
}
