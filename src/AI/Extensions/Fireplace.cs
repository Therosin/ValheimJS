using System;
using UnityEngine;
using ValheimJS.World;

namespace ValheimJS.AI.Extensions;

/// <summary>
/// Lets a scripted body refill a vanilla fire pit. Fireplace.Interact already owns the inventory
/// and RPC side, so the adapter only classifies the target and supplies the NPC-side verb.
/// </summary>
internal sealed class FireplaceExtension : INpcInteractionExtension
{
    public bool TryDescribe(GameObject instance, NpcAffordances result)
    {
        var fire = instance.GetComponent<Fireplace>();
        if (fire == null) return false;

        var zdo = instance.GetComponent<ZNetView>()?.GetZDO();
        var fuel = zdo != null ? zdo.GetFloat("fuel") : 0f;
        result.Kind = "fireplace";
        result.Verbs = new[] { "addFuel" };
        result.Supported = fire.m_canRefill && !fire.m_infiniteFuel;
        result.ReadyCount = Mathf.CeilToInt(fuel);
        result.Requires = fire.m_fuelItem != null
            ? new[] { fire.m_fuelItem.m_itemData.m_shared.m_name }
            : Array.Empty<string>();
        result.State = !fire.m_canRefill || fire.m_infiniteFuel
            ? "unsupported"
            : fuel <= 0f
                ? "empty"
                : fuel >= fire.m_maxFuel
                    ? "full"
                    : "burning";
        return true;
    }

    public NpcInteractionResult? Interact(
        WorldObject obj,
        GameObject instance,
        string verb,
        NpcInteractOptions options)
    {
        if (!string.Equals(verb, "addFuel", StringComparison.Ordinal)) return null;

        var fire = instance.GetComponent<Fireplace>();
        var humanoid = obj.Character as Humanoid;
        if (fire == null || humanoid == null)
        {
            return NpcInteractionResult.Fail(fire == null ? "not-fireplace" : "body-inactive", verb);
        }

        if (!fire.Interact(humanoid, false, false))
        {
            var fuel = instance.GetComponent<ZNetView>()?.GetZDO()?.GetFloat("fuel") ?? 0f;
            if (fuel >= fire.m_maxFuel) return NpcInteractionResult.Fail("full", verb);
            return NpcInteractionResult.Fail("no-fuel", verb);
        }

        return new NpcInteractionResult { Ok = true, Verb = verb, Count = 1 };
    }
}
