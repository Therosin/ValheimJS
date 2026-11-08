using HarmonyLib;

namespace ValheimJS.Hooks;

/// <summary>Applies queued NPC prefabs once the world scene exists.</summary>
[ValheimJS.Scripting.Shared]
[HarmonyPatch(typeof(ZNetScene), "Awake")]
internal static class NpcSceneHook
{
    [HarmonyPostfix]
    private static void Postfix() => World.WorldManager.Instance?.ApplyPendingPrefabs();
}
