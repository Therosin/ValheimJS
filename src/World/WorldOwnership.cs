using HarmonyLib;

namespace ValheimJS.World;

/// <summary>
/// Keeps simulated NPCs server-owned while nearby world objects follow vanilla ownership: after
/// the release pass, interactive ZDOs near a player belong to that player so their interaction
/// RPCs execute where a local player exists.
/// </summary>
[ValheimJS.Scripting.Shared]
[HarmonyPatch(typeof(ZDOMan), "ReleaseZDOS")]
internal static class WorldOwnership
{
    [HarmonyPostfix]
    private static void Postfix() => WorldManager.Instance?.RebalanceOwnership();
}
