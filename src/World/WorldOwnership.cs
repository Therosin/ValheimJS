using HarmonyLib;

namespace ValheimJS.World;

/// <summary>
/// Keeps simulated objects owned by the server. Vanilla releases nearby ZDOs to peers so those
/// peers can simulate them; clients are only viewers for these objects, so re-claim immediately.
/// </summary>
[ValheimJS.Scripting.Shared]
[HarmonyPatch(typeof(ZDOMan), "ReleaseZDOS")]
internal static class WorldOwnership
{
    [HarmonyPostfix]
    private static void Postfix() => WorldManager.Instance?.ReclaimOwnership();
}
