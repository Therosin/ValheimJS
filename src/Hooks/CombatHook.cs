using System;
using HarmonyLib;
using ValheimJS.Core;

namespace ValheimJS.Hooks;

/// <summary>
/// Damage capture point. <c>Character.Damage</c> is the server-side funnel for every hit that is
/// actually applied, so this is where gameplay rules can observe combat.
/// Read-only: the hit has already been applied when the postfix runs.
/// </summary>
[HarmonyPatch(typeof(Character), nameof(Character.Damage))]
internal static class CombatHook
{
    [HarmonyPostfix]
    private static void Postfix(Character __instance, HitData hit)
    {
        try
        {
            if (__instance == null || hit == null)
            {
                return;
            }

            var attacker = hit.GetAttacker();
            ValheimBridge.RaiseDamage(
                Name(__instance),
                __instance.IsPlayer(),
                Name(attacker),
                attacker != null && attacker.IsPlayer(),
                hit.GetTotalDamage(),
                __instance.GetHealth(),
                __instance.GetMaxHealth(),
                hit.m_hitType.ToString());
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("[CombatHook] " + exception.Message);
        }
    }

    private static string Name(Character? character)
    {
        if (character == null)
        {
            return string.Empty;
        }

        return character is Player player ? player.GetPlayerName() : character.m_name ?? string.Empty;
    }
}
