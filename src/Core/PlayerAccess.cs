using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ValheimJS.Core;

/// <summary>
/// Server-side view of a player.
/// <para>
/// A dedicated server has no <see cref="Player"/> component for remote players — only their ZDO
/// (health/stamina/eitr/position live in <c>ZDOVars</c> fields). Reads therefore come from the ZDO;
/// writes cannot be applied locally and are sent to the owning client, which runs
/// <see cref="PlayerCommandAgent"/>.
/// </para>
/// </summary>
public static class PlayerAccess
{
    /// <summary>Channel the server uses to ask a client to act on its own player.</summary>
    public const string CommandChannel = "valheimjs:player";

    private static readonly Dictionary<long, List<(string Channel, string Payload)>> Queued = new();
    private static readonly HashSet<long> Ready = new();

    /// <summary>A client told us it is listening; flush anything queued for it.</summary>
    internal static void MarkReady(long uid)
    {
        if (!Ready.Add(uid))
        {
            return;
        }

        if (!Queued.TryGetValue(uid, out var commands))
        {
            return;
        }

        Queued.Remove(uid);
        foreach (var message in commands)
        {
            Networking.RpcChannel.SendTo(uid, message.Channel, message.Payload);
            Plugin.Logger.LogInfo($"[player] flushed '{message.Channel}' to {uid}");
        }
    }

    internal static bool IsReady(long uid) => Ready.Contains(uid);

    /// <summary>
    /// Sends now when the peer has proven it is listening, otherwise queues — a client's
    /// channels register a moment after it connects, so join-time messages would be lost.
    /// </summary>
    internal static void QueueOrSend(long uid, string channel, string payload)
    {
        if (uid == 0L)
        {
            return;
        }

        if (Ready.Contains(uid))
        {
            Networking.RpcChannel.SendTo(uid, channel, payload);
            return;
        }

        if (!Queued.TryGetValue(uid, out var list))
        {
            list = new List<(string Channel, string Payload)>();
            Queued[uid] = list;
        }

        if (list.Count < 32)
        {
            list.Add((channel, payload));
        }
    }

    internal static void Forget(long uid)
    {
        Ready.Remove(uid);
        Queued.Remove(uid);
    }

    public static bool TryGetZdo(long uid, out ZDO? zdo)
    {
        zdo = null;
        if (uid == 0L)
        {
            return false;
        }

        var peer = ZNet.instance?.GetPeers()?.FirstOrDefault(candidate => candidate != null && candidate.m_uid == uid);
        if (peer == null || peer.m_characterID.IsNone())
        {
            return false;
        }

        zdo = ZDOMan.instance?.GetZDO(peer.m_characterID);
        return zdo != null;
    }

    public static string? ReadStats(long uid)
    {
        var player = PlayerLookup.ByUid(uid);
        if (player != null)
        {
            return StatsJson(player.GetPlayerName(), player.GetHealth(), player.GetMaxHealth(),
                player.GetStamina(), player.GetMaxStamina(), player.GetEitr(), player.GetMaxEitr(),
                player.transform.position);
        }

        if (!TryGetZdo(uid, out var zdo) || zdo == null)
        {
            return null;
        }

        var name = ZNet.instance?.GetPeers()?.FirstOrDefault(p => p != null && p.m_uid == uid)?.m_playerName ?? string.Empty;
        return StatsJson(
            name,
            zdo.GetFloat(ZDOVars.s_health),
            zdo.GetFloat(ZDOVars.s_maxHealth),
            zdo.GetFloat(ZDOVars.s_stamina),
            0f,
            zdo.GetFloat(ZDOVars.s_eitr),
            0f,
            zdo.GetPosition());
    }

    public static bool Exists(long uid) => PlayerLookup.ByUid(uid) != null || TryGetZdo(uid, out _);

    /// <summary>Applies a mutation locally when possible, otherwise commands the owning client.</summary>
    public static void Mutate(long uid, string action, string argument)
    {
        var player = PlayerLookup.ByUid(uid);
        if (player != null)
        {
            Apply(player, action, argument);
            return;
        }

        if (!TryGetZdo(uid, out _))
        {
            return;
        }

        QueueOrSend(uid, CommandChannel, action + "|" + argument);
    }

    /// <summary>Runs on whichever process owns the player (see <see cref="PlayerCommandAgent"/>).</summary>
    internal static void Apply(Player player, string action, string argument)
    {
        switch (action)
        {
            case "setHealth":
                player.SetHealth(Mathf.Clamp(ParseFloat(argument, player.GetHealth()), 0f, player.GetMaxHealth()));
                break;
            case "setGodMode":
                player.SetGodMode(argument == "1");
                break;
            case "teleport":
                var parts = argument.Split(',');
                if (parts.Length == 3)
                {
                    player.TeleportTo(
                        new Vector3(ParseFloat(parts[0], 0f), ParseFloat(parts[1], 0f), ParseFloat(parts[2], 0f)),
                        Quaternion.identity,
                        distantTeleport: true);
                }
                break;
            case "raiseSkill":
                var skillParts = argument.Split(',');
                if (skillParts.Length == 2 &&
                    Enum.TryParse<Skills.SkillType>(skillParts[0], ignoreCase: true, out var skill))
                {
                    player.GetSkills()?.RaiseSkill(skill, ParseFloat(skillParts[1], 1f));
                }
                break;
        }
    }

    private static float ParseFloat(string text, float fallback) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static string StatsJson(
        string name, float health, float maxHealth, float stamina, float maxStamina,
        float eitr, float maxEitr, Vector3 position) =>
        "{" +
        $"\"name\":{Utils.Json.Quote(name)}," +
        $"\"health\":{F(health)},\"maxHealth\":{F(maxHealth)}," +
        $"\"stamina\":{F(stamina)},\"maxStamina\":{F(maxStamina)}," +
        $"\"eitr\":{F(eitr)},\"maxEitr\":{F(maxEitr)}," +
        $"\"position\":{{\"x\":{F(position.x)},\"y\":{F(position.y)},\"z\":{F(position.z)}}}" +
        "}";

    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
