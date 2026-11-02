using System;
using HarmonyLib;
using UnityEngine;

namespace ValheimJS.Core;

/// <summary>
/// Client-side executor for player mutations the server cannot apply (a dedicated server has no
/// <see cref="Player"/> component for remote players). Installed only on non-dedicated processes.
/// </summary>
internal static class PlayerCommandAgent
{
    private static bool _installed;

    internal static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        Networking.RpcChannel.Received += Handle;
        Networking.RpcChannel.Listen(PlayerAccess.CommandChannel);
        Plugin.Logger.LogInfo($"[player] command agent listening on '{PlayerAccess.CommandChannel}'");
    }

    private static float _nextPublish;

    /// <summary>Cheap periodic republish so the server mirror converges after external changes.</summary>
    internal static void Tick()
    {
        var local = Player.m_localPlayer;
        if (local == null)
        {
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (now < _nextPublish)
        {
            return;
        }

        _nextPublish = now + 5f;
        PublishInventory(local);
    }

    private static void PublishInventory(Player local)
    {
        try
        {
            var uid = local.GetPlayerID();
            if (uid == 0L)
            {
                return;
            }

            // Publishing marks this peer ready on the server, so it must happen only after our
            // channels are registered — otherwise join-time messages are sent into a deaf client.
            if (Networking.RpcChannel.HasPending())
            {
                return;
            }

            Networking.RpcChannel.Send(InventorySnapshot.Channel, uid + "|" + InventorySnapshot.Build(local));
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("[PlayerCommandAgent] publish failed: " + exception.Message);
        }
    }

    private static void Handle(string channel, long sender, string payload)
    {
        if (channel != PlayerAccess.CommandChannel)
        {
            return;
        }

        try
        {
            var separator = payload.IndexOf('|');
            var action = separator < 0 ? payload : payload.Substring(0, separator);
            var argument = separator < 0 ? string.Empty : payload.Substring(separator + 1);

            var local = Player.m_localPlayer;
            if (local == null)
            {
                return;
            }

            PlayerAccess.Apply(local, action, argument);
            Plugin.Logger.LogInfo($"[PlayerCommandAgent] applied '{action}' ({argument})");
            PublishInventory(local);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("[PlayerCommandAgent] " + exception.Message);
        }
    }
}
