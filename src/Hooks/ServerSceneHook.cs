using HarmonyLib;

namespace ValheimJS.Hooks;

/// <summary>
/// Runs immediately before ZNetScene decides which ZDOs get bodies. On a dedicated server no
/// local player ever sets the reference position, so the scene would keep streaming the sentinel
/// area and no player or NPC body would ever be instantiated. This hook is the ordering-safe
/// point to point the reference at a real player (body, peer reference or player ZDO).
/// </summary>
[ValheimJS.Scripting.ServerOnly]
[HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
internal static class ServerSceneHook
{
    [HarmonyPrefix]
    private static void Prefix() => World.WorldManager.Instance?.SyncReference();
}

/// <summary>
/// Vanilla <c>Game.FixedUpdate</c> runs a respawn pass on a dedicated server and resets the
/// reference position to the local player profile's logout/start point every physics step.
/// Re-sync after it, before any <c>Update</c> (ZoneSystem streams zones in Update), so the whole
/// frame uses a real player's position.
/// </summary>
[ValheimJS.Scripting.ServerOnly]
[HarmonyPatch(typeof(Game), "FixedUpdate")]
internal static class ServerFixedUpdateHook
{
    [HarmonyPostfix]
    private static void Postfix() => World.WorldManager.Instance?.SyncReference();
}
