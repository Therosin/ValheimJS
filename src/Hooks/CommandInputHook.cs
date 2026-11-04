using System;
using HarmonyLib;
using ValheimJS.Commands;
using ValheimJS.Scripting;

namespace ValheimJS.Hooks;

[ClientOnly]
[HarmonyPatch(typeof(Chat), "InputText")]
internal static class CommandInputHook
{
    private static bool Prefix(Chat __instance)
    {
        var text = __instance.m_input.text;
        if (!ChatCommandParser.IsCommand(text)) return true;
        try { Plugin.CommandTransport?.Submit(text); }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("Command submission failed: " + exception);
            global::ValheimJS.Networking.CommandService.Show("Command submission failed.");
        }
        return false; // Never send ! input through the game's say/broadcast path.
    }
}
