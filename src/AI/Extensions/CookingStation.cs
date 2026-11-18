using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimJS.Entities;
using ValheimJS.World;

namespace ValheimJS.AI.Extensions;

/// <summary>
/// CookingStation is a player-only interactable in vanilla: OnInteract dereferences
/// Player.m_localPlayer when a finished item is collected. This adapter owns the whole lifecycle
/// for a scripted Humanoid: add raw food, watch the station, take the finished item and publish a
/// board event. JS never polls the native station.
/// </summary>
internal sealed class CookingStationExtension : INpcInteractionExtension
{
    private const string EventKey = "cook";
    private const float WatchInterval = 0.5f;
    private const float WatchTimeout = 600f;

    private static readonly MethodInfo? SetSlot = typeof(CookingStation).GetMethod(
        "SetSlot",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Type? StatusType = typeof(CookingStation).GetNestedType(
        "Status",
        BindingFlags.NonPublic);
    private static readonly Dictionary<string, Coroutine> Watchers = new(StringComparer.Ordinal);

    public bool TryDescribe(GameObject instance, NpcAffordances result)
    {
        var station = instance.GetComponent<CookingStation>();
        if (station == null)
        {
            return false;
        }

        var (free, raw, done) = CountSlots(station);
        result.Kind = "cooking";
        result.Verbs = new[] { "cook" };
        result.Supported = true;
        result.PlayerOnly = false;
        result.Requires = RawNames(station);
        result.Gives = CookedNames(station);
        result.ReadyCount = done;
        result.State = done > 0 ? "done" : raw > 0 ? "cooking" : free > 0 ? "idle" : "full";
        return true;
    }

    public NpcInteractionResult? Interact(
        WorldObject obj,
        GameObject instance,
        string verb,
        NpcInteractOptions options)
    {
        if (!string.Equals(verb, "cook", StringComparison.Ordinal))
        {
            return null;
        }

        var station = instance.GetComponent<CookingStation>();
        var humanoid = obj.Character as Humanoid;
        if (station == null || humanoid == null)
        {
            return NpcInteractionResult.Fail(station == null ? "not-cooking" : "body-inactive", verb);
        }

        if (TryTakeDone(station, humanoid, obj.Handle, out var taken))
        {
            return new NpcInteractionResult { Ok = true, Verb = verb, Count = 1 };
        }

        if (taken != null)
        {
            return NpcInteractionResult.Fail(taken, verb);
        }

        var (free, raw, done) = CountSlots(station);
        if (done > 0)
        {
            return NpcInteractionResult.Fail("no-room", verb);
        }

        if (free > 0 && TryAddRaw(station, humanoid))
        {
            StartWatcher(station, humanoid, obj.Handle);
            return new NpcInteractionResult { Ok = true, Verb = verb, Count = 1 };
        }

        if (raw > 0)
        {
            StartWatcher(station, humanoid, obj.Handle);
            return new NpcInteractionResult { Ok = true, Verb = verb, Count = 1 };
        }

        if (free == 0)
        {
            return NpcInteractionResult.Fail("full", verb);
        }

        return NpcInteractionResult.Fail("no-food", verb);
    }

    private static bool TryAddRaw(CookingStation station, Humanoid humanoid)
    {
        var inventory = humanoid.GetInventory();
        if (inventory == null)
        {
            return false;
        }

        foreach (var item in inventory.GetAllItems())
        {
            if (item == null || item.m_shared == null || !IsFood(item.m_shared))
            {
                continue;
            }

            try
            {
                if (station.UseItem(humanoid, item))
                {
                    Plugin.Logger.LogInfo($"npc cook: {humanoid.m_name} added {item.m_shared.m_name}");
                    return true;
                }
            }
            catch (Exception exception)
            {
                PluginLogger.Warn($"cooking station rejected {item.m_shared.m_name}: {exception.Message}");
            }
        }

        return false;
    }

    private static bool TryTakeDone(
        CookingStation station,
        Humanoid humanoid,
        string handle,
        out string? failure)
    {
        failure = null;
        var slots = station.m_slots?.Length ?? 0;
        var inventory = humanoid.GetInventory();
        if (inventory == null)
        {
            failure = "no-inventory";
            return false;
        }

        var zdo = View(station)?.GetZDO();
        if (zdo == null)
        {
            failure = "not-loaded";
            return false;
        }

        for (var index = 0; index < slots; index++)
        {
            var name = zdo.GetString("slot" + index);
            if (string.IsNullOrEmpty(name) || !IsDone(station, zdo, index))
            {
                continue;
            }

            var prefab = ObjectDB.instance?.GetItemPrefab(name);
            var drop = prefab?.GetComponent<ItemDrop>();
            if (drop == null)
            {
                failure = "unknown-item";
                return false;
            }

            var item = drop.m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_stack = 1;
            if (!inventory.AddItem(item))
            {
                failure = "no-room";
                return false;
            }

            ClearSlot(station, index);
            Emit(handle, true, name, 1);
            Plugin.Logger.LogInfo($"npc cook: {humanoid.m_name} took {name}");
            return true;
        }

        return false;
    }

    private static IEnumerator Watch(
        CookingStation station,
        Humanoid humanoid,
        string handle,
        string key)
    {
        try
        {
            var elapsed = 0f;
            while (elapsed < WatchTimeout)
            {
                yield return new WaitForSeconds(WatchInterval);
                elapsed += WatchInterval;

                if (station == null || humanoid == null)
                {
                    Emit(handle, false, null, 0, "body-lost");
                    yield break;
                }

                if (TryTakeDone(station, humanoid, handle, out var failure))
                {
                    yield break;
                }

                if (failure != null && failure != "no-room")
                {
                    Emit(handle, false, null, 0, failure);
                    yield break;
                }

                var (free, raw, done) = CountSlots(station);
                if (raw == 0 && done == 0 && free > 0)
                {
                    Emit(handle, false, null, 0, "station-empty");
                    yield break;
                }
            }

            Emit(handle, false, null, 0, "timeout");
        }
        finally
        {
            Watchers.Remove(key);
        }
    }

    private static void StartWatcher(CookingStation station, Humanoid humanoid, string handle)
    {
        var key = handle + "|" + station.GetInstanceID();
        if (Watchers.ContainsKey(key))
        {
            return;
        }

        var routine = station.StartCoroutine(Watch(station, humanoid, handle, key));
        Watchers[key] = routine;
    }

    private static void ClearSlot(CookingStation station, int index)
    {
        if (StatusType == null || SetSlot == null)
        {
            return;
        }

        SetSlot.Invoke(
            station,
            new[]
            {
                index,
                string.Empty,
                0f,
                Enum.ToObject(StatusType, 0),
                false,
                0L
            });
        View(station)?.InvokeRPC(ZNetView.Everybody, "RPC_SetSlotVisual", index, string.Empty);
    }

    private static ZNetView? View(CookingStation station) => station.GetComponent<ZNetView>();

    private static (int Free, int Raw, int Done) CountSlots(CookingStation station)
    {
        var zdo = View(station)?.GetZDO();
        var slots = station.m_slots?.Length ?? 0;
        if (zdo == null)
        {
            return (0, 0, 0);
        }

        var free = 0;
        var raw = 0;
        var done = 0;
        for (var index = 0; index < slots; index++)
        {
            var name = zdo.GetString("slot" + index);
            if (string.IsNullOrEmpty(name))
            {
                free++;
            }
            else if (IsDone(station, zdo, index))
            {
                done++;
            }
            else
            {
                raw++;
            }
        }

        return (free, raw, done);
    }

    private static bool IsDone(CookingStation station, ZDO zdo, int index)
    {
        var name = zdo.GetString("slot" + index);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (zdo.GetInt("slotstatus" + index) != 0)
        {
            return true;
        }

        if (station.m_overCookedItem != null && name == station.m_overCookedItem.gameObject.name)
        {
            return true;
        }

        foreach (var conversion in station.m_conversion)
        {
            if (conversion?.m_to != null && name == conversion.m_to.gameObject.name)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> RawNames(CookingStation station)
    {
        var names = new List<string>();
        foreach (var conversion in station.m_conversion)
        {
            if (conversion?.m_from != null)
            {
                names.Add(conversion.m_from.gameObject.name);
            }
        }

        return names;
    }

    private static IReadOnlyList<string> CookedNames(CookingStation station)
    {
        var names = new List<string>();
        foreach (var conversion in station.m_conversion)
        {
            if (conversion?.m_to != null)
            {
                names.Add(conversion.m_to.gameObject.name);
            }
        }

        return names;
    }

    private static bool IsFood(ItemDrop.ItemData.SharedData shared) =>
        shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f;

    private static void Emit(
        string handle,
        bool ok,
        string? item,
        int count,
        string? reason = null)
    {
        NpcBoards.For(handle).Set(
            EventKey,
            new Dictionary<string, object?>
            {
                ["ok"] = ok,
                ["item"] = item,
                ["count"] = count,
                ["reason"] = reason,
                ["at"] = Time.time
            });
    }
}
