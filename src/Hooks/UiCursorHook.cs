#if USE_GUI
using HarmonyLib;
using UnityEngine;
using ValheimJS.UI;

namespace ValheimJS.Hooks;

/// <summary>Lets the player use the mouse while a panel is open by holding Alt.</summary>
[ValheimJS.Scripting.ClientOnly]
[HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
internal static class UiCursorHook
{
    private static void Postfix()
    {
        if (!UiHost.CursorUnlock)
        {
            return;
        }

        ZCursor.LockState = CursorLockMode.None;
        ZCursor.Show();
    }
}
#endif
