#if USE_INPUT
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem.Controls;
using ValheimJS.Input;

namespace ValheimJS.Hooks;

[ValheimJS.Scripting.ClientOnly]
[HarmonyPatch(typeof(ZInput), nameof(ZInput.Update))]
internal static class KeyboardUpdateHook
{
    private static void Postfix()
    {
        KeyboardManager.Update();
    }
}

// Mask named game actions only when their resolved keyboard control was claimed.
[ValheimJS.Scripting.ClientOnly]
[HarmonyPatch(typeof(ZInput), "TryGetButtonState")]
internal static class KeyboardClaimHook
{
    private static void Postfix(string name, Dictionary<string, ZInput.ButtonDef> ___m_buttons, ref bool __result)
    {
        if (!__result || !KeyboardManager.HandledAny || !___m_buttons.TryGetValue(name, out var button)) return;
        var action = button.ButtonAction;
        if (action == null) return;
        var claimed = false;
        foreach (var control in action.controls)
        {
            if (control is KeyControl key && KeyboardManager.IsClaimed(key.keyCode)) claimed = true;
            else if (control is ButtonControl other && other.isPressed) return;
        }
        if (claimed) __result = false;
    }
}
#endif
