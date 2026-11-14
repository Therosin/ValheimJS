using System;
using HarmonyLib;
using UnityEngine;
using ValheimJS.Core;
using ValheimJS.World;

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

            // A reachable ZNet is not a playable world. Dedicated servers need the scene/ZDO
            // world; clients and hosts need a spawned local character outside loading screens.
            if (GameEnvironment.Dedicated)
            {
                if (!GameEnvironment.WorldReady) return;
            }
            else if (!WorldManager.ClientReady())
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
