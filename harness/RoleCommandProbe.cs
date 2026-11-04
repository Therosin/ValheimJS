using System.Collections.Concurrent;
using System.Text.Json;
using ValheimJS.Bindings;
using ValheimJS.Commands;
using ValheimJS.Services;
using ValheimJS.Scripting;

/// <summary>Real JS registration -> native role/permission policy -> JS callbacks.</summary>
internal static class RoleCommandProbe
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-command-roles-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "host", "plugins", "role-probe");
        Directory.CreateDirectory(directory);
        var queue = new ConcurrentQueue<Action>();
        var replies = new List<string>();
        var accounts = new AccountService(Path.Combine(root, "accounts"), () => true);
        var permissions = new PermissionService(Path.Combine(root, "permissions"), accounts);
        var account = accounts.Resolve("Steam_42", "Player");
        using var registry = new ValheimJS.Core.ChatCommands(queue.Enqueue);
        var host = new JsHost(Path.Combine(root, "host"), ProcessSide.Server, new Log(),
            bindingFilter: type => type == typeof(ChatCommandsBindings), commands: registry);
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.ini"), "name=role-probe\napiVersion=1\n");
            File.WriteAllText(Path.Combine(directory, "shared.js"), """
                (function(){
                 var api=globalThis.__vjs_context.host.api.ChatCommands;
                 var validations=0, executions=0, release;
                 function validate(){validations++;}
                 function execute(raw){executions++;api.reply(raw.invocationId,'executed');}
                 globalThis.__vjs_plugin={__vjsPlugin:'object',value:{
                  setup:function(){
                   api.registerCommand('{"name":"restricted","namespace":"test","aliases":["restricted"],"roles":["ADMIN","staff"]}',validate,execute);
                   api.registerCommand('{"name":"staff","roles":["staff"]}',validate,execute);
                   api.registerCommand('{"name":"both","roles":["staff"],"permissions":["test.one","test.two"]}',validate,execute);
                   api.registerCommand('{"name":"pending","roles":["staff"]}',async function(){validations++;await new Promise(function(resolve){release=resolve;});},execute);
                   api.registerCommand('{"name":"public","roles":[]}',validate,execute);
                  },
                  counts:function(){return {validations:validations,executions:executions};},
                  release:function(){release();}
                 }};
                })();
                """);
            host.Load();
            Assert(!host.HasErrors && registry.List().Length == 5, "JS role commands did not register");
            var user = new CommandUser(42, "Player", "Steam_42", replies.Add, accounts, account.Id, permissions);
            var unverified = new CommandUser(43, "Unknown", null, replies.Add);
            (int Validated, int Executed) Counts()
            {
                using var json = JsonDocument.Parse(host.DispatchTo("role-probe", "counts", "{}")!);
                return (json.RootElement.GetProperty("validations").GetInt32(), json.RootElement.GetProperty("executions").GetInt32());
            }
            void Pump(Func<bool> until)
            {
                var end = DateTime.UtcNow.AddSeconds(2);
                var settled = 0;
                do
                {
                    host.Tick();
                    while (queue.TryDequeue(out var action)) action();
                    settled = until() && queue.IsEmpty ? settled + 1 : 0;
                    if (settled >= 5) return;
                    Thread.Sleep(1);
                } while (DateTime.UtcNow < end);
                throw new Exception("Role command did not settle");
            }
            string Send(string text, CommandUser? caller = null)
            {
                var previous = replies.Count;
                registry.Submit(caller ?? user, text);
                Pump(() => replies.Count > previous);
                return replies.Last();
            }
            Assert(Send("!restricted roles=staff").Contains("permission"), "submitted options bypassed role policy");
            Assert(Counts() == (0, 0), "denied player entered JS validation/execution");
            Assert(Send("!test:restricted", unverified).Contains("permission"), "unverified caller bypassed role policy");
            Assert(Counts() == (0, 0), "denied accountless caller entered JS");

            permissions.SetAccess(account.Id, new[] { "staff" }, Array.Empty<string>(), Array.Empty<string>());
            Assert(Send("!restricted") == "executed", "matching staff role was not enough");
            Assert(Counts() == (1, 1), "allowed alias did not validate and execute exactly once");
            permissions.SetAccess(account.Id, new[] { "admin" }, Array.Empty<string>(), Array.Empty<string>());
            Assert(Send("!test:restricted") == "executed", "any-role semantics/case normalization failed");
            var before = Counts();
            Assert(Send("!staff").Contains("permission") && Counts() == before, "wildcard permission bypassed an explicit staff role requirement");

            permissions.SetAccess(account.Id, new[] { "staff" }, new[] { "test.one" }, Array.Empty<string>());
            Assert(Send("!both").Contains("permission") && Counts() == before, "roles bypassed a missing required permission");
            permissions.SetAccess(account.Id, new[] { "staff" }, new[] { "test.one", "test.two" }, new[] { "test.two" });
            Assert(Send("!both").Contains("permission") && Counts() == before, "explicit denial ignored");
            permissions.SetAccess(account.Id, new[] { "staff" }, new[] { "test.one", "test.two" }, Array.Empty<string>());
            Assert(Send("!both") == "executed", "combined role and permission requirements rejected matching user");

            before = Counts();
            registry.Submit(user, "!pending");
            Pump(() => Counts().Validated == before.Validated + 1);
            Assert(Counts().Executed == before.Executed, "pending validation executed early");
            permissions.SetAccess(account.Id, new[] { "player" }, Array.Empty<string>(), Array.Empty<string>());
            var previousReplies = replies.Count;
            host.DispatchTo("role-probe", "release", "{}");
            Pump(() => replies.Count > previousReplies);
            Assert(replies.Last().Contains("permission") && Counts().Executed == before.Executed, "revocation across JS await did not prevent execution");
            Assert(Send("!restricted").Contains("permission"), "removed role remained authorized");
            Assert(Send("!public", unverified) == "executed", "empty roles array imposed a requirement");
            host.Reload();
            Assert(!host.HasErrors && registry.List().Length == 5, "reload leaked or duplicated role commands");
            Assert(Send("!restricted").Contains("permission") && Counts() == (0, 0), "reload lost role requirements");
            Console.WriteLine("PASS: JS command role gates, aliases, any-role matching, permissions, async revocation and reload");
        }
        finally { host.Dispose(); Directory.Delete(root, true); }
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Log : IJsLog
    { public void Write(string level, string plugin, string message) { if (level == "error") Console.Error.WriteLine(message); } }
}
