using System.Collections.Concurrent;
using ValheimJS.Bindings;
using ValheimJS.Commands;
using ValheimJS.Services;
using ValheimJS.Scripting;

internal static class AccountProbe
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-accounts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var authority = true;
            var accounts = new AccountService(Path.Combine(root, "accounts"), () => authority);
            var permissions = new PermissionService(Path.Combine(root, "permissions"), accounts);
            var account = accounts.Observe("42", "Steam_76561198000000000", "Player");
            Assert(permissions.Roles().Select(role => role.Name).OrderBy(name => name).SequenceEqual(new[] { "admin", "player", "staff" }), "default role definitions missing");
            Assert(permissions.GetAccess(account.Id).Roles.SequenceEqual(new[] { "player" }), "default player assignment missing");
            Assert(!permissions.HasPermission(account.Id, "anything.use"), "player received privileged defaults");
            Assert(accounts.FromPeer("42")!.Id == account.Id, "session account missing");
            Assert(accounts.FindByPlatform("76561198000000000")!.Id == account.Id, "platform normalization");
            Assert(accounts.Resolve("steam:76561198000000000", "Renamed").Id == account.Id, "rename changed identity");
            accounts.SetProfile(account.Id, new AccountProfile { DisplayName = "Display", Locale = "en-GB" });
            var detached = accounts.Get(account.Id)!;
            detached.Profile.DisplayName = "tampered";
            Assert(accounts.Get(account.Id)!.Profile.DisplayName == "Display", "mutable profile escaped");
            Throws(() => accounts.Resolve("127.0.0.1", "Unknown"), "IP accepted as identity");
            Throws(() => accounts.SetProfile(account.Id, new AccountProfile { DisplayName = new string('x', 81) }), "unbounded profile");

            var bank = accounts.ForPlugin("bank");
            var shop = accounts.ForPlugin("shop");
            bank.SetData(account.Id, "{\"balance\":42,\"details\":{\"currency\":\"coins\"}}");
            Assert(shop.GetData(account.Id) == null, "own data leaked another publisher");
            Assert(shop.ReadPublishedData(account.Id, "bank")!.Contains("42"), "published data unreadable");
            shop.SetData(account.Id, "{\"balance\":1}");
            Assert(bank.GetData(account.Id)!.Contains("42"), "another publisher overwrote bank");
            foreach (var json in new[] { "[]", "null", "{broken", "{} trailing", "{\"x\":\"" + new string('x', 17000) + "\"}" })
                Throws(() => bank.SetData(account.Id, json), "invalid or oversized metadata");
            Assert(bank.GetData(account.Id)!.Contains("42"), "failed metadata write modified value");
            shop.DeleteData(account.Id);
            Assert(shop.GetData(account.Id) == null && bank.GetData(account.Id) != null, "delete crossed publisher boundary");

            permissions.DefineRole("Moderator", new[] { "chat.use", "bank.read" });
            permissions.SetAccess(account.Id, new[] { "MODERATOR" }, new[] { "custom.use" }, new[] { "bank.read" });
            Assert(permissions.HasRole(account.Id, "moderator"), "role assignment");
            Assert(permissions.HasPermission(account.Id, "CHAT.USE") && permissions.HasPermission(account.Id, "custom.use"), "role/direct grants");
            Assert(!permissions.HasPermission(account.Id, "bank.read") && !permissions.HasPermission(account.Id, "unknown"), "deny/default policy");
            Throws(() => permissions.SetAccess(account.Id, new[] { "undefined" }, Array.Empty<string>(), Array.Empty<string>()), "undefined role assigned");
            permissions.GetAccess(account.Id).Roles[0] = "admin";
            Assert(permissions.HasRole(account.Id, "moderator"), "mutable policy escaped");
            permissions.DefineRole("admin", new[] { "*" });
            permissions.SetAccess(account.Id, new[] { "admin" }, Array.Empty<string>(), new[] { "dangerous.use" });
            Assert(permissions.HasPermission(account.Id, "anything.use") && !permissions.HasPermission(account.Id, "dangerous.use"), "wildcard/deny precedence");

            // Reopen from disk, independent of the default plugin document database.
            ValheimJS.Storage.DocumentStore.Configure(Path.Combine(root, "plugin-data"));
            var reopened = new AccountService(Path.Combine(root, "accounts"), () => authority);
            var reopenedPermissions = new PermissionService(Path.Combine(root, "permissions"), reopened);
            Assert(reopened.FindByPlatform("Steam_76561198000000000")!.Id == account.Id, "account did not persist");
            Assert(reopened.ForPlugin("shop").ReadPublishedData(account.Id, "bank")!.Contains("42"), "metadata did not persist");
            Assert(reopenedPermissions.HasRole(account.Id, "admin"), "access did not persist separately");
            reopenedPermissions.UpdateRole("staff", new[] { "staff.custom" });
            reopenedPermissions.DeleteRole("player");
            var thirdPermissions = new PermissionService(Path.Combine(root, "permissions"), reopened);
            Assert(thirdPermissions.Roles().Single(role => role.Name == "staff").Permissions.SequenceEqual(new[] { "staff.custom" }), "restart reset customized defaults");
            Assert(!thirdPermissions.Roles().Any(role => role.Name == "player"), "restart recreated intentionally deleted default");
            var freshAccount = reopened.Resolve("Steam_76561198000000001", "Fresh");
            Assert(thirdPermissions.GetAccess(freshAccount.Id).Roles.Length == 0, "deleted player role was assigned to a new account");
            accounts.RetainSessions(Array.Empty<string>());
            Assert(accounts.FromPeer("42") == null && accounts.Get(account.Id) != null, "disconnect deleted account");

            authority = false;
            Assert(!accounts.Available && !permissions.Available, "authority did not transition");
            Throws(() => accounts.Get(account.Id), "client read host account store");
            Throws(() => bank.SetData(account.Id, "{}"), "client wrote metadata");
            Throws(() => permissions.HasRole(account.Id, "admin"), "client read policy");
            authority = true;
            var blockedRoot = Path.Combine(root, "blocked");
            File.WriteAllText(blockedRoot, "this is a file, not a database directory");
            var blocked = new ValheimJS.Storage.DocumentDatabase(blockedRoot);
            Throws(() => blocked.Put("test", "records", "one", "{}"), "failed write reported success");
            Assert(blocked.Get("test", "records", "one") == null, "failed write replaced cached data");
            CommandAccess(accounts, permissions, account.Id);
            ScriptAccess(root, accounts, permissions, account.Id);
            Console.WriteLine("PASS: persistent accounts, shared publisher data, authority, separate permissions, command gating and scoped JS API");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: account probe: " + error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void CommandAccess(AccountService accounts, PermissionService permissions, string id)
    {
        var queue = new ConcurrentQueue<Action>();
        using var registry = new ValheimJS.Core.ChatCommands(queue.Enqueue);
        using var scope = registry.OpenScope();
        var replies = new List<string>();
        var user = new CommandUser(42, "Player", "Steam_76561198000000000", replies.Add, accounts, id, permissions);
        var validation = new TaskCompletionSource<bool>();
        var executed = 0;
        scope.Register("test", new RestrictedCommand(() => validation.Task, () => executed++));
        permissions.SetAccess(id, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        registry.Submit(user, "!restricted");
        Assert(replies.Last().Contains("permission") && executed == 0, "unauthorized command ran");
        permissions.SetAccess(id, new[] { "moderator" }, Array.Empty<string>(), Array.Empty<string>());
        registry.Submit(user, "!restricted");
        // Revoke while validate is pending: execute must check live policy again.
        permissions.SetAccess(id, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        validation.SetResult(true);
        while (queue.TryDequeue(out var action)) action();
        Assert(replies.Count == 2 && executed == 0, "revoked command executed after await");
        permissions.SetAccess(id, new[] { "moderator" }, Array.Empty<string>(), Array.Empty<string>());
        registry.Submit(user, "!restricted");
        while (queue.TryDequeue(out var action)) action();
        Assert(executed == 1 && user.HasRole("moderator") && user.HasPermission("chat.use"), "authorized command failed");
    }
    private static void ScriptAccess(string root, AccountService accounts, PermissionService permissions, string id)
    {
        var directory = Path.Combine(root, "host", "plugins", "shop");
        Directory.CreateDirectory(directory);
        var bankDirectory = Path.Combine(root, "host", "plugins", "bank-plugin");
        Directory.CreateDirectory(bankDirectory);
        File.WriteAllText(Path.Combine(bankDirectory, "manifest.ini"), "name=bank-plugin\napiVersion=1\n");
        File.WriteAllText(Path.Combine(bankDirectory, "shared.js"), """
            (function(){
             var api=globalThis.__vjs_context.host.api.AccountService;
             var bankAccount={balance:0,deposit:function(amount){this.balance+=amount;}};
             globalThis.__vjs_plugin={__vjsPlugin:'object',value:{
              setup:function(){var user=api.findByPlatform('Steam_76561198000000000');api.setData(user.id,'{"accountId":"bank-account-42"}');},
              exports:{get_account:function(id){if(id!=='bank-account-42')throw Error('wrong bank account');return bankAccount;}}
             }};
            })();
            """);
        File.WriteAllText(Path.Combine(directory, "manifest.ini"), "name=shop\napiVersion=1\ndepends=bank-plugin@*\n");
        File.WriteAllText(Path.Combine(directory, "shared.js"), """
            var api=globalThis.__vjs_context.host.api;
            globalThis.__vjs_plugin={__vjsPlugin:'object',value:{setup:function(){
             var account=api.AccountService.findByPlatform('Steam_76561198000000000');
             if(!account || !api.PermissionService.hasPermission(account.id,'chat.use')) throw Error('account query failed');
             if(api.AccountService.setProfile || api.AccountService.setAccess || api.PermissionService.setAccess || api.PermissionService.defineRole) throw Error('management API exposed');
             if(api.AccountService.readPublishedData(account.id,'bank').balance!==42) throw Error('shared data unavailable');
             var link=api.AccountService.readPublishedData(account.id,'bank-plugin');
             var bank=globalThis.__vjs_context.dep('bank-plugin');
             var bankAccount=bank.get_account(link.accountId);
             bankAccount.deposit(10);
             if(bankAccount.balance!==10)throw Error('exported account operation failed');
             api.AccountService.setData(account.id,'{"visited":true}');
            }}};
            """);
        var host = new JsHost(Path.Combine(root, "host"), ProcessSide.Server, new Log(),
            bindingFilter: type => type == typeof(AccountBindings) || type == typeof(PermissionBindings), accounts: accounts, permissions: permissions);
        try
        {
            host.Load(); Assert(!host.HasErrors && host.LoadedNames.Count == 2, string.Join(" | ", host.Errors));
            Assert(accounts.ForPlugin("shop").GetData(id)!.Contains("visited"), "JS scoped write failed");
            Assert(accounts.ForPlugin("bank").GetData(id)!.Contains("42"), "JS modified another publisher");
            host.Reload(); Assert(!host.HasErrors, "reload disrupted account service");
        }
        finally { host.Dispose(); }
    }
    private sealed class RestrictedCommand : ChatCommand
    {
        private readonly Func<Task> _validate;
        private readonly Action _execute;
        internal RestrictedCommand(Func<Task> validate, Action execute) : base(new ChatCommandDefinition
            { Name = "restricted", Roles = new[] { "admin", "moderator" }, Permissions = new[] { "chat.use" } })
        { _validate = validate; _execute = execute; }
        public override Task Validate(ChatCommandContext ctx) => _validate();
        public override Task Execute(ChatCommandContext ctx) { _execute(); return Task.CompletedTask; }
    }
    private sealed class Log : IJsLog
    { public void Write(string level, string plugin, string message) { if (level == "error") Console.Error.WriteLine(message); } }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws(Action action, string message) { try { action(); } catch { return; } throw new Exception("Expected failure: " + message); }
}
