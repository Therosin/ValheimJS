using System;
using System.Collections.Generic;
using System.Threading;
using ValheimJS.AI;

namespace ValheimJS.World;

/// <summary>
/// Process-wide per-NPC boards built on the reactive <see cref="Blackboard"/>.
/// One channel per NPC handle; changes are deduplicated by the board and delivered once.
/// </summary>
internal static class NpcBoards
{
    private sealed class Subscription
    {
        internal string Plugin = "";
        internal string Handle = "";
        internal Action Release = null!;
    }

    private static Blackboard _root = new();
    private static readonly Dictionary<long, Subscription> Subscriptions = new();
    private static long _next;

    internal static BlackboardScope For(string handle) => _root.Channels.Get(handle);

    internal static object? Get(string handle, string key) =>
        For(handle).TryGet<object?>(key, out var value) ? value : null;

    internal static IReadOnlyDictionary<string, object?> Snapshot(string handle) => For(handle).Snapshot();

    /// <summary>Subscribes to every change of one NPC, optionally filtered by key.</summary>
    internal static long Subscribe(string plugin, string handle, string? key, Action<BlackboardChange> onChange)
    {
        var channel = For(handle);
        Action<BlackboardChange> handler = change =>
        {
            if (key != null && !string.Equals(change.Key, key, StringComparison.Ordinal))
            {
                return;
            }

            onChange(change);
        };

        channel.Changed += handler;
        var id = Interlocked.Increment(ref _next);
        Subscriptions[id] = new Subscription
        {
            Plugin = plugin,
            Handle = handle,
            Release = () => channel.Changed -= handler
        };
        return id;
    }

    internal static bool Unsubscribe(string plugin, long id)
    {
        if (!Subscriptions.TryGetValue(id, out var subscription) ||
            !string.Equals(subscription.Plugin, plugin, StringComparison.Ordinal))
        {
            return false;
        }

        Subscriptions.Remove(id);
        subscription.Release();
        return true;
    }

    internal static void Release(string plugin)
    {
        List<long>? remove = null;
        foreach (var pair in Subscriptions)
        {
            if (string.Equals(pair.Value.Plugin, plugin, StringComparison.Ordinal))
            {
                (remove ??= new()).Add(pair.Key);
            }
        }

        if (remove == null)
        {
            return;
        }

        foreach (var id in remove)
        {
            Unsubscribe(plugin, id);
        }
    }

    internal static void Remove(string handle)
    {
        if (Subscriptions.Count > 0)
        {
            List<long>? remove = null;
            foreach (var pair in Subscriptions)
            {
                if (string.Equals(pair.Value.Handle, handle, StringComparison.Ordinal))
                {
                    (remove ??= new()).Add(pair.Key);
                }
            }

            if (remove != null)
            {
                foreach (var id in remove)
                {
                    if (!Subscriptions.Remove(id, out var subscription))
                    {
                        continue;
                    }

                    subscription.Release();
                }
            }
        }

        _root.Channels.Remove(handle);
    }

    /// <summary>Drops every board and subscription; used when the world is torn down.</summary>
    internal static void Clear()
    {
        foreach (var subscription in Subscriptions.Values)
        {
            subscription.Release();
        }

        Subscriptions.Clear();
        _root = new Blackboard();
    }
}
