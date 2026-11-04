using System.Collections.Concurrent;
using ValheimJS.Bindings;
using ValheimJS.Commands;
using ValheimJS.Services;
using ValheimJS.Scripting;

internal static class HelpCommandProbe
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-command-help-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "host", "plugins", "help-probe");
        Directory.CreateDirectory(directory);
        var queue = new ConcurrentQueue<Action>();
        var replies = new List<string>();
        var errors = new List<string>();
        var accounts = new AccountService(Path.Combine(root, "accounts"), () => true);
        var permissions = new PermissionService(Path.Combine(root, "permissions"), accounts);
        var account = accounts.Resolve("Steam_42", "Player");
        using var registry = new ValheimJS.Core.ChatCommands(queue.Enqueue, errors.Add);
        using var scope = registry.OpenScope();
        scope.Register("host", new HelpCommand(registry));
        var nativeId = scope.Register("host", new NativeHelp());
        using var managers = new ManagementCommands(registry, accounts, permissions, _ => false);
        var host = new JsHost(Path.Combine(root, "host"), ProcessSide.Server, new Log(),
            bindingFilter: type => type == typeof(ChatCommandsBindings), commands: registry);
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.ini"), "name=help-probe\napiVersion=1\n");
            File.WriteAllText(Path.Combine(directory, "shared.js"), """
                (function(){
                 var ctx=globalThis.__vjs_context,api=ctx.host.api.ChatCommands;
                 function forbidden(){throw Error('Help invoked command lifecycle');}
                 globalThis.__vjs_plugin={__vjsPlugin:'object',value:{setup:function(){
                  api.registerCommand('{"name":"static","help":"Static help text"}',forbidden,forbidden);
                  api.registerCommand('{"name":"fallback","description":"Description fallback"}',forbidden,forbidden);
                  api.registerCommand('{"name":"empty"}',forbidden,forbidden);
                  var id=api.registerCommand('{"name":"contextual","namespace":"test","aliases":["contextual"]}',forbidden,forbidden);
                  api.setHelp(id,function(query){if(globalThis.__vjs_context!==ctx)throw Error('Help context lost');return 'Context: '+query;});
                  var secret=api.registerCommand('{"name":"secret","roles":["staff"]}',forbidden,forbidden);
                  api.setHelp(secret,function(query){return 'Secret '+query;});
                  var asyncId=api.registerCommand('{"name":"async-help"}',forbidden,forbidden);
                  api.setHelp(asyncId,async function(){return 'Unsupported';});
                 }}};
                })();
                """);
            host.Load();
            Assert(!host.HasErrors, string.Join(" | ", host.Errors));
            var user = new CommandUser(42, "Player", "Steam_42", replies.Add, accounts, account.Id, permissions);
            string Send(string text)
            {
                var previous = replies.Count;
                registry.Submit(user, text);
                var end = DateTime.UtcNow.AddSeconds(2);
                var settled = 0;
                do
                {
                    host.Tick();
                    while (queue.TryDequeue(out var action)) action();
                    settled = replies.Count > previous && queue.IsEmpty ? settled + 1 : 0;
                    if (settled >= 5) return replies.Last();
                    Thread.Sleep(1);
                } while (DateTime.UtcNow < end);
                throw new Exception("Help reply timed out");
            }
            Assert(Send("!help static") == "Static help text", "static help lost");
            Assert(Send("!help fallback") == "Description fallback", "description fallback lost");
            Assert(Send("!help empty").Contains("No help"), "missing help fallback lost");
            Assert(Send("!help contextual topic key=\"some value\" more") == "Context: topic key=\"some value\" more", "raw contextual query changed");
            Assert(Send("!help !test:contextual") == "Context: ", "alias/namespaced/empty query help failed");
            Assert(Send("!help native topic") == "Native topic", "native contextual help failed");
            Assert(Send("!help unknown").Contains("Unknown command"), "unknown target help failed");
            Assert(Send("!help secret").Contains("permission"), "restricted help was exposed");
            Assert(Send("!help manage:roles").Contains("permission"), "management help bypassed custom access gate");
            var listing = Send("!help");
            Assert(listing.Contains("!test:contextual") && !listing.Contains("!secret") && !listing.Contains("!manage:"), "help listing exposed inaccessible commands");
            permissions.SetAccess(account.Id, new[] { "staff" }, Array.Empty<string>(), Array.Empty<string>());
            Assert(Send("!help secret topic") == "Secret topic", "allowed help rejected");
            Assert(Send("!help async-help").Contains("failed") && errors.Any(error => error.Contains("synchronously")), "promise-returning help was accepted");
            Assert(!errors.Any(error => error.Contains("Help invoked command lifecycle")), "target validation/execution ran for help");
            var deniedOwner = false;
            try { scope.SetHelp("someone-else", nativeId, _ => "forged"); } catch (InvalidOperationException) { deniedOwner = true; }
            Assert(deniedOwner, "another owner replaced help callback");
            host.Reload();
            Assert(!host.HasErrors && Send("!help contextual again") == "Context: again", "help reload leaked or lost callback");
            host.Dispose();
            Assert(Send("!help contextual").Contains("Unknown command"), "unload retained contextual help");
            Console.WriteLine("PASS: private static/contextual help, native overrides, raw queries, access filtering, synchronous callbacks and cleanup");
        }
        finally { host.Dispose(); Directory.Delete(root, true); }
    }
    [Command("native", Help = "Native static text")]
    private sealed class NativeHelp : ChatCommand
    {
        public override string GetHelp(string query) => "Native " + query;
        public override Task Execute(ChatCommandContext ctx) => throw new Exception("Help invoked native execution");
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Log : IJsLog
    { public void Write(string level, string plugin, string message) { if (level == "error") Console.Error.WriteLine(message); } }
}
