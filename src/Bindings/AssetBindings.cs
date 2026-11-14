using System;
using System.IO;
using System.Linq;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

/// <summary>
/// Read-only access to files shipped inside the calling plugin's folder. Never writable and never
/// allowed outside that folder: the plugin can ship YAML, TOML or other text assets but cannot use
/// this surface to rewrite its own bundles or reach sibling plugins.
/// </summary>
[JsBinder("assets", Phase = JsBindPhase.Engine, Order = 21,
    Description = "Read-only access to files shipped with this plugin.")]
internal sealed class AssetBindings : JsBindings
{
    public AssetBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding(Scoped = true, Description = "Absolute path of this plugin's folder.")]
    public string Root(string plugin) => Context.PluginDirectoryFor(plugin);

    [JsBinding(Scoped = true, Description = "True when a shipped file or folder exists.")]
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

    [JsBinding(Scoped = true, Description = "Entry names in a shipped folder, sorted; empty when missing.")]
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

    private string Resolve(string plugin, string path) => ResolveCore(Context.PluginDirectoryFor(plugin), path);

    private static string ResolveCore(string root, string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path ?? string.Empty));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"path '{path}' escapes this plugin's folder");
        }

        return full;
    }
}
