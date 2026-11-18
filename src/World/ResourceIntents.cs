using System;
using System.Collections.Generic;

namespace ValheimJS.World;

/// <summary>
/// Process-wide resource intentions. This is not ownership of the native item: it only keeps
/// competing scripted actors from selecting the same resource during a short execution window.
/// </summary>
internal static class ResourceIntents
{
    private readonly struct Entry
    {
        internal Entry(string owner, long expiresAt)
        {
            Owner = owner;
            ExpiresAt = expiresAt;
        }

        internal string Owner { get; }
        internal long ExpiresAt { get; }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    internal static bool TryReserve(string handle, string owner, int ttlMs)
    {
        if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }

        var now = DateTime.UtcNow.Ticks;
        lock (Gate)
        {
            if (Entries.TryGetValue(handle, out var existing) &&
                existing.ExpiresAt > now &&
                !string.Equals(existing.Owner, owner, StringComparison.Ordinal))
            {
                return false;
            }

            Entries[handle] = new Entry(owner, now + Math.Max(100, ttlMs) * TimeSpan.TicksPerMillisecond);
            return true;
        }
    }

    internal static void Release(string handle, string owner)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return;
        }

        lock (Gate)
        {
            if (Entries.TryGetValue(handle, out var existing) &&
                string.Equals(existing.Owner, owner, StringComparison.Ordinal))
            {
                Entries.Remove(handle);
            }
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }
    }
}
