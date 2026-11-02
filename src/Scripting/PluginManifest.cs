using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimJS.Scripting;

/// <summary>
/// One plugin's settings, read from <c>manifest.ini</c> before any script runs
/// (two-pass load: metadata first, code second, so dependency order is known up front).
/// </summary>
public sealed class PluginManifest
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; private set; } = string.Empty;
    public string Version { get; private set; } = "0.0.0";
    public string ApiVersion { get; private set; } = "1";
    public string Entry { get; private set; } = "plugin.js";
    public bool Enabled { get; private set; } = true;

    /// <summary>Dependency name → version requirement (<c>*</c>, <c>1.2.3</c>, <c>&gt;=1.2.3</c>, <c>^1.2.3</c>).</summary>
    public IReadOnlyDictionary<string, string> Depends => _depends;

    private readonly Dictionary<string, string> _depends = new(StringComparer.OrdinalIgnoreCase);

    public static PluginManifest Load(string directory)
    {
        var manifest = new PluginManifest
        {
            Name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        };

        var file = Path.Combine(directory, "manifest.ini");
        if (!File.Exists(file))
        {
            return manifest;
        }

        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line.Substring(0, separator).Trim();
            var value = line.Substring(separator + 1).Trim();
            manifest._values[key] = value;
            switch (key.ToLowerInvariant())
            {
                case "name":
                    manifest.Name = value;
                    break;
                case "version":
                    manifest.Version = value;
                    break;
                case "apiversion":
                    manifest.ApiVersion = value;
                    break;
                case "entry":
                    manifest.Entry = value;
                    break;
                case "enabled":
                    manifest.Enabled = bool.TryParse(value, out var enabled) && enabled;
                    break;
                case "depends":
                    // depends=core@^1.0.0, utils@*
                    foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = item.Split('@', 2);
                        var dependencyName = parts[0].Trim();
                        if (dependencyName.Length == 0)
                        {
                            continue;
                        }
                        manifest._depends[dependencyName] = parts.Length > 1 ? parts[1].Trim() : "*";
                    }
                    break;
            }
        }

        return manifest;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    /// <summary>Returns a copy with a different entry path (used for side-specific bundles).</summary>
    public PluginManifest WithEntry(string entry)
    {
        var clone = (PluginManifest)MemberwiseClone();
        clone.Entry = entry;
        return clone;
    }

    /// <summary>Minimal semver check: <c>*</c>, exact, <c>&gt;=</c>, <c>^</c> (same major).</summary>
    public static bool Satisfies(string version, string requirement)
    {
        if (requirement is "*" or "" or null)
        {
            return true;
        }

        var required = Parse(requirement.TrimStart('^', '>', '=', '~'));
        var actual = Parse(version);
        if (required == null || actual == null)
        {
            return true;
        }

        if (requirement.StartsWith("^", StringComparison.Ordinal))
        {
            return actual.Major == required.Major && actual.CompareTo(required) >= 0;
        }

        if (requirement.StartsWith(">=", StringComparison.Ordinal))
        {
            return actual.CompareTo(required) >= 0;
        }

        return actual.CompareTo(required) == 0;
    }

    private static System.Version? Parse(string text)
    {
        var digits = new System.Text.StringBuilder();
        foreach (var character in text)
        {
            if (char.IsDigit(character) || character == '.')
            {
                digits.Append(character);
            }
            else
            {
                break;
            }
        }

        return System.Version.TryParse(digits.ToString().Trim('.'), out var version) ? version : null;
    }
}
