using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using ValheimJS.Networking;
using ValheimJS.Storage;

namespace ValheimJS.Services;

[DataContract]
public sealed class AccountAccess
{
    [DataMember(Name = "accountId")] public string AccountId { get; set; } = "";
    [DataMember(Name = "roles")] public string[] Roles { get; set; } = Array.Empty<string>();
    [DataMember(Name = "grants")] public string[] Grants { get; set; } = Array.Empty<string>();
    [DataMember(Name = "denials")] public string[] Denials { get; set; } = Array.Empty<string>();
}

[DataContract]
public sealed class AccountRole
{
    [DataMember(Name = "name")] public string Name { get; set; } = "";
    [DataMember(Name = "description")] public string Description { get; set; } = "";
    [DataMember(Name = "permissions")] public string[] Permissions { get; set; } = Array.Empty<string>();
}

[DataContract]
public sealed class PermissionDefinition
{
    [DataMember(Name = "name")] public string Name { get; set; } = "";
    [DataMember(Name = "description")] public string Description { get; set; } = "";
}

/// <summary>Owns access policy independently of account profiles and shared plugin metadata.
/// Native mutation methods are trusted host APIs; scripts only receive access queries.</summary>
public sealed class PermissionService
{
    private readonly DocumentDatabase _database;
    private readonly AccountService _accounts;
    private bool _defaultsReady;
    public PermissionService(string root, AccountService accounts)
    { _database = new DocumentDatabase(root); _accounts = accounts; }
    public bool Available => _accounts.Available;

    public AccountAccess GetAccess(string id)
    {
        RequireAuthority();
        var account = _accounts.Get(id) ?? throw new ArgumentException("Account does not exist.");
        var json = _database.Get("core", "access", account.Id);
        if (json != null) return NetworkJson.Read<AccountAccess>(json);
        var access = new AccountAccess
        {
            AccountId = account.Id,
            Roles = _database.Has("core", "roles", "player") ? new[] { "player" } : Array.Empty<string>()
        };
        _database.Put("core", "access", account.Id, NetworkJson.Write(access));
        return access;
    }
    public AccountRole[] Roles()
    {
        RequireAuthority();
        return _database.Raw("core", "roles").Select(NetworkJson.Read<AccountRole>).ToArray();
    }
    public void DefineRole(string name, string[] permissions)
    {
        RequireAuthority();
        var role = new AccountRole { Name = Token(name), Permissions = Tokens(permissions, true) };
        _database.Put("core", "roles", role.Name, NetworkJson.Write(role));
    }
    public AccountRole CreateRole(string name, string[] permissions, string description = "")
    {
        RequireAuthority();
        var key = Token(name);
        if (Roles().Any(role => role.Name == key)) throw new ArgumentException($"Role '{key}' already exists.");
        var role = new AccountRole { Name = key, Permissions = Tokens(permissions, true), Description = Description(description) };
        _database.Put("core", "roles", key, NetworkJson.Write(role));
        return role;
    }
    public AccountRole UpdateRole(string name, string[]? permissions = null, string? description = null)
    {
        var key = Token(name);
        var role = Roles().SingleOrDefault(item => item.Name == key) ?? throw new ArgumentException($"Role '{key}' does not exist.");
        if (permissions != null) role.Permissions = Tokens(permissions, true);
        if (description != null) role.Description = Description(description);
        _database.Put("core", "roles", key, NetworkJson.Write(role));
        return role;
    }
    public void DeleteRole(string name)
    {
        var key = Token(name);
        if (!Roles().Any(role => role.Name == key)) throw new ArgumentException($"Role '{key}' does not exist.");
        if (AccessRecords().Any(access => access.Roles.Contains(key))) throw new ArgumentException("Role is assigned to users; unassign it before deleting it.");
        _database.Delete("core", "roles", key);
    }
    public PermissionDefinition[] Permissions()
    {
        RequireAuthority();
        return _database.Raw("core", "permissions").Select(NetworkJson.Read<PermissionDefinition>).ToArray();
    }
    public PermissionDefinition CreatePermission(string name, string description = "")
    {
        var key = Token(name);
        if (Permissions().Any(item => item.Name == key)) throw new ArgumentException($"Permission '{key}' already exists.");
        var permission = new PermissionDefinition { Name = key, Description = Description(description) };
        _database.Put("core", "permissions", key, NetworkJson.Write(permission));
        return permission;
    }
    public PermissionDefinition UpdatePermission(string name, string description)
    {
        var key = Token(name);
        var permission = Permissions().SingleOrDefault(item => item.Name == key) ?? throw new ArgumentException($"Permission '{key}' does not exist.");
        permission.Description = Description(description);
        _database.Put("core", "permissions", key, NetworkJson.Write(permission));
        return permission;
    }
    public void DeletePermission(string name)
    {
        var key = Token(name);
        if (!Permissions().Any(item => item.Name == key)) throw new ArgumentException($"Permission '{key}' does not exist.");
        if (Roles().Any(role => role.Permissions.Contains(key)) || AccessRecords().Any(access => access.Grants.Contains(key) || access.Denials.Contains(key)))
            throw new ArgumentException("Permission is referenced by roles or users; remove those references before deleting it.");
        _database.Delete("core", "permissions", key);
    }
    private AccountAccess[] AccessRecords()
    {
        RequireAuthority();
        return _database.Raw("core", "access").Select(NetworkJson.Read<AccountAccess>).ToArray();
    }
    private static string Description(string value)
    {
        if (value == null || value.Length > 256 || value.Any(char.IsControl)) throw new ArgumentException("Description must be at most 256 characters without control characters.");
        return value;
    }
    public AccountAccess SetAccess(string id, string[] roles, string[] grants, string[] denials)
    {
        var access = GetAccess(id);
        var assigned = Tokens(roles, false);
        var defined = Roles().Select(role => role.Name).ToArray();
        if (assigned.Any(role => !defined.Contains(role))) throw new ArgumentException("Define roles before assigning them.");
        access.Roles = assigned;
        access.Grants = Tokens(grants, true);
        access.Denials = Tokens(denials, true);
        _database.Put("core", "access", access.AccountId, NetworkJson.Write(access));
        return access;
    }
    public bool HasRole(string id, string role) => GetAccess(id).Roles.Contains(Token(role));
    public bool HasPermission(string id, string permission)
    {
        var access = GetAccess(id);
        var key = Token(permission);
        if (access.Denials.Contains(key) || access.Denials.Contains("*")) return false;
        if (access.Grants.Contains(key) || access.Grants.Contains("*")) return true;
        return Roles().Where(role => access.Roles.Contains(role.Name))
            .Any(role => role.Permissions.Contains(key) || role.Permissions.Contains("*"));
    }
    private void RequireAuthority()
    {
        if (!Available) throw new InvalidOperationException("PermissionService requires an active session with server authority.");
        if (_defaultsReady) return;
        if (!_database.Has("core", "settings", "default-roles"))
        {
            foreach (var role in new[]
            {
                new AccountRole { Name = "admin", Description = "Administrators", Permissions = new[] { "*" } },
                new AccountRole { Name = "staff", Description = "Staff", Permissions = Array.Empty<string>() },
                new AccountRole { Name = "player", Description = "Players", Permissions = Array.Empty<string>() }
            })
            {
                if (!_database.Has("core", "roles", role.Name))
                    _database.Put("core", "roles", role.Name, NetworkJson.Write(role));
            }
            // Persist only after all definitions exist. Later edits/deletions remain deliberate.
            _database.Put("core", "settings", "default-roles", "{\"initialized\":true}");
        }
        _defaultsReady = true;
    }
    private static string Token(string value)
    {
        if (value == null || !Regex.IsMatch(value, @"\A[a-zA-Z0-9][a-zA-Z0-9._:-]{0,95}\z")) throw new ArgumentException("Invalid role or permission name.");
        return value.ToLowerInvariant();
    }
    private static string[] Tokens(string[] values, bool wildcard)
    {
        if (values == null || values.Length > 128) throw new ArgumentException("At most 128 roles or permissions may be supplied.");
        return values.Select(value => wildcard && value == "*" ? "*" : Token(value)).Distinct().ToArray();
    }
}
