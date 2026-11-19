using HarmonyLib;

namespace ValheimJS.World;

/// <summary>
/// Marks the NPC <see cref="Container"/> as a UI adapter, not a networked chest. Vanilla
/// <see cref="InventoryGui"/> keeps the whole container grid, drag, split, stack-all and take-all
/// implementation; only ownership/persistence are redirected.
/// </summary>
internal static class NpcInventoryContainerPatches
{
    private static NpcInventoryUi? Adapter(Container container) =>
        container != null ? container.GetComponent<NpcInventoryUi>() : null;

    [ValheimJS.Scripting.Shared]
    [HarmonyPatch(typeof(Container), nameof(Container.Interact))]
    private static class InteractPatch
    {
        private static bool Prefix(Container __instance, Humanoid character, bool hold, ref bool __result)
        {
            var ui = Adapter(__instance);
            if (ui == null) return true;
            __result = ui.TryInteract(character, hold);
            return false;
        }
    }

    [ValheimJS.Scripting.Shared]
    [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
    private static class HoverTextPatch
    {
        private static bool Prefix(Container __instance, ref string __result)
        {
            var ui = Adapter(__instance);
            if (ui == null) return true;
            __result = ui.HoverText();
            return false;
        }
    }

    [ValheimJS.Scripting.Shared]
    [HarmonyPatch(typeof(Container), nameof(Container.GetHoverName))]
    private static class HoverNamePatch
    {
        private static bool Prefix(Container __instance, ref string __result)
        {
            var ui = Adapter(__instance);
            if (ui == null) return true;
            __result = ui.HoverName();
            return false;
        }
    }

    [ValheimJS.Scripting.Shared]
    [HarmonyPatch(typeof(Container), nameof(Container.IsOwner))]
    private static class OwnerPatch
    {
        private static bool Prefix(Container __instance, ref bool __result)
        {
            var ui = Adapter(__instance);
            if (ui == null) return true;
            // The UI process must accept the local mirror even though the body ZDO is server-owned.
            __result = !Core.GameEnvironment.Dedicated;
            return false;
        }
    }

    [ValheimJS.Scripting.Shared]
    [HarmonyPatch(typeof(Container), nameof(Container.SetInUse))]
    private static class InUsePatch
    {
        private static bool Prefix(Container __instance) => Adapter(__instance) == null;
    }

    [ValheimJS.Scripting.Shared]
    [HarmonyPatch(typeof(Container), "Save")]
    private static class SavePatch
    {
        private static bool Prefix(Container __instance) => Adapter(__instance) == null;
    }

    [ValheimJS.Scripting.Shared]
    [HarmonyPatch(typeof(Container), "Load")]
    private static class LoadPatch
    {
        private static bool Prefix(Container __instance) => Adapter(__instance) == null;
    }
}
