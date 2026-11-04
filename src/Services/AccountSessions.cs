using System;
using System.Collections.Generic;
using System.Globalization;
using ValheimJS.Core;

namespace ValheimJS.Services;

/// <summary>Adapts verified game connections to the engine-independent account service.</summary>
internal sealed class AccountSessions
{
    private readonly AccountService _accounts;
    internal AccountSessions(AccountService accounts) => _accounts = accounts;
    internal void Refresh()
    {
        var active = new List<string>();
        if (_accounts.Available && ZNet.instance != null)
        {
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null || peer.m_server || peer.m_uid == 0 || string.IsNullOrEmpty(peer.m_playerName)) continue;
                Observe(peer.m_uid, peer.m_playerName, active);
            }
            if (!ZNet.instance.IsDedicated() && Player.m_localPlayer != null)
                Observe(ZNet.GetUID(), Player.m_localPlayer.GetPlayerName(), active);
        }
        _accounts.RetainSessions(active);
    }
    private void Observe(long peer, string name, List<string> active)
    {
        var platform = PlayerIdentity.PlatformId(peer);
        if (platform == null) return;
        var id = peer.ToString(CultureInfo.InvariantCulture);
        try { _accounts.Observe(id, platform, name); active.Add(id); }
        catch (Exception error) { Plugin.Logger.LogWarning("[accounts] could not resolve peer " + id + ": " + error.Message); }
    }
}
