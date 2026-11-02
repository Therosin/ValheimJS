using System;
using ValheimJS.Scripting.Binding;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ValheimJS.Bindings;

/// <summary>
/// Authoritative player state and mutation. Scripts see <c>host.api.player.*</c>.
/// Everything here is server-side: players are resolved from the live peer list, and calls fail
/// softly (no-op) when the player is not spawned — which is the normal state for a moment after
/// they connect.
/// </summary>
[JsBinding("Player", JsBindPhase.Gameplay, order: 150)]
internal static class PlayerBindings
{
    public static void Bind(JsBindingContext context)
    {
        context.Add("player.stats", new Func<long, string?>(Core.PlayerAccess.ReadStats), JsReturnKind.Json);
        context.Add("player.exists", new Func<long, bool>(Core.PlayerAccess.Exists));
        context.Add("player.setHealth", new Action<long, float>((uid, value) =>
            Core.PlayerAccess.Mutate(uid, "setHealth", value.ToString(CultureInfo.InvariantCulture))));
        context.Add("player.setGodMode", new Action<long, bool>((uid, enabled) =>
            Core.PlayerAccess.Mutate(uid, "setGodMode", enabled ? "1" : "0")));
        context.Add("player.teleport", new Action<long, float, float, float>((uid, x, y, z) =>
            Core.PlayerAccess.Mutate(uid, "teleport",
                string.Join(",", x.ToString(CultureInfo.InvariantCulture),
                    y.ToString(CultureInfo.InvariantCulture), z.ToString(CultureInfo.InvariantCulture)))));
        context.Add("player.raiseSkill", new Action<long, string, float>((uid, skill, factor) =>
            Core.PlayerAccess.Mutate(uid, "raiseSkill",
                skill + "," + factor.ToString(CultureInfo.InvariantCulture))));
    }

    internal static Player? Resolve(long uid) => Core.PlayerLookup.ByUid(uid);

    private static string? Stats(long uid)
    {
        var player = Resolve(uid);
        if (player == null)
        {
            return null;
        }

        var position = player.transform.position;
        return "{" +
               $"\"name\":{ValheimJS.Utils.Json.Quote(player.GetPlayerName())}," +
               $"\"health\":{F(player.GetHealth())}," +
               $"\"maxHealth\":{F(player.GetMaxHealth())}," +
               $"\"stamina\":{F(player.GetStamina())}," +
               $"\"maxStamina\":{F(player.GetMaxStamina())}," +
               $"\"eitr\":{F(player.GetEitr())}," +
               $"\"maxEitr\":{F(player.GetMaxEitr())}," +
               $"\"position\":{{\"x\":{F(position.x)},\"y\":{F(position.y)},\"z\":{F(position.z)}}}" +
               "}";
    }

    private static void SetHealth(long uid, float value)
    {
        var player = Resolve(uid);
        if (player == null)
        {
            return;
        }

        player.SetHealth(Mathf.Clamp(value, 0f, player.GetMaxHealth()));
    }

    private static void SetGodMode(long uid, bool enabled) => Resolve(uid)?.SetGodMode(enabled);

    private static void Teleport(long uid, float x, float y, float z)
    {
        var player = Resolve(uid);
        player?.TeleportTo(new Vector3(x, y, z), Quaternion.identity, distantTeleport: false);
    }

    private static void RaiseSkill(long uid, string skill, float factor)
    {
        var player = Resolve(uid);
        if (player == null || !Enum.TryParse<Skills.SkillType>(skill, ignoreCase: true, out var skillType))
        {
            return;
        }

        player.GetSkills()?.RaiseSkill(skillType, factor);
    }

    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
