using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ValheimJS.Core;

namespace ValheimJS.Hooks;

/// <summary>
/// Join/leave detection.
/// <para>
/// Peer fields are not populated when the player list is first rewritten: the server's own entry
/// shows up as uid 0 with an empty name, and a connecting peer only gets its name after the
/// handshake finishes. So entries are filtered to "fully known peers" and the comparison is run
/// both on list rewrites and on the ~1 Hz heartbeat — a peer joins when it becomes known, not
/// when the socket opens.
/// </para>
/// </summary>
internal static class PlayerWatcher
{
    private static readonly Dictionary<long, string> Known = new();
    private static readonly Dictionary<long, string> Spawned = new();

    internal static void Poll()
    {
        var peers = ZNet.instance?.GetPeers();
        if (peers == null)
        {
            return;
        }

        var current = new Dictionary<long, string>();
        foreach (var peer in peers)
        {
            if (peer == null || peer.m_server || peer.m_uid == 0L || string.IsNullOrEmpty(peer.m_playerName))
            {
                continue;
            }

            current[peer.m_uid] = peer.m_playerName;
        }

        foreach (var (uid, name) in current)
        {
            if (!Known.ContainsKey(uid))
            {
                ValheimBridge.RaisePlayerJoin(uid, name);
            }
        }

        foreach (var (uid, name) in Known)
        {
            if (!current.ContainsKey(uid))
            {
                ValheimBridge.RaisePlayerLeave(uid, name);
            }
        }

        Known.Clear();
        foreach (var (uid, name) in current)
        {
            Known[uid] = name;
        }
    }

    /// <summary>
    /// Character phase: a peer connects seconds before its character exists, and `player.*` only
    /// works once it does. Polled from the heartbeat so plugins get a reliable "can I touch it" event.
    /// </summary>
    internal static void PollSpawned()
    {
        var current = new Dictionary<long, string>();
        foreach (var (uid, name, _) in PlayerLookup.Spawned())
        {
            current[uid] = name;
        }

        foreach (var (uid, name) in current)
        {
            if (!Spawned.ContainsKey(uid))
            {
                ValheimBridge.RaisePlayerSpawned(uid, name);
            }
        }

        foreach (var (uid, name) in Spawned)
        {
            if (!current.ContainsKey(uid))
            {
                ValheimBridge.RaisePlayerDespawned(uid, name);
            }
        }

        Spawned.Clear();
        foreach (var (uid, name) in current)
        {
            Spawned[uid] = name;
        }
    }

    internal static void Reset()
    {
        Known.Clear();
        Spawned.Clear();
    }

    internal static IReadOnlyList<(long Uid, string Name)> Snapshot() =>
        Known.Select(entry => (entry.Key, entry.Value)).ToList();
}

/// <summary>Prompt poll whenever the game republishes the player list.</summary>
[HarmonyPatch(typeof(ZNet), "SendPlayerList")]
internal static class PlayerListHook
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        try
        {
            PlayerWatcher.Poll();
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("[PlayerHook] " + exception.Message);
        }
    }
}
