using System.Collections.Concurrent;
using ValheimJS.Commands;
using ValheimJS.Services;

internal static class ManagementProbe
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-management-" + Guid.NewGuid().ToString("N"));
        try
        {
            var authority = true;
            var admin = true;
            var accounts = new AccountService(Path.Combine(root, "accounts"), () => authority);
            var permissions = new PermissionService(Path.Combine(root, "permissions"), accounts);
            var owner = accounts.Resolve("Steam_1", "Owner");
            var player = accounts.Resolve("Steam_2", "Some Player");
            var queue = new ConcurrentQueue<Action>();
            var replies = new List<string>();
            var audit = new List<string>();
            using var registry = new ValheimJS.Core.ChatCommands(queue.Enqueue);
            using var commands = new ManagementCommands(registry, accounts, permissions, user => admin && user.PeerId == 1, audit.Add);
            var ownerUser = new CommandUser(1, "Owner", "Steam_1", replies.Add, accounts, owner.Id, permissions);
            var ordinaryUser = new CommandUser(2, "Some Player", "Steam_2", replies.Add, accounts, player.Id, permissions);
            string Send(string text, CommandUser? caller = null)
            {
                var previous = replies.Count;
                Assert(registry.Submit(caller ?? ownerUser, text), "command was not consumed");
                var end = DateTime.UtcNow.AddSeconds(2);
                var stable = 0;
                do
                {
                    while (queue.TryDequeue(out var action)) action();
                    stable = replies.Count > previous && queue.IsEmpty ? stable + 1 : 0;
                    if (stable >= 5) return replies.Last();
                    Thread.Sleep(1);
                } while (DateTime.UtcNow < end);
                throw new Exception("Management reply timed out: " + text);
            }
            Assert(Send("!manage:roles create name=blocked", ordinaryUser).Contains("permission"), "unprivileged mutation allowed");
            Assert(permissions.Roles().Length == 3 && !permissions.Roles().Any(role => role.Name == "blocked"), "unauthorized role created");
            Assert(Send("!manage:roles create name=foo description=\"A quoted role\"").Contains("Created role"), "action/options parsing failed");
            Assert(Send("!manage:roles show name=FOO").Contains("A quoted role"), "case-insensitive role query");
            Assert(Send("!manage:roles create name=foo").Contains("already exists"), "duplicate role silently overwritten");
            Assert(Send("!manage:roles create name=bad typo=1").Contains("Unknown option"), "unknown option accepted");
            Assert(!permissions.Roles().Any(role => role.Name == "bad"), "invalid command mutated role");
            Assert(Send("!manage:roles create name=bad name=duplicate").Contains("Duplicate option"), "duplicate options accepted");
            Assert(Send("!manage:roles create name=bad trailing text").Contains("Unexpected text"), "free text accepted");
            Assert(Send("!manage:roles name=bad create").Contains("action before"), "wrong option placement accepted");
            Assert(Send("!manage:roles create name=\"unfinished").Contains("Malformed"), "unterminated quote accepted");
            Assert(Send("!manage:roles list page=1").Contains("foo"), "role listing");
            Assert(Send("!manage:roles list page=0").Contains("positive integer"), "invalid pagination");
            Assert(Send("!manage:permission create name=bank.read description=\"Read bank links\"").Contains("Created permission"), "permission create");
            Assert(Send("!manage:permission update name=bank.read description=\"Read shared links\"").Contains("Updated permission"), "permission update");
            Assert(Send("!manage:permission show name=bank.read").Contains("Read shared links"), "permission description missing");
            Assert(Send("!manage:permission update name=missing description=x").Contains("does not exist"), "update created missing permission");
            Assert(Send("!manage:permission update name=bank.read").Contains("Missing description"), "missing update field");
            Assert(Send("!manage:roles update name=foo permissions=bank.read").Contains("Updated role"), "role permission update");
            Assert(Send("!manage:user assign name=\"Some Player\" role=foo").Contains("assigned"), "role assignment");
            Assert(permissions.HasPermission(player.Id, "bank.read"), "role permission not effective");
            Assert(Send("!manage:roles delete name=foo").Contains("unassign"), "assigned role deleted");
            Assert(Send("!manage:permission delete name=bank.read").Contains("referenced"), "referenced permission deleted");
            Assert(Send("!manage:user deny platform=Steam_2 permission=bank.read").Contains("Updated direct"), "permission deny");
            Assert(!permissions.HasPermission(player.Id, "bank.read"), "denial ineffective");
            Assert(Send("!manage:user check platform=Steam_2 permission=bank.read").Contains("denied"), "check denied");
            Send("!manage:user revoke platform=Steam_2 permission=bank.read");
            Assert(permissions.HasPermission(player.Id, "bank.read"), "revoke did not restore inherited grant");
            Send("!manage:user grant platform=Steam_2 permission=valheimjs.manage");
            Assert(Send("!manage:roles list", ordinaryUser).Contains("foo"), "delegated management rejected");
            Send("!manage:user deny platform=Steam_2 permission=valheimjs.manage");
            Assert(Send("!manage:roles list", ordinaryUser).Contains("permission"), "management denial ineffective");
            Send("!manage:user update platform=Steam_2 displayName=\"Friendly Player\" locale=en-GB");
            Assert(accounts.Get(player.Id)!.Profile.DisplayName == "Friendly Player", "profile update failed");
            Assert(Send("!manage:user update platform=Steam_2 platformId=Steam_99").Contains("Unknown option"), "identity edit accepted");
            accounts.Resolve("Steam_3", "Some Player");
            Assert(Send("!manage:user show name=\"Some Player\"").Contains("ambiguous"), "ambiguous name selected arbitrarily");
            Assert(Send("!manage:user show self=true").Contains(owner.Id), "self selector");
            Assert(Send("!manage:user show id=" + player.Id + " platform=Steam_2").Contains("exactly one"), "conflicting selectors");
            Send("!manage:user unassign platform=Steam_2 role=foo");
            Send("!manage:roles update name=foo permissions=\"\"");
            Assert(Send("!manage:permission delete name=bank.read").Contains("deleted"), "unused permission deletion");
            Assert(Send("!manage:roles delete name=foo").Contains("deleted"), "unused role deletion");
            Assert(permissions.Roles().Length == 3 && permissions.Permissions().Length == 0, "deleted definitions remained");
            // An administrator's list access can disappear between validation and execution.
            registry.Submit(ownerUser, "!manage:roles create name=race");
            admin = false;
            for (var i = 0; i < 20; i++) { while (queue.TryDequeue(out var action)) action(); Thread.Sleep(1); }
            Assert(!permissions.Roles().Any(role => role.Name == "race"), "access was not rechecked before mutation");
            admin = true;
            Assert(Send("!manage:roles list").Contains("total"), "Valheim admin recovery failed");
            authority = false;
            Assert(Send("!manage:roles list").Contains("permission"), "client management allowed");
            authority = true;
            Assert(audit.Any(line => line.Contains("actor=" + owner.Id) && line.Contains("action=create")), "audit missing");
            Assert(!audit.Any(line => line.Contains("blocked")), "failed action audited as success");
            commands.Dispose();
            Assert(registry.List().Length == 0, "native commands leaked on disposal");
            Console.WriteLine("PASS: native management grammar, role/permission CRUD, user access edits, authorization, rechecks and cleanup");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: management probe: " + error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
