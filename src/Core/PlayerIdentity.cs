using System;
using System.Linq;
using Splatform;

namespace ValheimJS.Core;

/// <summary>
/// Stable identity for a connected peer.
/// <para>
/// The routed uid is session-only, so anything that must survive a restart keys off the platform
/// identity instead. Steam reports <c>Steam_&lt;SteamID64&gt;</c> as the socket host name; the
/// digits are the stable id.
/// </para>
/// </summary>
public static class PlayerIdentity
{
    /// <summary>Display name for one connected peer, or null.</summary>
    public static string? Name(long uid) =>
        ZNet.instance?.GetPeers()?.FirstOrDefault(peer => peer != null && peer.m_uid == uid)?.m_playerName;

    /// <summary>Raw platform id from the peer socket ("Steam_7656…"), or the local user's.</summary>
    public static string? PlatformId(long uid)
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

    /// <summary>SteamID64 digits, stable across sessions; null when the peer is not on Steam.</summary>
    public static string? SteamId(long uid) => ToSteamId(PlatformId(uid));

    /// <summary>Normalises a platform id to SteamID64 digits, or null for other platforms.</summary>
    public static string? ToSteamId(string? platformId)
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
}
