using System.Collections.Concurrent;
using ValheimJS.Bindings;
using ValheimJS.Commands;
using ValheimJS.Scripting;
using Registry = ValheimJS.Core.ChatCommands;

internal static class CommandProbe
{
    internal static int Run()
    {
        try { Parser(); RegistryLifecycle(); ScriptCommands(); RoleCommandProbe.Run(); HelpCommandProbe.Run(); Console.WriteLine("PASS: command grammar, aliases, async lifecycle, private replies and JS registry cleanup"); return 0; }
        catch (Exception error) { Console.Error.WriteLine("FAIL: command probe: " + error); return 1; }
    }
    private static void Parser()
    {
        var input = ChatCommandParser.Parse("!blah:foo key=\"some value\" count=3 hello there mode=\"literal\"");
        Assert(input.Route == "blah:foo" && input.Options["key"] == "some value" && input.Options["count"] == "3" && input.Text == "hello there mode=\"literal\"", "leading options boundary");
        Assert(ChatCommandParser.Parse("!HI name=\"a \\\"quoted\\\" value\" empty=\"\"").Options["name"] == "a \"quoted\" value", "quoted escapes");
        Assert(ChatCommandParser.Parse("!foo free text x=1").Options.Count == 0, "options parsed after text");
        Assert(ChatCommandParser.Parse("!foo").Text == "", "bare command");
        foreach (var value in new[] { "!", "!foo::bar", "!foo:bar:baz", "!foo x=\"unterminated", "!foo x=1 X=2", "!foo x=\"a\"tail", "!foo x=\"bad\\q\"" }) Throws(() => ChatCommandParser.Parse(value), value);
        Assert(!ChatCommandParser.IsCommand("/help") && !ChatCommandParser.IsCommand("ordinary chat"), "native chat was claimed");
    }
    private static void RegistryLifecycle()
    {
        var queue = new ConcurrentQueue<Action>();
        var replies = new List<string>();
        using var registry = new Registry(queue.Enqueue);
        using var scope = registry.OpenScope();
        using var other = registry.OpenScope();
        var executed = 0;
        ChatCommandContext? active = null;
        var validation = new TaskCompletionSource<bool>();
        var command = new DelegateCommand(new ChatCommandDefinition { Name = "foo", Namespace = "blah", Aliases = new[] { "foo" } },
            ctx => { active = ctx; return validation.Task; }, ctx => { executed++; return ctx.User.SendMessage("private:" + ctx.Text); });
        var id = scope.Register("plugin", command);
        Throws(() => other.Register("other", new DelegateCommand(new ChatCommandDefinition { Name = "foo" })), "duplicate alias");
        var user = new CommandUser(42, "User", "Steam_42", text => replies.Add(text));
        Assert(!registry.Submit(user, "/help"), "native slash command intercepted");
        Assert(registry.Submit(user, "!foo key=1 body"), "alias not consumed");
        Assert(executed == 0, "execute ran before async validation");
        Throws(() => other.Reply("plugin", active!.InvocationId, "forged"), "cross-owner reply");
        validation.SetResult(true);
        Drain(queue, () => replies.Count == 1);
        Assert(executed == 1 && replies[0] == "private:body", "roles were enforced or private reply failed");
        Drain(queue);
        Throws(() => scope.Reply("plugin", active!.InvocationId, "late"), "reply after completion");
        scope.Unregister("wrong-owner", id);
        Assert(registry.List().Length == 1, "unregister ignored ownership");
        scope.Release("plugin");
        Assert(registry.List().Length == 0, "release left aliases registered");
        registry.Submit(user, "!foo");
        Assert(replies.Last().Contains("Unknown command"), "unknown command did not reply privately");

        var failing = new DelegateCommand(new ChatCommandDefinition { Name = "invalid" }, _ => throw new InvalidOperationException("validation reason"), _ => { executed++; return Task.CompletedTask; });
        scope.Register("plugin", failing); registry.Submit(user, "!invalid");
        Assert(replies.Last() == "validation reason" && executed == 1, "validation failure executed command");
        var pending = new TaskCompletionSource<bool>();
        scope.Register("cancel", new DelegateCommand(new ChatCommandDefinition { Name = "pending" }, ctx => { active = ctx; return pending.Task; }, _ => { executed++; return Task.CompletedTask; }));
        registry.Submit(user, "!pending"); scope.Release("cancel");
        Assert(active!.Cancellation.IsCancellationRequested, "unload did not cancel invocation");
        pending.SetResult(true); Drain(queue);
        Assert(executed == 1, "unloaded command executed after validation");
        using var native = registry.OpenScope();
        native.Register("native", new NativeCommand());
        Assert(registry.List().Any(value => value.Name == "native" && value.Namespace == "tools"), "native attribute definition");
    }
    private static void ScriptCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-commands-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "plugins", "commands");
        Directory.CreateDirectory(directory);
        var queue = new ConcurrentQueue<Action>();
        var replies = new List<string>();
        using var registry = new Registry(queue.Enqueue);
        var log = new ProbeLog();
        var host = new JsHost(root, ProcessSide.Server, log, bindingFilter: type => type == typeof(ChatCommandsBindings), commands: registry);
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.ini"), "name=commands\napiVersion=1\n");
            File.WriteAllText(Path.Combine(directory, "shared.js"), """
                (function(){
                 var ctx=globalThis.__vjs_context, api=ctx.host.api.ChatCommands;
                 globalThis.__vjs_plugin={__vjsPlugin:'object',value:{setup:function(){
                  api.registerCommand('{"name":"hello","namespace":"demo","aliases":["hi"]}',
                   async function(raw){ await new Promise(function(resolve){setTimeout(resolve,1);}); if(globalThis.__vjs_context!==ctx)throw Error('validation context lost'); if(raw.options.fail)throw Error('JS validation reason'); },
                   async function(raw){
                    if(raw.options.key!=='some value')throw Error('options are not a dictionary');
                    await Promise.resolve();
                    if(globalThis.__vjs_context!==ctx)throw Error('execution context lost');
                    api.reply(raw.invocationId,'hello '+raw.user.name+': '+raw.text);
                   });
                 }}};
                })();
                """);
            host.Load(); Assert(!host.HasErrors, string.Join(" | ", host.Errors));
            var user = new CommandUser(42, "User", "Steam_42", text => replies.Add(text));
            registry.Submit(user, "!hi key=\"some value\" body mode=literal");
            Drain(queue, () => replies.Count > 0, host.Tick);
            Assert(replies[0] == "hello User: body mode=literal", "async JS command failed: " + string.Join(" | ", replies));
            registry.Submit(user, "!demo:hello fail=1");
            Drain(queue, () => replies.Count > 1, host.Tick);
            Assert(replies[1] == "JS validation reason", "JS validation rejection lost");
            registry.Submit(user, "!hi key=\"some value\" cancelled");
            host.Reload();
            Drain(queue, tick: host.Tick);
            Assert(replies.Count == 2 && registry.List().Length == 1 && !host.HasErrors, "reload leaked/collided registrations or invoked an unloaded command");
        }
        finally
        {
            host.Dispose();
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("valheimjs-commands-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
        Assert(registry.List().Length == 0, "JS disposal left commands registered");
    }
    private static void Drain(ConcurrentQueue<Action> queue, Func<bool>? until = null, Action? tick = null)
    {
        var timeout = DateTime.UtcNow.AddSeconds(until == null ? .05 : 2);
        do
        {
            tick?.Invoke();
            while (queue.TryDequeue(out var action)) action();
            if (until?.Invoke() == true) return;
            Thread.Sleep(1);
        } while (DateTime.UtcNow < timeout);
        if (until != null) Assert(until(), "async command timed out in harness");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action action, string message) { try { action(); } catch { return; } throw new Exception("Expected failure: " + message); }
    private sealed class DelegateCommand : ChatCommand
    {
        private readonly Func<ChatCommandContext, Task> _validate, _execute;
        internal DelegateCommand(ChatCommandDefinition definition, Func<ChatCommandContext, Task>? validate = null, Func<ChatCommandContext, Task>? execute = null) : base(definition)
        { _validate = validate ?? (_ => Task.CompletedTask); _execute = execute ?? (_ => Task.CompletedTask); }
        public override Task Validate(ChatCommandContext context) => _validate(context);
        public override Task Execute(ChatCommandContext context) => _execute(context);
    }
    [Command("native", Namespace = "tools", Roles = new[] { "admin" })]
    private sealed class NativeCommand : ChatCommand
    { public override Task Execute(ChatCommandContext context) => context.User.SendMessage("native"); }
    private sealed class ProbeLog : IJsLog
    { public void Write(string level, string plugin, string message) { if (level == "error") Console.Error.WriteLine(plugin + ": " + message); } }
}
