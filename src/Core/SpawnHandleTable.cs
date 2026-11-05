using System;
using System.Collections.Generic;

namespace ValheimJS.Core;

/// <summary>Outcome of looking a session spawn handle up for one plugin.</summary>
public enum HandleLookup
{
    /// <summary>The handle exists and belongs to the asking plugin.</summary>
    Ok,

    /// <summary>The handle was never issued or was already released.</summary>
    Unknown,

    /// <summary>The handle exists but belongs to another plugin.</summary>
    Foreign
}

/// <summary>
/// Session spawn handles, owned by the plugin that created them. Handles are opaque, never
/// reused, and refuse cross-plugin access so one plugin cannot read, overwrite or destroy
/// another's spawned objects. Unity-free so the headless harness can verify the ownership rules.
/// </summary>
public sealed class SpawnHandleTable<TId> where TId : notnull
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _next;

    public int Count => _entries.Count;

    /// <summary>Records a spawned object and returns the handle this plugin presents.</summary>
    public string Add(string owner, TId id)
    {
        var handle = "spawn-" + (++_next);
        _entries.Add(handle, new Entry(owner, id));
        return handle;
    }

    /// <summary>Looks a handle up without removing it; foreign handles are reported, not resolved.</summary>
    public HandleLookup Resolve(string owner, string handle, out TId id)
    {
        if (!_entries.TryGetValue(handle, out var entry))
        {
            id = default!;
            return HandleLookup.Unknown;
        }

        if (entry.Owner != owner)
        {
            id = default!;
            return HandleLookup.Foreign;
        }

        id = entry.Id;
        return HandleLookup.Ok;
    }

    /// <summary>Resolves and forgets a handle; nothing is removed for an unknown or foreign caller.</summary>
    public HandleLookup Take(string owner, string handle, out TId id)
    {
        var result = Resolve(owner, handle, out id);
        if (result == HandleLookup.Ok)
        {
            _entries.Remove(handle);
        }

        return result;
    }

    /// <summary>Forgets every handle owned by a plugin; returns how many were dropped.</summary>
    public int Release(string owner)
    {
        var owned = new List<string>();
        foreach (var pair in _entries)
        {
            if (pair.Value.Owner == owner)
            {
                owned.Add(pair.Key);
            }
        }

        foreach (var handle in owned)
        {
            _entries.Remove(handle);
        }

        return owned.Count;
    }

    private readonly struct Entry
    {
        internal Entry(string owner, TId id)
        {
            Owner = owner;
            Id = id;
        }

        internal string Owner { get; }

        internal TId Id { get; }
    }
}
