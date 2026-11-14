using System;
using System.Collections.Generic;
using Jint.Native;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.World;

/// <summary>
/// Core-owned per-NPC blackboard. Unlike the per-tick <c>ctx.state.senses</c> snapshot, this
/// emits change subscriptions with explicit lifecycle. Values are published only when they change;
/// plugin-local boards remain separate.
/// </summary>
internal static class NpcBoard
{
    private sealed class Entry
    {
        internal readonly Dictionary<string, object?> Values = new(StringComparer.Ordinal);
        internal readonly List<Subscription> Subscriptions = new();
    }

    private sealed class Subscription
    {
        internal string Plugin = "";
        internal string Handle = "";
        internal string? Key;
        internal JsCallback Callback = null!;
        internal JsValue Target = JsValue.Undefined;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Boards = new(StringComparer.Ordinal);
    private static readonly Dictionary<long, Subscription> Subscriptions = new();
    private static long _next = 1;

    internal static void Publish(string handle, string key, object? value)
    {
        List<(Subscription Subscription, object? Value)>? notify = null;
        lock (Gate)
        {
            if (!Boards.TryGetValue(handle, out var entry))
            {
                entry = new Entry();
                Boards[handle] = entry;
            }

            if (entry.Values.TryGetValue(key, out var previous) && Equals(previous, value))
            {
                return;
            }

            entry.Values[key] = value;
            foreach (var subscription in entry.Subscriptions)
            {
                if (subscription.Key == null || string.Equals(subscription.Key, key, StringComparison.Ordinal))
                {
                    (notify ??= new()).Add((subscription, value));
                }
            }
        }

        if (notify == null) return;
        foreach (var (subscription, current) in notify)
        {
            try
            {
                subscription.Callback.InvokeOn(subscription.Target, key,
                    JsPayload.FromPlain(subscription.Callback.Engine, current));
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"npc board '{handle}' listener failed: {exception.Message}");
            }
        }
    }

    internal static object? Get(string handle, string key)
    {
        lock (Gate)
        {
            return Boards.TryGetValue(handle, out var entry) && entry.Values.TryGetValue(key, out var value)
                ? value
                : null;
        }
    }

    internal static Dictionary<string, object?> Snapshot(string handle)
    {
        lock (Gate)
        {
            if (!Boards.TryGetValue(handle, out var entry))
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            return new Dictionary<string, object?>(entry.Values, StringComparer.Ordinal);
        }
    }

    internal static long Subscribe(string plugin, string handle, string? key, JsCallback callback, JsValue target)
    {
        var id = System.Threading.Interlocked.Increment(ref _next);
        lock (Gate)
        {
            if (!Boards.TryGetValue(handle, out var entry))
            {
                entry = new Entry();
                Boards[handle] = entry;
            }

            entry.Subscriptions.Add(new Subscription
            {
                Plugin = plugin,
                Handle = handle,
                Key = string.IsNullOrWhiteSpace(key) ? null : key,
                Callback = callback,
                Target = target
            });
            Subscriptions[id] = entry.Subscriptions[^1];
        }

        return id;
    }

    internal static bool Unsubscribe(string plugin, long id)
    {
        lock (Gate)
        {
            if (!Subscriptions.TryGetValue(id, out var subscription) ||
                !string.Equals(subscription.Plugin, plugin, StringComparison.Ordinal))
            {
                return false;
            }

            Subscriptions.Remove(id);
            if (Boards.TryGetValue(subscription.Handle, out var entry))
            {
                entry.Subscriptions.RemoveAll(candidate => ReferenceEquals(candidate, subscription));
            }

            return true;
        }
    }

    internal static void RemoveHandle(string handle)
    {
        lock (Gate)
        {
            if (Boards.TryGetValue(handle, out var entry))
            {
                foreach (var subscription in entry.Subscriptions)
                {
                    foreach (var pair in Subscriptions)
                    {
                        if (ReferenceEquals(pair.Value, subscription))
                        {
                            Subscriptions.Remove(pair.Key);
                            break;
                        }
                    }
                }
            }

            Boards.Remove(handle);
        }
    }

    internal static void Release(string plugin)
    {
        lock (Gate)
        {
            var remove = new List<long>();
            foreach (var pair in Subscriptions)
            {
                if (string.Equals(pair.Value.Plugin, plugin, StringComparison.Ordinal))
                {
                    remove.Add(pair.Key);
                }
            }

            foreach (var id in remove)
            {
                var subscription = Subscriptions[id];
                Subscriptions.Remove(id);
                if (Boards.TryGetValue(subscription.Handle, out var entry))
                {
                    entry.Subscriptions.RemoveAll(candidate => ReferenceEquals(candidate, subscription));
                }
            }
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Boards.Clear();
            Subscriptions.Clear();
        }
    }
}
