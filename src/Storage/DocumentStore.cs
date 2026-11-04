using System.Collections.Generic;

namespace ValheimJS.Storage;

/// <summary>Default plugin database. Host services use separate DocumentDatabase instances.</summary>
internal static class DocumentStore
{
    private static DocumentDatabase _database = new("");
    internal static void Configure(string root) => _database = new DocumentDatabase(root);
    internal static string? Get(string owner, string collection, string id) => _database.Get(owner, collection, id);
    internal static bool Has(string owner, string collection, string id) => _database.Has(owner, collection, id);
    internal static string All(string owner, string collection) => _database.All(owner, collection);
    internal static string Keys(string owner, string collection) => _database.Keys(owner, collection);
    internal static List<string> Raw(string owner, string collection) => _database.Raw(owner, collection);
    internal static int Count(string owner, string collection) => _database.Count(owner, collection);
    internal static bool Put(string owner, string collection, string id, string json) => _database.Put(owner, collection, id, json);
    internal static bool Delete(string owner, string collection, string id) => _database.Delete(owner, collection, id);
    internal static int Clear(string owner, string collection) => _database.Clear(owner, collection);
    internal static string Names(string owner) => _database.Names(owner);
    internal static string Quote(string value) => DocumentDatabase.Quote(value);
}
