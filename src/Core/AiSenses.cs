using System;
using System.Collections.Generic;

namespace ValheimJS.Core;

/// <summary>
/// Plugin-scoped perception views published by the NPC pump and consumed by AI instances. The
/// binding never reads game state directly; this keeps the AI engine and the NPC simulation
/// decoupled while still sharing one perception store per entity.
/// </summary>
internal static class AiSenses
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, Dictionary<string, object?>>> Plugins =
        new(StringComparer.Ordinal);

    internal static void Publish(string plugin, string key, Dictionary<string, object?> view)
    {
        lock (Gate)
        {
            if (!Plugins.TryGetValue(plugin, out var entries))
            {
                entries = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
                Plugins[plugin] = entries;
            }

            entries[key] = view;
        }
    }

    internal static Dictionary<string, object?>? Get(string plugin, string key)
    {
        lock (Gate)
        {
            return Plugins.TryGetValue(plugin, out var entries) &&
                   entries.TryGetValue(key, out var view)
                ? view
                : null;
        }
    }

    internal static void Clear(string plugin, string key)
    {
        lock (Gate)
        {
            if (Plugins.TryGetValue(plugin, out var entries))
            {
                entries.Remove(key);
            }
        }
    }

    internal static void Clear(string plugin)
    {
        lock (Gate)
        {
            Plugins.Remove(plugin);
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Plugins.Clear();
        }
    }
}
