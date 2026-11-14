using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimJS.Core;
using ValheimJS.Entities;

namespace ValheimJS.World;

/// <summary>
/// Native inventory/equipment helpers shared by the NPC body and world-container accessors.
/// Persistence serializes the native <see cref="Inventory"/> plus the body's equipment fields.
/// </summary>
internal static class VjsInventory
{
    private static readonly (string Name, FieldInfo? Field)[] EquipmentFields =
    {
        ("right", Field("m_rightItem")),
        ("left", Field("m_leftItem")),
        ("helmet", Field("m_helmetItem")),
        ("chest", Field("m_chestItem")),
        ("legs", Field("m_legItem")),
        ("shoulder", Field("m_shoulderItem")),
        ("utility", Field("m_utilityItem")),
        ("trinket", Field("m_trinketItem"))
    };

    private static FieldInfo? Field(string name) =>
        typeof(Humanoid).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

    internal static Dictionary<string, object?> Snapshot(Humanoid? humanoid, Inventory? inventory)
    {
        var items = new List<object?>();
        var equipment = new Dictionary<string, object?>();
        foreach (var entry in EquipmentFields)
        {
            equipment[entry.Name] = null;
        }

        if (inventory != null)
        {
            foreach (var item in inventory.GetAllItems())
            {
                items.Add(ItemView(item, humanoid));
            }
        }

        if (humanoid != null)
        {
            foreach (var entry in EquipmentFields)
            {
                if (entry.Field?.GetValue(humanoid) is ItemDrop.ItemData item)
                {
                    equipment[entry.Name] = ItemView(item, humanoid);
                }
            }
        }

        var width = inventory?.GetWidth() ?? 0;
        var height = inventory?.GetHeight() ?? 0;
        var used = inventory?.NrOfItems() ?? 0;
        return new Dictionary<string, object?>
        {
            ["available"] = inventory != null,
            ["kind"] = humanoid != null ? "body" : "container",
            ["items"] = items,
            ["equipment"] = equipment,
            ["weight"] = inventory?.GetTotalWeight() ?? 0f,
            ["freeSlots"] = Mathf.Max(0, width * height - used),
            ["capacity"] = width * height
        };
    }

    internal static Dictionary<string, object?> ItemView(ItemDrop.ItemData item, Humanoid? humanoid)
    {
        var shared = item.m_shared;
        var food = shared != null && (shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f)
            ? new Dictionary<string, object?>
            {
                ["health"] = shared.m_food,
                ["stamina"] = shared.m_foodStamina,
                ["eitr"] = shared.m_foodEitr,
                ["burnTime"] = shared.m_foodBurnTime,
                ["regen"] = shared.m_foodRegen
            }
            : null;

        return new Dictionary<string, object?>
        {
            ["slot"] = new Dictionary<string, object?> { ["x"] = item.m_gridPos.x, ["y"] = item.m_gridPos.y },
            ["prefab"] = PrefabName(item),
            ["name"] = shared?.m_name ?? "",
            ["stack"] = item.m_stack,
            ["quality"] = item.m_quality,
            ["durability"] = item.m_durability,
            ["maxDurability"] = shared?.m_maxDurability ?? 0f,
            ["weight"] = item.GetWeight(),
            ["type"] = Kind(item),
            ["equipped"] = humanoid != null && humanoid.IsItemEquiped(item),
            ["food"] = food
        };
    }

    internal static string Kind(ItemDrop.ItemData item)
    {
        var shared = item.m_shared;
        if (shared == null) return "misc";
        if (shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f) return "food";
        return shared.m_itemType switch
        {
            ItemDrop.ItemData.ItemType.Consumable => "mead",
            ItemDrop.ItemData.ItemType.OneHandedWeapon => "weapon",
            ItemDrop.ItemData.ItemType.TwoHandedWeapon => "weapon",
            ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft => "weapon",
            ItemDrop.ItemData.ItemType.Bow => "weapon",
            ItemDrop.ItemData.ItemType.Shield => "armor",
            ItemDrop.ItemData.ItemType.Helmet => "armor",
            ItemDrop.ItemData.ItemType.Chest => "armor",
            ItemDrop.ItemData.ItemType.Legs => "armor",
            ItemDrop.ItemData.ItemType.Shoulder => "armor",
            ItemDrop.ItemData.ItemType.Utility => "armor",
            ItemDrop.ItemData.ItemType.Trinket => "armor",
            ItemDrop.ItemData.ItemType.Tool => "tool",
            ItemDrop.ItemData.ItemType.Torch => "tool",
            ItemDrop.ItemData.ItemType.Ammo => "ammo",
            ItemDrop.ItemData.ItemType.Material => "material",
            _ => "misc"
        };
    }

    internal static string PrefabName(ItemDrop.ItemData item)
    {
        if (item.m_dropPrefab != null) return item.m_dropPrefab.name;
        return item.m_shared?.m_name ?? "";
    }

    internal static int PrefabHash(ItemDrop.ItemData item) =>
        PrefabName(item).GetStableHashCode();

    internal static ItemDrop.ItemData? Resolve(Inventory? inventory, string? prefab, string? name, int? slotX, int? slotY)
    {
        if (inventory == null) return null;
        foreach (var item in inventory.GetAllItems())
        {
            if (slotX.HasValue && slotY.HasValue &&
                (item.m_gridPos.x != slotX.Value || item.m_gridPos.y != slotY.Value))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(prefab) &&
                !string.Equals(PrefabName(item), prefab, StringComparison.Ordinal) &&
                !string.Equals(item.m_dropPrefab?.name, prefab, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(name) && !string.Equals(item.m_shared?.m_name, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrEmpty(prefab) && string.IsNullOrEmpty(name) && !slotX.HasValue)
            {
                continue;
            }

            return item;
        }

        return null;
    }

    internal static int Count(Inventory? inventory, string? prefab, string? name)
    {
        if (inventory == null) return 0;
        var total = 0;
        foreach (var item in inventory.GetAllItems())
        {
            if (!string.IsNullOrEmpty(prefab) &&
                !string.Equals(PrefabName(item), prefab, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(name) && !string.Equals(item.m_shared?.m_name, name, StringComparison.Ordinal))
            {
                continue;
            }

            total += item.m_stack;
        }

        return total;
    }

    internal static int Add(Inventory inventory, string prefabName, int count, int quality)
    {
        var scene = ZNetScene.instance;
        var prefab = scene?.GetPrefab(prefabName);
        var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
        if (drop == null || count <= 0) return 0;
        var maxStack = Mathf.Max(1, drop.m_itemData.m_shared.m_maxStackSize);
        var hash = prefab!.name.GetStableHashCode();
        var remaining = count;
        var added = 0;
        while (remaining > 0)
        {
            var data = drop.m_itemData.Clone();
            data.m_dropPrefab = prefab;
            data.m_stack = Mathf.Min(remaining, maxStack);
            if (quality > 0) data.m_quality = Mathf.Clamp(quality, 1, Mathf.Max(1, data.m_shared.m_maxQuality));
            if (data.m_shared.m_maxDurability > 0f) data.m_durability = data.m_shared.m_maxDurability;

            var before = CountHash(inventory, hash);
            if (!inventory.AddItem(data)) break;
            var gained = CountHash(inventory, hash) - before;
            if (gained <= 0) break;
            added += gained;
            remaining -= gained;
        }

        return added;
    }

    private static int CountHash(Inventory inventory, int hash)
    {
        var total = 0;
        foreach (var item in inventory.GetAllItems())
        {
            if (PrefabHash(item) == hash) total += item.m_stack;
        }

        return total;
    }

    internal static int Remove(Inventory inventory, ItemDrop.ItemData item, int count)
    {
        var amount = Mathf.Clamp(count, 1, item.m_stack);
        return inventory.RemoveItem(item, amount) ? amount : 0;
    }

    internal static bool Use(Humanoid humanoid, ItemDrop.ItemData item)
    {
        var kind = Kind(item);
        if (kind == "food" || kind == "mead")
        {
            var stats = humanoid.GetComponent<VjsBodyStats>();
            return stats != null && stats.TryConsume(item, out _);
        }

        return TryEquip(humanoid, item);
    }

    internal static bool TryEquip(Humanoid humanoid, ItemDrop.ItemData item)
    {
        var kind = Kind(item);
        if (kind is not ("weapon" or "tool" or "armor")) return false;
        return humanoid.EquipItem(item);
    }

    /// <summary>Equips the first weapon found in inventory order; used when a body starts.</summary>
    internal static ItemDrop.ItemData? EquipFirstWeapon(Humanoid humanoid)
    {
        var inventory = humanoid.GetInventory();
        if (inventory == null) return null;
        foreach (var item in inventory.GetAllItems())
        {
            if (Kind(item) != "weapon") continue;
            if (humanoid.EquipItem(item)) return item;
        }

        return null;
    }

    internal static bool Unequip(Humanoid humanoid, ItemDrop.ItemData item)
    {
        humanoid.UnequipItem(item, triggerEquipEffects: false);
        return true;
    }

    internal static bool Drop(Humanoid humanoid, ItemDrop.ItemData item, int count) =>
        humanoid.DropItem(humanoid.GetInventory(), item, Mathf.Clamp(count, 1, item.m_stack));

    internal static bool CanUse(Humanoid? humanoid, ItemDrop.ItemData? item)
    {
        if (humanoid == null || item == null) return false;
        var kind = Kind(item);
        if (kind is "food" or "mead")
        {
            var stats = humanoid.GetComponent<VjsBodyStats>();
            return stats != null && stats.CanConsume(item);
        }

        return kind is "weapon" or "tool" or "armor";
    }

    internal static void Save(Humanoid humanoid, ZDO zdo)
    {
        var inventory = humanoid.GetInventory();
        if (inventory == null) return;
        var payload = new NpcInventoryPayload();
        foreach (var item in inventory.GetAllItems())
        {
            payload.Items.Add(new NpcItemRecord
            {
                PrefabHash = PrefabHash(item),
                Stack = item.m_stack,
                Quality = item.m_quality,
                Durability = item.m_durability,
                X = item.m_gridPos.x,
                Y = item.m_gridPos.y
            });
        }

        foreach (var entry in EquipmentFields)
        {
            if (entry.Field?.GetValue(humanoid) is not ItemDrop.ItemData item) continue;
            payload.Equipment.Add(new NpcEquipmentRecord
            {
                Slot = entry.Name,
                PrefabHash = PrefabHash(item),
                X = item.m_gridPos.x,
                Y = item.m_gridPos.y
            });
        }

        zdo.Set(WorldKeys.Inventory, payload.Encode());
    }

    /// <summary>
    /// Restores a persisted inventory. The payload is validated before the live inventory is
    /// cleared, missing prefabs are reported, failed slot placement falls back to any free slot,
    /// and equipment is re-equipped from the item that was actually added.
    /// </summary>
    internal static void Restore(Humanoid humanoid, ZDO zdo)
    {
        var inventory = humanoid.GetInventory();
        var scene = ZNetScene.instance;
        if (inventory == null || scene == null) return;
        if (!zdo.GetString(WorldKeys.Inventory, out var saved) || string.IsNullOrEmpty(saved))
        {
            return;
        }

        if (!NpcInventoryPayload.TryDecode(saved, out var payload, out var error))
        {
            PluginLogger.Warn($"npc inventory: restore skipped: {error}");
            return;
        }

        if (payload.Skipped > 0)
        {
            PluginLogger.Warn($"npc inventory: ignored {payload.Skipped} malformed record(s)");
        }

        foreach (var existing in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
        {
            inventory.RemoveItem(existing);
        }

        var added = new Dictionary<(int Hash, int X, int Y), ItemDrop.ItemData>();
        foreach (var record in payload.Items)
        {
            var prefab = scene.GetPrefab(record.PrefabHash);
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null)
            {
                PluginLogger.Warn($"npc inventory: prefab {record.PrefabHash} is missing; dropping its stack");
                continue;
            }

            var data = drop.m_itemData.Clone();
            data.m_dropPrefab = prefab;
            data.m_stack = Mathf.Max(1, record.Stack);
            data.m_quality = Mathf.Max(1, record.Quality);
            data.m_durability = record.Durability;

            if (data.m_stack > 1)
            {
                // Stackable items do not preserve their slot; merge into the first free stack.
                data.m_stack = Mathf.Min(data.m_stack, Mathf.Max(1, data.m_shared.m_maxStackSize));
                if (!inventory.AddItem(data))
                {
                    PluginLogger.Warn($"npc inventory: no room for {record.Stack}x prefab {record.PrefabHash}");
                    continue;
                }
            }
            else
            {
                var inBounds = record.X >= 0 && record.Y >= 0 &&
                               record.X < inventory.GetWidth() && record.Y < inventory.GetHeight();
                data.m_gridPos = new Vector2i(record.X, record.Y);
                if ((!inBounds || !inventory.AddItem(data, data.m_gridPos)) && !inventory.AddItem(data))
                {
                    PluginLogger.Warn($"npc inventory: no room for prefab {record.PrefabHash}");
                    continue;
                }
            }

            added[(record.PrefabHash, record.X, record.Y)] = data;
        }

        foreach (var record in payload.Equipment)
        {
            if (!added.TryGetValue((record.PrefabHash, record.X, record.Y), out var item))
            {
                PluginLogger.Warn($"npc inventory: equipped '{record.Slot}' item was not restored");
                continue;
            }

            if (!humanoid.EquipItem(item))
            {
                PluginLogger.Warn($"npc inventory: could not re-equip '{record.Slot}'");
            }
        }
    }
}
