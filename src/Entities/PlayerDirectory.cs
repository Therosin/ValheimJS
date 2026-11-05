using System.Collections.Generic;
using System.Globalization;
using ValheimJS.Core;

namespace ValheimJS.Entities;

/// <summary>
/// Process-side player resolution. Native objects are keyed by routed uid so the same player
/// yields one <see cref="Player"/>; plugin-scoped JS instances are cached by the type registry on
/// top of these. ZDO ids and peer objects never leave this class.
/// </summary>
internal static class PlayerDirectory
{
    private static readonly Dictionary<long, Player> Cache = new();

    /// <summary>This process's local player, or null on a dedicated server.</summary>
    internal static Player? Local()
    {
        var local = global::Player.m_localPlayer;
        if (local == null)
        {
            return null;
        }

        var uid = ZNet.GetUID();
        var player = FindOrCreate(uid, local.GetPlayerName(), local.GetZDOID(), isLocal: true);
        return player;
    }

    /// <summary>One connected player by routed uid or platform id; null when unknown/offline.</summary>
    internal static Player? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var local = Local();
        if (local != null && Matches(local, id))
        {
            return local;
        }

        foreach (var peer in Peers())
        {
            if (peer == null || peer.m_server)
            {
                continue;
            }

            if (peer.m_uid.ToString(CultureInfo.InvariantCulture) == id ||
                PlayerIdentity.PlatformId(peer.m_uid) == id ||
                PlayerIdentity.SteamId(peer.m_uid) == id)
            {
                return FromPeer(peer);
            }
        }

        return null;
    }

    private static bool Matches(Player player, string id) =>
        player.Uid == id ||
        player.PlatformId == id ||
        (player.PlatformId != null && PlayerIdentity.ToSteamId(player.PlatformId) == id);

    /// <summary>Connected players with a spawned character; live authority only.</summary>
    internal static IReadOnlyList<Player> Online()
    {
        var result = new List<Player>();
        if (ZNet.instance == null)
        {
            return result;
        }

        var local = Local();
        if (local != null)
        {
            result.Add(local);
        }

        foreach (var peer in Peers())
        {
            if (peer.m_server || peer.m_characterID.IsNone() || !peer.IsReady())
            {
                continue;
            }

            if (local != null && peer.m_uid == local.UidValue)
            {
                continue;
            }

            result.Add(FromPeer(peer));
        }

        Prune(result);
        return result;
    }

    private static List<ZNetPeer> Peers() => ZNet.instance?.GetPeers() ?? new List<ZNetPeer>();

    private static Player FromPeer(ZNetPeer peer) =>
        FindOrCreate(peer.m_uid, peer.m_playerName, peer.m_characterID, isLocal: false);

    private static Player FindOrCreate(long uid, string name, ZDOID characterId, bool isLocal)
    {
        if (Cache.TryGetValue(uid, out var player))
        {
            player.Refresh(name, characterId, isLocal);
            return player;
        }

        player = new Player(uid, name, characterId, isLocal);
        Cache[uid] = player;
        return player;
    }

    private static void Prune(IReadOnlyList<Player> current)
    {
        if (Cache.Count <= current.Count)
        {
            return;
        }

        var keep = new HashSet<long>();
        foreach (var player in current)
        {
            keep.Add(player.UidValue);
        }

        var stale = new List<long>();
        foreach (var uid in Cache.Keys)
        {
            if (!keep.Contains(uid))
            {
                stale.Add(uid);
            }
        }

        foreach (var uid in stale)
        {
            Cache.Remove(uid);
        }
    }
}
