using System;
using System.IO;
using System.Linq;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

/// <summary>
/// Sandboxed file access. <b>Scoped</b>: every call is bound to the calling plugin, so a plugin
/// can only reach <c>&lt;root&gt;/data/&lt;plugin&gt;</c>. Escaping it throws.
/// </summary>
[JsBinder("fs", Phase = JsBindPhase.Engine, Order = 20,
    Description = "A plugin's own data folder.")]
internal sealed class FileSystemBindings : JsBindings
{
    public FileSystemBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding(Scoped = true, Description = "Absolute path of this plugin's data folder.")]
    public string Root(string plugin) => Context.DataDirectoryFor(plugin);

    [JsBinding(Scoped = true, Description = "True when a file or folder exists.")]
    public bool Exists(string plugin, string path)
    {
        var full = Resolve(plugin, path);
        return File.Exists(full) || Directory.Exists(full);
    }

    [JsBinding(Scoped = true, Returns = "string | null", Description = "File text, or null when missing.")]
    public string? Read(string plugin, string path)
    {
        var full = Resolve(plugin, path);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    [JsBinding(Scoped = true, Description = "Writes a file, creating parent folders.")]
    public void Write(string plugin, string path, string content)
    {
        var full = Resolve(plugin, path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(full, content);
    }

    [JsBinding(Scoped = true, Description = "Appends text, creating parent folders.")]
    public void Append(string plugin, string path, string content)
    {
        var full = Resolve(plugin, path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(full, content);
    }

    [JsBinding(Scoped = true, Description = "Entry names in a folder, sorted; empty when it does not exist.")]
    public string[] List(string plugin, string path)
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

    [JsBinding(Scoped = true, Description = "Creates a folder, including parents.")]
    public void Mkdir(string plugin, string path) => Directory.CreateDirectory(Resolve(plugin, path));

    [JsBinding(Scoped = true, Description = "Deletes a file, or a folder recursively.")]
    public void Delete(string plugin, string path)
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

    [JsBinding(Scoped = true, Description = "Joins two path segments with forward slashes.")]
    public string Join(string plugin, string left, string right) =>
        Path.Combine(left, right).Replace('\\', '/');

    private string Resolve(string plugin, string path) => ResolveCore(Context.DataDirectoryFor(plugin), path);

    private static string ResolveCore(string root, string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path ?? string.Empty));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"path '{path}' escapes this plugin's data sandbox");
        }

        return full;
    }
}
