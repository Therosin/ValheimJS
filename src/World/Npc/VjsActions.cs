using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimJS.World;

/// <summary>
/// Server-side native actions that vanilla only exposes through a Player/GUI. Each method
/// validates, performs the native operation and returns a stable failure reason.
/// </summary>
internal static class VjsActions
{
    private const float PickRange = 3.5f;
    private static readonly FieldInfo? PickablePicked = typeof(Pickable).GetField(
        "m_picked", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static bool IsPickableReady(Pickable? pickable) =>
        pickable != null && pickable.GetEnabled != 0 && PickablePicked?.GetValue(pickable) is not true;

    internal static bool IsFoodPickable(Pickable? pickable)
    {
        var prefab = pickable != null ? pickable.m_itemPrefab : null;
        var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
        var shared = drop != null ? drop.m_itemData.m_shared : null;
        return shared != null && (shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f);
    }

    internal static bool Pick(WorldObject obj, string handle, out string? reason)
    {
        reason = null;
        if (!WorldManager.TryParseHandle(handle, out var id))
        {
            reason = "invalid-target";
            return false;
        }

        var scene = ZNetScene.instance;
        var instance = scene?.FindInstance(id);
        var pickable = instance?.GetComponent<Pickable>();
        if (pickable == null)
        {
            reason = "target-missing";
            return false;
        }

        if (pickable.GetEnabled == 0)
        {
            reason = "not-ready";
            return false;
        }

        if (PickablePicked?.GetValue(pickable) is true)
        {
            reason = "not-ready";
            return false;
        }

        if (obj.Character == null || obj.Character is not Humanoid humanoid)
        {
            reason = "body-inactive";
            return false;
        }

        if (Vector3.Distance(obj.Character.transform.position, pickable.transform.position) > PickRange)
        {
            reason = "out-of-range";
            return false;
        }

        var view = instance.GetComponent<ZNetView>();
        if (view != null && !view.IsOwner())
        {
            view.ClaimOwnership();
        }

        var itemPrefab = pickable.m_itemPrefab;
        var itemDrop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
        if (itemDrop == null)
        {
            reason = "invalid-target";
            return false;
        }

        var count = pickable.m_dontScale
            ? Mathf.Max(1, pickable.m_amount)
            : Mathf.Max(pickable.m_minAmountScaled, pickable.m_amount);
        var position = pickable.transform.position +
                       (pickable.m_spawnOffsetLocalTransform ? pickable.transform.up : Vector3.up) * pickable.m_spawnOffset;
        var rotation = Quaternion.identity;

        for (var index = 0; index < count; index++)
        {
            var data = itemDrop.m_itemData.Clone();
            data.m_dropPrefab = itemPrefab;
            data.m_stack = 1;
            var dropped = ItemDrop.DropItem(data, 1, position, rotation);
            if (dropped != null)
            {
                humanoid.Pickup(dropped.gameObject, autoequip: false, autoPickupDelay: false);
            }
        }

        if (pickable.m_extraDrops != null && !pickable.m_extraDrops.IsEmpty())
        {
            foreach (var extra in pickable.m_extraDrops.GetDropListItems())
            {
                if (extra?.m_dropPrefab == null) continue;
                var dropped = ItemDrop.DropItem(extra, Mathf.Max(1, extra.m_stack), position, rotation);
                if (dropped != null)
                {
                    humanoid.Pickup(dropped.gameObject, autoequip: false, autoPickupDelay: false);
                }
            }
        }

        if (pickable.m_pickEffector != null)
        {
            var effectPosition = pickable.m_pickEffectAtSpawnPoint ? position : pickable.transform.position;
            pickable.m_pickEffector.Create(effectPosition, rotation, null, 1f, -1, humanoid.GetZDOID());
        }

        pickable.SetPicked(true);
        if (pickable.m_aggravateRange > 0f)
        {
            BaseAI.AggravateAllInArea(pickable.transform.position, pickable.m_aggravateRange, BaseAI.AggravatedReason.Theif);
        }

        return true;
    }
}
