using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Serialization.Json;
using System.Xml;
using ValheimJS.Networking;
using ValheimJS.Storage;

namespace ValheimJS.Services;

/// <summary>Authoritative, persistent accounts. Native callers are trusted host code;
/// plugins receive an owner-bound scope for publishing small shared metadata records.</summary>
public sealed class AccountService
{
    private readonly DocumentDatabase _database;
    private readonly Func<bool> _authority;
    private readonly Dictionary<string, (string Account, string Platform, string Name)> _sessions = new();

    public AccountService(string root, Func<bool> authority)
    {
        _database = new DocumentDatabase(root);
        _authority = authority;
    }

    public bool Available => _authority();
    public Account? FromPeer(string peerId)
    {
        RequireAuthority();
        return _sessions.TryGetValue(peerId, out var session) ? Get(session.Account) : null;
    }
    internal Account Observe(string peerId, string platformId, string name)
    {
        RequireAuthority();
        var identity = NormalizePlatformId(platformId);
        if (_sessions.TryGetValue(peerId, out var session) && session.Platform == identity && session.Name == name)
            return RequireAccount(session.Account);
        var account = Resolve(identity, name);
        _sessions[peerId] = (account.Id, identity, name);
        return account;
    }
    internal void RetainSessions(IEnumerable<string> peers)
    {
        var active = new HashSet<string>(peers);
        foreach (var peer in _sessions.Keys.ToArray()) if (!active.Contains(peer)) _sessions.Remove(peer);
    }
    public AccountScope ForPlugin(string plugin)
    {
        if (string.IsNullOrWhiteSpace(plugin)) throw new ArgumentException("Plugin identity is required.");
        return new AccountScope(this, plugin);
    }

    public Account? Get(string id)
    {
        RequireAuthority();
        var json = _database.Get("core", "accounts", AccountId(id));
        return json == null ? null : NetworkJson.Read<Account>(json);
    }

    public Account[] List()
    {
        RequireAuthority();
        return _database.Raw("core", "accounts").Select(NetworkJson.Read<Account>).ToArray();
    }

    public Account? FindByPlatform(string platformId)
    {
        RequireAuthority();
        var identity = NormalizePlatformId(platformId);
        return _database.Raw("core", "accounts").Select(NetworkJson.Read<Account>)
            .SingleOrDefault(account => account.PlatformId == identity);
    }

    /// <summary>Called with a verified connection identity, never client-provided account JSON.</summary>
    public Account Resolve(string platformId, string knownName)
    {
        RequireAuthority();
        var identity = NormalizePlatformId(platformId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var account = FindByPlatform(identity) ?? new Account
        {
            Id = Guid.NewGuid().ToString("N"), PlatformId = identity, CreatedAt = now
        };
        account.KnownName = Text(knownName, 128, "known name");
        account.LastSeenAt = now;
        Save(account);
        return account;
    }

    public Account SetProfile(string id, AccountProfile profile)
    {
        var account = RequireAccount(id);
        account.Profile = new AccountProfile
        {
            DisplayName = Text(profile.DisplayName, 80, "display name"),
            Locale = Text(profile.Locale, 32, "locale")
        };
        Save(account);
        return account;
    }

    internal string? GetData(string plugin, string id)
    {
        RequireAccount(id);
        return _database.Get(Owner(plugin), "accounts", AccountId(id));
    }

    internal void SetData(string plugin, string id, string json)
    {
        RequireAccount(id);
        if (json == null || Encoding.UTF8.GetByteCount(json) > 16384) throw new ArgumentException("Account plugin data is limited to 16 KiB.");
        using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), new XmlDictionaryReaderQuotas { MaxDepth = 32 }))
        {
            reader.MoveToContent();
            if (reader.GetAttribute("type") != "object") throw new ArgumentException("Account plugin data must be a JSON object.");
            while (reader.Read()) { }
        }
        _database.Put(Owner(plugin), "accounts", AccountId(id), json);
    }

    internal void DeleteData(string plugin, string id)
    {
        RequireAccount(id);
        _database.Delete(Owner(plugin), "accounts", AccountId(id));
    }

    private Account RequireAccount(string id) => Get(id) ?? throw new ArgumentException("Account does not exist.");
    private void RequireAuthority()
    {
        if (!Available) throw new InvalidOperationException("AccountService requires an active session with server authority.");
    }
    private void Save(Account account) => _database.Put("core", "accounts", account.Id, NetworkJson.Write(account));
    private static string AccountId(string id) => Guid.TryParseExact(id, "N", out var value)
        ? value.ToString("N") : throw new ArgumentException("Invalid account id.");
    private static string Owner(string plugin)
    {
        using var hash = SHA256.Create();
        return "plugin-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(plugin))).Replace("-", "").ToLowerInvariant();
    }
    private static string Text(string value, int max, string field)
    {
        if (value == null || value.Length > max || value.Any(char.IsControl)) throw new ArgumentException($"Invalid {field}; maximum length is {max}.");
        return value;
    }
    public static string NormalizePlatformId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
            throw new ArgumentException("A stable platform identity is required.");
        if (value.All(char.IsDigit)) return "steam:" + value;
        var separator = value.IndexOfAny(new[] { '_', ':' });
        if (separator < 1 || separator == value.Length - 1 || !value.Substring(0, separator).All(char.IsLetterOrDigit))
            throw new ArgumentException("Platform identity must include its provider (for example Steam_7656...).");
        var provider = value.Substring(0, separator).ToLowerInvariant();
        var identity = value.Substring(separator + 1);
        if (provider == "steam" && !identity.All(char.IsDigit)) throw new ArgumentException("Invalid Steam identity.");
        return provider + ":" + identity;
    }
}

/// <summary>Plugins may read another publisher's shared data, but may only write their own.</summary>
public sealed class AccountScope
{
    private readonly AccountService _service;
    private readonly string _plugin;
    internal AccountScope(AccountService service, string plugin) { _service = service; _plugin = plugin; }
    public string? GetData(string id) => _service.GetData(_plugin, id);
    public string? ReadPublishedData(string id, string publisher) => _service.GetData(publisher, id);
    public void SetData(string id, string json) => _service.SetData(_plugin, id, json);
    public void DeleteData(string id) => _service.DeleteData(_plugin, id);
}
