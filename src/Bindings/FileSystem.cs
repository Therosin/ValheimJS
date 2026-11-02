using System;
using ValheimJS.Scripting.Binding;
using System.IO;
using System.Linq;

namespace ValheimJS.Bindings;

/// <summary>
/// Sandboxed file access for scripts. <b>Scoped</b>: every call receives the calling plugin's
/// name first, so each plugin can only reach <c>server_plugins/data/&lt;plugin&gt;</c>.
/// Scripts see <c>host.api.fs.*</c>.
/// </summary>
[JsBinding("FileSystem", JsBindPhase.Engine, order: 20)]
internal static class FileSystemBindings
{
    private static Func<string, string> _dataDirectory = _ => throw new InvalidOperationException("filesystem binding not installed");

    public static void Bind(JsBindingContext context)
    {
        _dataDirectory = context.DataDirectoryFor;

        context.Add("fs.root", new Func<string, string>(Root), JsReturnKind.Raw, scoped: true);
        context.Add("fs.exists", new Func<string, string, bool>(Exists), JsReturnKind.Raw, scoped: true);
        context.Add("fs.read", new Func<string, string, string?>(ReadAllText), JsReturnKind.Raw, scoped: true);
        context.Add("fs.write", new Action<string, string, string>(WriteAllText), JsReturnKind.Raw, scoped: true);
        context.Add("fs.append", new Action<string, string, string>(Append), JsReturnKind.Raw, scoped: true);
        context.Add("fs.list", new Func<string, string, string[]>(List), JsReturnKind.Raw, scoped: true);
        context.Add("fs.mkdir", new Action<string, string>(CreateDirectory), JsReturnKind.Raw, scoped: true);
        context.Add("fs.delete", new Action<string, string>(Delete), JsReturnKind.Raw, scoped: true);
        context.Add("fs.join", new Func<string, string, string, string>((_, left, right) => Path.Combine(left, right).Replace('\\', '/')), JsReturnKind.Raw, scoped: true);
    }

    private static string Root(string plugin) => _dataDirectory(plugin);

    private static string Resolve(string plugin, string path)
    {
        var root = _dataDirectory(plugin);
        var full = Path.GetFullPath(Path.Combine(root, path ?? string.Empty));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"path '{path}' escapes this plugin's data sandbox");
        }

        return full;
    }

    private static bool Exists(string plugin, string path)
    {
        var full = Resolve(plugin, path);
        return File.Exists(full) || Directory.Exists(full);
    }

    private static string? ReadAllText(string plugin, string path)
    {
        var full = Resolve(plugin, path);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    private static void WriteAllText(string plugin, string path, string content)
    {
        var full = Resolve(plugin, path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(full, content);
    }

    private static void Append(string plugin, string path, string content)
    {
        var full = Resolve(plugin, path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(full, content);
    }

    private static string[] List(string plugin, string path)
    {
        var full = Resolve(plugin, path);
        if (!Directory.Exists(full))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFileSystemEntries(full)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    private static void CreateDirectory(string plugin, string path) => Directory.CreateDirectory(Resolve(plugin, path));

    private static void Delete(string plugin, string path)
    {
        var full = Resolve(plugin, path);
        if (File.Exists(full))
        {
            File.Delete(full);
        }
        else if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }
}
