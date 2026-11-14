using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ValheimJS.World;

/// <summary>
/// Native inventory/equipment helpers shared by every inventory accessor (NPC body, player body,
/// world container). Persistence is deliberately provider-agnostic: it serializes the native
/// <see cref="Inventory"/> so the same code can back future player-owned inventories.
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
        var data = drop.m_itemData.Clone();
        data.m_dropPrefab = prefab;
        data.m_stack = Mathf.Clamp(count, 1, Mathf.Max(1, data.m_shared.m_maxStackSize));
        if (quality > 0) data.m_quality = Mathf.Clamp(quality, 1, Mathf.Max(1, data.m_shared.m_maxQuality));
        if (data.m_shared.m_maxDurability > 0f) data.m_durability = data.m_shared.m_maxDurability;
        return inventory.AddItem(data) ? data.m_stack : 0;
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
        var items = new StringBuilder();
        foreach (var item in inventory.GetAllItems())
        {
            if (items.Length > 0) items.Append(';');
            items.Append(PrefabHash(item).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.m_stack.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.m_quality.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.m_durability.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.m_gridPos.x.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(item.m_gridPos.y.ToString(CultureInfo.InvariantCulture));
        }

        zdo.Set(WorldKeys.Inventory, items.ToString());

        var equipment = new StringBuilder();
        foreach (var entry in EquipmentFields)
        {
            if (entry.Field?.GetValue(humanoid) is not ItemDrop.ItemData item) continue;
            if (equipment.Length > 0) equipment.Append(';');
            equipment.Append(entry.Name).Append('=')
                .Append(PrefabHash(item).ToString(CultureInfo.InvariantCulture)).Append('@')
                .Append(item.m_gridPos.x.ToString(CultureInfo.InvariantCulture)).Append('@')
                .Append(item.m_gridPos.y.ToString(CultureInfo.InvariantCulture));
        }

        zdo.Set(WorldKeys.Equipment, equipment.ToString());
    }

    internal static void Restore(Humanoid humanoid, ZDO zdo)
    {
        var inventory = humanoid.GetInventory();
        var scene = ZNetScene.instance;
        if (inventory == null || scene == null) return;
        foreach (var existing in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
        {
            inventory.RemoveItem(existing);
        }

        if (zdo.GetString(WorldKeys.Inventory, out var saved) && !string.IsNullOrEmpty(saved))
        {
            foreach (var entry in saved.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split(',');
                if (parts.Length < 6) continue;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hash) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stack) ||
                    !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality) ||
                    !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var durability) ||
                    !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slotX) ||
                    !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slotY))
                {
                    continue;
                }

                var prefab = scene.GetPrefab(hash);
                var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop == null) continue;
                var data = drop.m_itemData.Clone();
                data.m_dropPrefab = prefab;
                data.m_stack = Mathf.Max(1, stack);
                data.m_quality = Mathf.Max(1, quality);
                data.m_durability = durability;
                data.m_gridPos = new Vector2i(slotX, slotY);
                inventory.AddItem(data, data.m_gridPos);
            }
        }

        if (!zdo.GetString(WorldKeys.Equipment, out var equipped) || string.IsNullOrEmpty(equipped)) return;
        foreach (var entry in equipped.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = entry.IndexOf('=');
            if (equals <= 0) continue;
            var slot = entry.Substring(0, equals);
            var parts = entry.Substring(equals + 1).Split('@');
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hash) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slotX) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slotY))
            {
                continue;
            }

            foreach (var item in inventory.GetAllItems())
            {
                if (PrefabHash(item) == hash && item.m_gridPos.x == slotX && item.m_gridPos.y == slotY)
                {
                    humanoid.EquipItem(item);
                    break;
                }
            }
        }
    }
}
