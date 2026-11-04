using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ValheimJS.Storage;

/// <summary>
/// Per-plugin document store. Each collection is a folder and each document is one JSON file,
/// so writes are atomic per document and the core never has to parse JSON: the SDK owns the
/// document shape, C# owns durability.
/// <para>
/// Layout: <c>&lt;data&gt;/&lt;plugin&gt;/db/&lt;collection&gt;/&lt;id&gt;.json</c>.
/// Docs are cached in memory after the first read; values are handed out as raw JSON text.
/// </para>
/// </summary>
internal static class DocumentStore
{
    private sealed class Cache
    {
        public readonly Dictionary<string, string> Documents = new(StringComparer.Ordinal);
        public readonly HashSet<string> Dirty = new(StringComparer.Ordinal);
        public bool Loaded;
    }

    private static readonly Dictionary<string, Cache> Collections = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>UTF-8 without BOM: documents must stay readable by any JSON tool.</summary>
    private static readonly UTF8Encoding Utf8 = new(false);
    private static string _dataRoot = "";

    internal static void Configure(string dataRoot)
    {
        lock (Gate)
        {
            Collections.Clear();
            _dataRoot = dataRoot ?? "";
        }
    }

    private static string CollectionDir(string plugin, string collection) =>
        Path.Combine(_dataRoot, Sanitize(plugin), "db", Sanitize(collection));

    private static string DocPath(string plugin, string collection, string id) =>
        Path.Combine(CollectionDir(plugin, collection), Sanitize(id) + ".json");

    /// <summary>Filesystem-safe key: ids are opaque, so anything unusual is escaped, not rejected.</summary>
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '_' + ((int)character).ToString("x4"));
        }

        var text = builder.ToString();
        if (text.Length == 0)
        {
            return "_";
        }

        // keep names inside Windows path limits while staying deterministic
        if (text.Length > 100)
        {
            uint hash = 2166136261;
            foreach (var character in value)
            {
                hash = (hash ^ character) * 16777619;
            }

            return text.Substring(0, 80) + "-" + hash.ToString("x8");
        }

        return text;
    }

    private static Cache Load(string plugin, string collection)
    {
        var key = plugin + "\u0000" + collection;
        if (Collections.TryGetValue(key, out var existing) && existing.Loaded)
        {
            return existing;
        }

        var cache = new Cache { Loaded = true };
        var directory = CollectionDir(plugin, collection);
        if (System.IO.Directory.Exists(directory))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    cache.Documents[Path.GetFileNameWithoutExtension(file)] = System.IO.File.ReadAllText(file, Encoding.UTF8);
                }
                catch (Exception exception)
                {
                    Plugin.Logger.LogWarning($"[db] could not read {file}: {exception.Message}");
                }
            }
        }

        Collections[key] = cache;
        return cache;
    }

    private static string Key(string plugin, string collection, string id) => plugin + "\u0000" + collection;

    internal static string? Get(string plugin, string collection, string id)
    {
        lock (Gate)
        {
            var cache = Load(plugin, collection);
            return cache.Documents.TryGetValue(Sanitize(id), out var json) ? json : null;
        }
    }

    internal static bool Has(string plugin, string collection, string id)
    {
        lock (Gate)
        {
            return Load(plugin, collection).Documents.ContainsKey(Sanitize(id));
        }
    }

    /// <summary>All documents in the collection as one JSON array, which is what scripts consume.</summary>
    internal static string All(string plugin, string collection)
    {
        lock (Gate)
        {
            var cache = Load(plugin, collection);
            return "[" + string.Join(",", cache.Documents.Values) + "]";
        }
    }

    internal static string Keys(string plugin, string collection)
    {
        lock (Gate)
        {
            var cache = Load(plugin, collection);
            return "[" + string.Join(",", cache.Documents.Keys.Select(key => Quote(key))) + "]";
        }
    }

    /// <summary>Raw document text, for host-side predicate/patch loops that never build one big array.</summary>
    internal static List<string> Raw(string plugin, string collection)
    {
        lock (Gate)
        {
            return new List<string>(Load(plugin, collection).Documents.Values);
        }
    }

    internal static int Count(string plugin, string collection)
    {
        lock (Gate)
        {
            return Load(plugin, collection).Documents.Count;
        }
    }

    internal static bool Put(string plugin, string collection, string id, string json)
    {
        var key = Sanitize(id);
        lock (Gate)
        {
            var cache = Load(plugin, collection);
            var existed = cache.Documents.ContainsKey(key);
            cache.Documents[key] = json;
            Write(plugin, collection, key, json);
            return !existed;
        }
    }

    internal static bool Delete(string plugin, string collection, string id)
    {
        var key = Sanitize(id);
        lock (Gate)
        {
            var cache = Load(plugin, collection);
            if (!cache.Documents.Remove(key))
            {
                return false;
            }

            var file = DocPath(plugin, collection, key);
            try
            {
                if (System.IO.File.Exists(file))
                {
                    System.IO.File.Delete(file);
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[db] could not delete {file}: {exception.Message}");
            }

            return true;
        }
    }

    internal static int Clear(string plugin, string collection)
    {
        lock (Gate)
        {
            var cache = Load(plugin, collection);
            var removed = cache.Documents.Count;
            if (removed == 0)
            {
                return 0;
            }

            cache.Documents.Clear();
            var directory = CollectionDir(plugin, collection);
            try
            {
                if (System.IO.Directory.Exists(directory))
                {
                    foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.json"))
                    {
                        System.IO.File.Delete(file);
                    }
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[db] could not clear {directory}: {exception.Message}");
            }

            return removed;
        }
    }

    /// <summary>Collection folder names, so a plugin can enumerate its own tables.</summary>
    internal static string Names(string plugin)
    {
        lock (Gate)
        {
            var root = Path.Combine(_dataRoot, Sanitize(plugin), "db");
            if (!System.IO.Directory.Exists(root))
            {
                return "[]";
            }

            return "[" + string.Join(",", System.IO.Directory.GetDirectories(root)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => Quote(name!))) + "]";
        }
    }

    /// <summary>Writes through a temp file so a crash mid-write cannot truncate a document.</summary>
    private static void Write(string plugin, string collection, string key, string json)
    {
        var file = DocPath(plugin, collection, key);
        try
        {
            var directory = Path.GetDirectoryName(file)!;
            System.IO.Directory.CreateDirectory(directory);
            var temp = file + ".tmp";
            System.IO.File.WriteAllText(temp, json, Utf8);
            if (System.IO.File.Exists(file))
            {
                System.IO.File.Replace(temp, file, null);
            }
            else
            {
                System.IO.File.Move(temp, file);
            }
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"[db] could not write {file}: {exception.Message}");
        }
    }

    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
