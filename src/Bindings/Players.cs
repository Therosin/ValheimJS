using System;
using Splatform;
using ValheimJS.Scripting.Binding;
using System.Collections.Generic;
using System.Linq;

namespace ValheimJS.Bindings;

/// <summary>
/// Connected peers as the server sees them. Scripts see <c>host.api.players.*</c>.
/// Peer identity is stable for the session; the character ZDO is only valid once the
/// player has spawned, so treat <c>characterId</c> as optional.
/// </summary>
[JsBinding("Players", JsBindPhase.Gameplay, order: 110)]
internal static class PlayersBindings
{
    public static void Bind(JsBindingContext context)
    {
        context.Add("players.list", new Func<string>(List), JsReturnKind.Json);
        context.Add("players.count", new Func<int>(() => ZNet.instance?.GetPeers()?.Count ?? 0));
        context.Add("players.names", new Func<string[]>(() =>
            ZNet.instance?.GetPeers()?.Select(peer => peer.m_playerName).ToArray() ?? Array.Empty<string>()));
        context.Add("players.hostname", new Func<long, string?>(PeerName));
        context.Add("players.isAdmin", new Func<long, bool>(IsAdmin));
        // Platform identity, as the Discord bridge does it: socket host name -> SteamID64 digits.
        context.Add("players.steamId", new Func<long, string?>(uid => SteamIdOf(PlatformOf(uid))));
        context.Add("players.platformId", new Func<long, string?>(PlatformOf));
    }

    /// <summary>Raw platform id from the peer socket ("Steam_7656…"), or the local user's.</summary>
    private static string? PlatformOf(long uid)
    {
        var peer = ZNet.instance?.GetPeers()?.FirstOrDefault(candidate => candidate != null && candidate.m_uid == uid);
        if (peer?.m_socket != null)
        {
            try
            {
                return peer.m_socket.GetHostName();
            }
            catch
            {
                return null;
            }
        }

        if (ZNet.instance != null && !ZNet.instance.IsDedicated() && uid == ZNet.GetUID())
        {
            try
            {
                return PlatformManager.DistributionPlatform?.LocalUser?.PlatformUserID.ToString();
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>"Steam_7656…" → "7656…"; null when it is not a Steam id.</summary>
    private static string? SteamIdOf(string? platformId)
    {
        if (string.IsNullOrEmpty(platformId))
        {
            return null;
        }

        var digits = platformId.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase)
            ? platformId.Substring(6)
            : platformId;

        return digits.Length > 0 && digits.All(char.IsDigit) ? digits : null;
    }

    private static bool IsAdmin(long peerUid)
    {
        // Admin membership is server-side knowledge; asking on a client throws inside the game.
        if (!EnvBindings.IsServer())
        {
            return false;
        }

        try
        {
            var peer = ZNet.instance?.GetPeers()?.FirstOrDefault(candidate => candidate.m_uid == peerUid);
            if (peer?.m_socket == null)
            {
                return false;
            }

            return ZNet.instance!.IsAdmin(peer.m_socket.GetHostName());
        }
        catch
        {
            return false;
        }
    }

    private static string? PeerName(long peerUid)
    {
        var peer = ZNet.instance?.GetPeers()?.FirstOrDefault(candidate => candidate.m_uid == peerUid);
        return peer?.m_playerName;
    }

    private static string List()
    {
        var peers = ZNet.instance?.GetPeers();
        if (peers == null || peers.Count == 0)
        {
            return "[]";
        }

        var entries = new List<string>(peers.Count);
        foreach (var peer in peers)
        {
            if (peer == null)
            {
                continue;
            }

            try
            {
                entries.Add(Describe(peer));
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning("[Players] skipped a peer: " + exception.Message);
            }
        }

        return "[" + string.Join(",", entries) + "]";
    }

    private static string Describe(ZNetPeer peer)
    {
        return "{" +
                        $"\"uid\":{peer.m_uid}," +
                        $"\"name\":{Utils.Json.Quote(peer.m_playerName)}," +
                        $"\"server\":{(peer.m_server ? "true" : "false")}," +
                        $"\"admin\":{(IsAdmin(peer.m_uid) ? "true" : "false")}," +
                        $"\"steamId\":{Core.ValheimBridge.Quote(SteamIdOf(PlatformOf(peer.m_uid))) }," +
                        $"\"platformId\":{Core.ValheimBridge.Quote(PlatformOf(peer.m_uid))}," +
                        $"\"characterId\":{Utils.Json.Quote(peer.m_characterID.ToString())}," +
                 $"\"position\":{{\"x\":{F(peer.m_refPos.x)},\"y\":{F(peer.m_refPos.y)},\"z\":{F(peer.m_refPos.z)}}}" +
                 "}";
    }

    private static string F(float value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
