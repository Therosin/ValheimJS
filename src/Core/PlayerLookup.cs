using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimJS.Core;

/// <summary>
/// Resolves a peer uid to its live <see cref="Player"/> component.
/// <para>
/// <c>Player.GetAllPlayers()</c> only lists players this process instantiated, which on a dedicated
/// server is empty — clients' characters live in the ZDO world instead. So the authoritative path
/// is peer → character ZDOID → <c>ZNetScene.FindInstance</c> → component. Both are tried, because a
/// hosting player (listen server) sees them the other way round.
/// </para>
/// </summary>
public static class PlayerLookup
{
    public static Player? ByUid(long uid)
    {
        if (uid == 0L)
        {
            return null;
        }

        var local = Player.GetAllPlayers()?.FirstOrDefault(player => player != null && player.GetPlayerID() == uid);
        if (local != null)
        {
            return local;
        }

        var peer = ZNet.instance?.GetPeers()?.FirstOrDefault(candidate => candidate != null && candidate.m_uid == uid);
        if (peer == null || peer.m_characterID.IsNone())
        {
            return null;
        }

        var go = ZNetScene.instance?.FindInstance(peer.m_characterID);
        return go != null ? go.GetComponent<Player>() : null;
    }

    /// <summary>
    /// Every player that currently has a character in the world, keyed by peer uid. The player
    /// component is null on a dedicated server (see <see cref="PlayerAccess"/>); the name is
    /// always filled.
    /// </summary>
    public static IEnumerable<(long Uid, string Name, Player? Player)> Spawned()
    {
        var seen = new HashSet<long>();

        foreach (var player in Player.GetAllPlayers() ?? new List<Player>())
        {
            if (player == null)
            {
                continue;
            }

            var uid = player.GetPlayerID();
            if (seen.Add(uid))
            {
                yield return (uid, player.GetPlayerName() ?? string.Empty, player);
            }
        }

        // A dedicated server never instantiates a Player component for remote clients; the
        // character ZDO is the authoritative "this player is in the world" signal there.
        foreach (var peer in ZNet.instance?.GetPeers() ?? new List<ZNetPeer>())
        {
            if (peer == null || peer.m_characterID.IsNone() || !seen.Add(peer.m_uid))
            {
                continue;
            }

            var go = ZNetScene.instance?.FindInstance(peer.m_characterID);
            var player = go != null ? go.GetComponent<Player>() : null;
            if (player != null)
            {
                yield return (peer.m_uid, player.GetPlayerName() ?? peer.m_playerName ?? string.Empty, player);
                continue;
            }

            if (ZDOMan.instance?.GetZDO(peer.m_characterID) != null)
            {
                yield return (peer.m_uid, peer.m_playerName ?? string.Empty, null);
            }
        }
    }
}
