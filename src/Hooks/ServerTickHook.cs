using System;
using HarmonyLib;
using UnityEngine;
using ValheimJS.Core;

namespace ValheimJS.Hooks;

/// <summary>
/// Coarse server heartbeat. Emitted at ~1 Hz so plugins have a scheduling point without
/// paying per-frame cost; the tick interval is owned by the mod, not by plugin code.
/// </summary>
[ValheimJS.Scripting.Shared]
[HarmonyPatch(typeof(ZNet), "Update")]
internal static class ServerTickHook
{
    private const float IntervalSeconds = 1f;
    private static float _next;

    [HarmonyPostfix]
    private static void Postfix()
    {
        try
        {
            var now = Time.realtimeSinceStartup;
            if (now < _next)
            {
                return;
            }

            _next = now + IntervalSeconds;

            PlayerWatcher.Poll();
            PlayerWatcher.PollSpawned();
            ValheimBridge.RaiseServerTick();
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("[ServerTickHook] " + exception.Message);
        }
    }
}
