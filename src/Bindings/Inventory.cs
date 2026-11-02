using System;
using ValheimJS.Scripting.Binding;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ValheimJS.Bindings;

/// <summary>
/// Player inventory access. Scripts see <c>host.api.inventory.*</c>.
/// Item names are the prefab names players know from the game (<c>Wood</c>, <c>Coins</c>, …);
/// unknown names are rejected instead of guessed.
/// </summary>
[JsBinding("Inventory", JsBindPhase.Gameplay, order: 160)]
internal static class InventoryBindings
{
    public static void Bind(JsBindingContext context)
    {
        context.Add("inventory.list", new Func<long, string?>(List), JsReturnKind.Json);
        context.Add("inventory.count", new Func<long, string, int>(Count));
        context.Add("inventory.add", new Action<long, string, int>(Add));
        context.Add("inventory.remove", new Action<long, string, int>(Remove));
        context.Add("inventory.has", new Func<long, string, int, bool>((uid, item, amount) => Count(uid, item) >= amount));
        context.Add("inventory.items", new Func<string[]>(KnownItems));
    }

    private static Player? Player(long uid) => PlayerBindings.Resolve(uid);

    private static Inventory? InventoryOf(long uid) => Player(uid)?.GetInventory();

    private static string? List(long uid)
    {
        var inventory = InventoryOf(uid);
        if (inventory == null)
        {
            // Dedicated server: the owning client publishes a mirror (see InventorySnapshot).
            return Core.InventorySnapshot.Read(uid);
        }

        var items = inventory.GetAllItems() ?? new List<ItemDrop.ItemData>();
        var entries = items.Select(item =>
            "{" +
            $"\"name\":{Utils.Json.Quote(item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared?.m_name)}," +
            $"\"amount\":{item.m_stack}," +
            $"\"quality\":{item.m_quality}," +
            $"\"slot\":{{\"x\":{item.m_gridPos.x},\"y\":{item.m_gridPos.y}}}" +
            "}");

        return "[" + string.Join(",", entries) + "]";
    }

    private static int Count(long uid, string itemName)
    {
        var inventory = InventoryOf(uid);
        if (inventory != null)
        {
            return inventory.CountItems(itemName);
        }

        var mirror = Core.InventorySnapshot.Read(uid);
        return mirror == null ? 0 : Core.InventorySnapshot.Count(mirror, itemName);
    }

    private static void Add(long uid, string itemName, int amount)
    {
        var inventory = InventoryOf(uid);
        var prefab = ResolvePrefab(itemName);
        if (inventory == null || prefab == null || amount <= 0)
        {
            return;
        }

        inventory.AddItem(prefab, amount);
    }

    private static void Remove(long uid, string itemName, int amount)
    {
        var inventory = InventoryOf(uid);
        if (inventory == null || amount <= 0)
        {
            return;
        }

        inventory.RemoveItem(itemName, amount);
    }

    private static UnityEngine.GameObject? ResolvePrefab(string itemName) =>
        ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(itemName) : null;

    private static string[] KnownItems()
    {
        var database = ObjectDB.instance;
        if (database?.m_items == null)
        {
            return Array.Empty<string>();
        }

        return database.m_items
            .Where(item => item != null)
            .Select(item => item.name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
