using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ValheimJS.Services;

namespace ValheimJS.Commands;

/// <summary>Process-owned native commands. Every action is authorized again before execution.</summary>
public sealed class ManagementCommands : IDisposable
{
    public const string ManagePermission = "valheimjs.manage";
    private readonly Core.ChatCommandScope _scope;

    public ManagementCommands(Core.ChatCommands registry, AccountService accounts, PermissionService permissions,
        Func<CommandUser, bool> isGameAdmin, Action<string>? audit = null)
    {
        _scope = registry.OpenScope();
        try
        {
            _scope.Register("host", new RolesCommand(accounts, permissions, isGameAdmin, audit));
            _scope.Register("host", new PermissionsCommand(accounts, permissions, isGameAdmin, audit));
            _scope.Register("host", new UsersCommand(accounts, permissions, isGameAdmin, audit));
        }
        catch { _scope.Dispose(); throw; }
    }
    public void Dispose() => _scope.Dispose();

    private abstract class ManagementCommand : ChatCommand
    {
        protected readonly AccountService Accounts;
        protected readonly PermissionService Permissions;
        private readonly Func<CommandUser, bool> _isGameAdmin;
        private readonly Action<string> _audit;
        protected ManagementCommand(string name, AccountService accounts, PermissionService permissions,
            Func<CommandUser, bool> isGameAdmin, Action<string>? audit)
            : base(new ChatCommandDefinition { Name = name, Namespace = "manage", Description = "Private administrative management commands." })
        { Accounts = accounts; Permissions = permissions; _isGameAdmin = isGameAdmin; _audit = audit ?? (_ => { }); }

        public override bool CanAccess(CommandUser user) => Accounts.Available && user.AccountId != null
            && (_isGameAdmin(user) || user.HasPermission(ManagePermission));
        private void RequireAccess(CommandUser user)
        {
            if (!CanAccess(user))
                throw new UnauthorizedAccessException("Management requires Valheim admin access or the 'valheimjs.manage' permission.");
        }
        public override Task Validate(ChatCommandContext ctx)
        {
            RequireAccess(ctx.User);
            Arguments.Parse(ctx); // Verify syntax without doing work.
            return Task.CompletedTask;
        }
        public override Task Execute(ChatCommandContext ctx)
        {
            RequireAccess(ctx.User);
            try
            {
                var input = Arguments.Parse(ctx);
                var reply = ExecuteAction(ctx, input);
                if (input.Action is not ("list" or "show" or "check" or "help"))
                    _audit($"[manage] actor={ctx.User.AccountId} command=manage:{Definition.Name} action={input.Action} target={input.Target ?? input.Optional("name") ?? ctx.User.AccountId} role={input.Optional("role")} permission={input.Optional("permission")}");
                return ctx.User.SendMessage(reply);
            }
            catch (ArgumentException error) { return ctx.User.SendMessage(error.Message); }
        }
        protected abstract string ExecuteAction(ChatCommandContext ctx, Arguments input);
        public override string GetHelp(string query)
        {
            var action = query.Trim().ToLowerInvariant();
            if (action == "list") return $"!manage:{Definition.Name} list [page=1]\nLists eight records per page.";
            return (Definition.Name, action) switch
            {
                ("roles", "create") => "!manage:roles create name=foo [permissions=\"chat.use,bank.read\"] [description=\"...\"]\nCreates a role; existing names are rejected.",
                ("roles", "update") => "!manage:roles update name=foo [permissions=\"chat.use,bank.read\"] [description=\"...\"]\nSupply at least one change. permissions= replaces the list; permissions=\"\" clears it.",
                ("roles", "delete") => "!manage:roles delete name=foo\nUnassign the role from every user first.",
                ("permission", "create") => "!manage:permission create name=bank.read [description=\"...\"]\nDescribes a permission; grants no access by itself.",
                ("permission", "update") => "!manage:permission update name=bank.read description=\"...\"\nUpdates an existing permission's description.",
                ("permission", "delete") => "!manage:permission delete name=bank.read\nRemove references from roles and user grants/denials first.",
                ("user", "update") => "!manage:user update <selector> [displayName=\"...\"] [locale=en-GB]\nChanges profile fields; identity fields remain host-owned.",
                ("user", "assign") or ("user", "unassign") => $"!manage:user {action} <selector> role=staff\nSelect with id=, platform=, name= or self=true.",
                ("user", "grant") or ("user", "deny") or ("user", "revoke") or ("user", "check") => $"!manage:user {action} <selector> permission=bank.read\nSelect with id=, platform=, name= or self=true. Revoke removes direct overrides; role grants may still apply.",
                ("roles", _) => "!manage:roles list | show/create/update/delete name=foo [permissions=\"chat.use,bank.read\"] [description=\"...\"]",
                ("permission", _) => "!manage:permission list | show/create/update/delete name=bank.read [description=\"...\"]",
                _ => "!manage:user list | show | update | assign | unassign | grant | deny | revoke | check\nSelect with id=, platform=, name= or self=true; actions use role=, permission=, displayName= or locale=."
            };
        }
        protected static string Join(IEnumerable<string> values) => string.Join(", ", values.DefaultIfEmpty("(none)"));
        protected static string Page(Arguments input, IEnumerable<string> lines)
        {
            var page = input.Optional("page") ?? "1";
            if (!int.TryParse(page, out var number) || number < 1) throw new ArgumentException("page must be a positive integer.");
            var values = lines.ToArray();
            var count = Math.Max(1, (values.Length + 7) / 8);
            if (number > count) throw new ArgumentException($"Page exceeds {count} available page(s).");
            return $"Page {number}/{count} ({values.Length} total):\n" + Join(values.Skip((number - 1) * 8).Take(8));
        }
    }

    private sealed class RolesCommand : ManagementCommand
    {
        internal RolesCommand(AccountService a, PermissionService p, Func<CommandUser, bool> admin, Action<string>? audit) : base("roles", a, p, admin, audit) { }
        protected override string ExecuteAction(ChatCommandContext ctx, Arguments input)
        {
            switch (input.Action)
            {
                case "help": input.Allow(); return GetHelp("");
                case "list": input.Allow("page"); return Page(input, Permissions.Roles().OrderBy(role => role.Name).Select(role => role.Name));
                case "show":
                    input.Allow("name");
                    var role = Permissions.Roles().SingleOrDefault(item => item.Name.Equals(input.Required("name"), StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException("Role does not exist.");
                    return $"Role {role.Name}: {role.Description}\nPermissions: {Join(role.Permissions)}";
                case "create":
                    input.Allow("name", "permissions", "description");
                    var created = Permissions.CreateRole(input.Required("name"), input.Csv("permissions"), input.Optional("description") ?? "");
                    return $"Created role '{created.Name}'.";
                case "update":
                    input.Allow("name", "permissions", "description");
                    if (!input.Has("permissions") && !input.Has("description")) throw new ArgumentException("Supply permissions= or description= to update the role.");
                    var updated = Permissions.UpdateRole(input.Required("name"), input.Has("permissions") ? input.Csv("permissions") : null, input.Optional("description"));
                    return $"Updated role '{updated.Name}'.";
                case "delete":
                    input.Allow("name"); Permissions.DeleteRole(input.Required("name")); return "Role deleted.";
                default: throw new ArgumentException("Unknown roles action. Use !manage:roles help.");
            }
        }
    }

    private sealed class PermissionsCommand : ManagementCommand
    {
        internal PermissionsCommand(AccountService a, PermissionService p, Func<CommandUser, bool> admin, Action<string>? audit) : base("permission", a, p, admin, audit) { }
        protected override string ExecuteAction(ChatCommandContext ctx, Arguments input)
        {
            switch (input.Action)
            {
                case "help": input.Allow(); return GetHelp("");
                case "list": input.Allow("page"); return Page(input, Permissions.Permissions().OrderBy(item => item.Name).Select(item => item.Name));
                case "show":
                    input.Allow("name");
                    var permission = Permissions.Permissions().SingleOrDefault(item => item.Name.Equals(input.Required("name"), StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException("Permission does not exist.");
                    return $"Permission {permission.Name}: {permission.Description}";
                case "create":
                    input.Allow("name", "description");
                    var created = Permissions.CreatePermission(input.Required("name"), input.Optional("description") ?? "");
                    return $"Created permission '{created.Name}'.";
                case "update":
                    input.Allow("name", "description");
                    var updated = Permissions.UpdatePermission(input.Required("name"), input.RequiredValue("description"));
                    return $"Updated permission '{updated.Name}'.";
                case "delete": input.Allow("name"); Permissions.DeletePermission(input.Required("name")); return "Permission deleted.";
                default: throw new ArgumentException("Unknown permission action. Use !manage:permission help.");
            }
        }
    }

    private sealed class UsersCommand : ManagementCommand
    {
        internal UsersCommand(AccountService a, PermissionService p, Func<CommandUser, bool> admin, Action<string>? audit) : base("user", a, p, admin, audit) { }
        protected override string ExecuteAction(ChatCommandContext ctx, Arguments input)
        {
            if (input.Action == "help")
            {
                input.Allow();
                return GetHelp("");
            }
            if (input.Action == "list")
            {
                input.Allow("page");
                return Page(input, Accounts.List().OrderBy(item => item.KnownName).Select(item => item.KnownName + " id=" + item.Id));
            }
            input.Allow("id", "platform", "name", "self", "role", "permission", "displayName", "locale");
            var account = SelectAccount(ctx, input);
            input.Target = account.Id;
            var access = Permissions.GetAccess(account.Id);
            switch (input.Action)
            {
                case "show":
                    input.Allow("id", "platform", "name", "self");
                    return $"{account.KnownName}: id={account.Id}, platform={account.PlatformId}\nDisplay name: {account.Profile.DisplayName}; locale: {account.Profile.Locale}\nRoles: {Join(access.Roles)}\nGrants: {Join(access.Grants)}\nDenials: {Join(access.Denials)}";
                case "update":
                    input.Allow("id", "platform", "name", "self", "displayName", "locale");
                    if (!input.Has("displayName") && !input.Has("locale")) throw new ArgumentException("Supply displayName= or locale= to update a user's profile.");
                    Accounts.SetProfile(account.Id, new AccountProfile { DisplayName = input.Optional("displayName") ?? account.Profile.DisplayName, Locale = input.Optional("locale") ?? account.Profile.Locale });
                    return $"Updated profile for '{account.KnownName}'.";
                case "assign": case "unassign":
                    input.Allow("id", "platform", "name", "self", "role");
                    var role = input.Required("role").ToLowerInvariant();
                    if (!Permissions.Roles().Any(item => item.Name == role)) throw new ArgumentException("Role does not exist.");
                    Permissions.SetAccess(account.Id, input.Action == "assign" ? access.Roles.Concat(new[] { role }).ToArray() : access.Roles.Where(item => item != role).ToArray(), access.Grants, access.Denials);
                    return $"Role '{role}' {(input.Action == "assign" ? "assigned to" : "removed from")} '{account.KnownName}'.";
                case "grant": case "deny": case "revoke":
                    input.Allow("id", "platform", "name", "self", "permission");
                    var key = input.Required("permission").ToLowerInvariant();
                    var grants = access.Grants.Where(item => item != key);
                    var denials = access.Denials.Where(item => item != key);
                    if (input.Action == "grant") grants = grants.Concat(new[] { key });
                    if (input.Action == "deny") denials = denials.Concat(new[] { key });
                    Permissions.SetAccess(account.Id, access.Roles, grants.ToArray(), denials.ToArray());
                    return $"Updated direct permission '{key}' for '{account.KnownName}'.";
                case "check":
                    input.Allow("id", "platform", "name", "self", "permission");
                    var permission = input.Required("permission");
                    return $"{account.KnownName}: {permission} = {(Permissions.HasPermission(account.Id, permission) ? "allowed" : "denied")}";
                default: throw new ArgumentException("Unknown user action. Use !manage:user help.");
            }
        }
        private Account SelectAccount(ChatCommandContext ctx, Arguments input)
        {
            var selectors = new[] { "id", "platform", "name", "self" }.Where(input.Has).ToArray();
            if (selectors.Length == 0 && input.Action == "show") return ctx.User.Account ?? throw new ArgumentException("Your account is unavailable.");
            if (selectors.Length != 1) throw new ArgumentException("Select exactly one user with id=, platform=, name= or self=true.");
            switch (selectors[0])
            {
                case "id": return Accounts.Get(input.Required("id")) ?? throw new ArgumentException("Account does not exist.");
                case "platform": return Accounts.FindByPlatform(input.Required("platform")) ?? throw new ArgumentException("No known account has that platform identity.");
                case "self":
                    if (input.Required("self") != "true") throw new ArgumentException("Use self=true to select your account.");
                    return ctx.User.Account ?? throw new ArgumentException("Your account is unavailable.");
                default:
                    var matches = Accounts.List().Where(item => item.KnownName.Equals(input.Required("name"), StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1) throw new ArgumentException(matches.Length == 0 ? "No user has that name." : "Name is ambiguous; select by id= or platform=.");
                    return matches[0];
            }
        }
    }

    private sealed class Arguments
    {
        private readonly IReadOnlyDictionary<string, string> _options;
        private Arguments(string action, IReadOnlyDictionary<string, string> options) { Action = action; _options = options; }
        internal string Action { get; }
        internal string? Target { get; set; }
        internal static Arguments Parse(ChatCommandContext ctx)
        {
            if (ctx.Options.Count != 0) throw new ArgumentException("Put the action before its options, for example !manage:roles create name=foo.");
            if (string.IsNullOrWhiteSpace(ctx.Text)) return new Arguments("help", new Dictionary<string, string>());
            var parsed = ChatCommandParser.Parse("!" + ctx.Text);
            if (!string.IsNullOrEmpty(parsed.Text)) throw new ArgumentException("Unexpected text. Use key=value options, with double quotes for spaces.");
            return new Arguments(parsed.Route, parsed.Options);
        }
        internal bool Has(string key) => _options.ContainsKey(key);
        internal string? Optional(string key) => _options.TryGetValue(key, out var value) ? value : null;
        internal string RequiredValue(string key) => Optional(key) ?? throw new ArgumentException($"Missing {key}= option.");
        internal string Required(string key)
        {
            var value = RequiredValue(key);
            return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{key}= cannot be empty.") : value;
        }
        internal string[] Csv(string key)
        {
            var value = Optional(key);
            if (string.IsNullOrEmpty(value)) return Array.Empty<string>();
            var entries = value.Split(',').Select(item => item.Trim()).ToArray();
            if (entries.Any(string.IsNullOrEmpty)) throw new ArgumentException($"{key}= needs comma-separated names without empty entries.");
            return entries;
        }
        internal void Allow(params string[] keys)
        {
            var unknown = _options.Keys.FirstOrDefault(key => !keys.Contains(key, StringComparer.OrdinalIgnoreCase));
            if (unknown != null) throw new ArgumentException($"Unknown option '{unknown}'.");
        }
    }
}
