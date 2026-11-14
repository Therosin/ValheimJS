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

            // A reachable ZNet is not a playable world. On a client or player host, wait for the
            // local character to finish loading so hooks do not fire under the loading screen.
            if (!GameEnvironment.Dedicated && !WorldManager.ClientReady())
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
