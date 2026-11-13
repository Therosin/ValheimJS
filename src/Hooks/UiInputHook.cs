#if USE_GUI
using HarmonyLib;
using ValheimJS.UI;

namespace ValheimJS.Hooks;

/// <summary>Blocks game input while a plugin modal panel owns the screen.</summary>
[ValheimJS.Scripting.ClientOnly]
[HarmonyPatch(typeof(Player), "TakeInput")]
internal static class UiPlayerInputHook
{
    private static void Postfix(ref bool __result)
    {
        if (__result && (UiHost.HasModal || UiHost.TextFocused))
        {
            __result = false;
        }
    }
}

[ValheimJS.Scripting.ClientOnly]
[HarmonyPatch(typeof(PlayerController), "TakeInput")]
internal static class UiPlayerControllerInputHook
{
    private static void Postfix(ref bool __result)
    {
        if (__result && (UiHost.HasModal || UiHost.TextFocused))
        {
            __result = false;
        }
    }
}
#endif
