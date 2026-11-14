using System;
using System.Collections.Generic;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using UnityEngine;
using ValheimJS.Scripting.Binding;
using ValheimJS.Scripting.Types;

namespace ValheimJS.World;

/// <summary>Native inventory backing for one accessor instance.</summary>
internal interface IInventoryProvider
{
    bool Available { get; }
    string Kind { get; }
    Inventory? Inventory { get; }
    Humanoid? Owner { get; }
    bool CanWrite { get; }
    string? Save();
}

internal sealed class NpcInventoryProvider : IInventoryProvider
{
    private readonly string _plugin;
    private readonly string _handle;

    internal NpcInventoryProvider(string plugin, string handle)
    {
        _plugin = plugin;
        _handle = handle;
    }

    private WorldObject? Object => WorldManager.Instance?.Resolve(_plugin, _handle);

    public bool Available => Object?.Character != null;

    public string Kind => "body";

    public Inventory? Inventory => (Object?.Character as Humanoid)?.GetInventory();

    public Humanoid? Owner => Object?.Character as Humanoid;

    public bool CanWrite => Available;

    public string? Save()
    {
        var owner = Owner;
        var zdo = WorldManager.Zdo(_handle);
        if (owner == null || zdo == null) return "body-inactive";
        VjsInventory.Save(owner, zdo);
        return null;
    }
}

internal sealed class ContainerInventoryProvider : IInventoryProvider
{
    private readonly string _handle;
    private ZDOID _id;

    internal ContainerInventoryProvider(string handle)
    {
        _handle = handle;
        WorldManager.TryParseHandle(handle, out _id);
    }

    private Container? Container
    {
        get
        {
            var instance = ZNetScene.instance?.FindInstance(_id);
            return instance != null ? instance.GetComponent<Container>() : null;
        }
    }

    public bool Available => Container != null;

    public string Kind => "container";

    public Inventory? Inventory => Container?.GetInventory();

    public Humanoid? Owner => null;

    public bool CanWrite => false;

    public string? Save() => "not-supported";
}

/// <summary>
/// Generic inventory accessor returned by <c>npc.inventory</c> and <c>npc.container</c>.
/// The same JavaScript surface backs an NPC body, a player body and a world container; the
/// provider decides which verbs are legal.
/// </summary>
[JsType("Inventory", Description = "A native inventory or container with query and mutation verbs.")]
public sealed class NpcInventoryAccessor
{
    private readonly Engine _engine;
    private readonly IInventoryProvider _provider;

    internal NpcInventoryAccessor(Engine engine, IInventoryProvider provider)
    {
        _engine = engine;
        _provider = provider;
    }

    public bool Available => _provider.Available;

    public string Kind => _provider.Kind;

    public bool Writable => _provider.CanWrite;

    public double Weight => _provider.Inventory?.GetTotalWeight() ?? 0d;

    public int Capacity
    {
        get
        {
            var inventory = _provider.Inventory;
            return inventory != null ? inventory.GetWidth() * inventory.GetHeight() : 0;
        }
    }

    public int FreeSlots
    {
        get
        {
            var inventory = _provider.Inventory;
            return inventory != null ? Math.Max(0, Capacity - inventory.NrOfItems()) : 0;
        }
    }

    [JsReturns("InventoryItem[]")]
    public JsValue Items
    {
        get
        {
            var snapshot = VjsInventory.Snapshot(_provider.Owner, _provider.Inventory);
            return JsPayload.FromPlain(_engine, snapshot["items"]);
        }
    }

    [JsReturns("NpcEquipment")]
    public JsValue Equipment
    {
        get
        {
            var snapshot = VjsInventory.Snapshot(_provider.Owner, _provider.Inventory);
            return JsPayload.FromPlain(_engine, snapshot["equipment"]);
        }
    }

    public bool Has(JsValue selector, double count = 1d) => Count(selector) >= Math.Max(1d, count);

    public int Count(JsValue selector)
    {
        var (prefab, name, _, _) = Parse(selector);
        return VjsInventory.Count(_provider.Inventory, prefab, name);
    }

    [JsReturns("ActionResult")]
    public JsValue Add(string prefab, double count = 1d, double quality = 0d)
    {
        if (!_provider.CanWrite) return Result(false, "not-supported");
        var inventory = _provider.Inventory;
        if (inventory == null) return Result(false, "no-inventory");
        var added = VjsInventory.Add(inventory, prefab, (int)Math.Round(count), (int)Math.Round(quality));
        var reason = _provider.Save();
        return Result(added > 0, added > 0 ? reason : "no-room", added);
    }

    [JsReturns("ActionResult")]
    public JsValue Remove(JsValue selector, double count = 1d)
    {
        if (!_provider.CanWrite) return Result(false, "not-supported");
        var inventory = _provider.Inventory;
        var item = Resolve(selector);
        if (inventory == null) return Result(false, "no-inventory");
        if (item == null) return Result(false, "no-item");
        var removed = VjsInventory.Remove(inventory, item, (int)Math.Round(count));
        var reason = _provider.Save();
        return Result(removed > 0, removed > 0 ? reason : "no-item", removed);
    }

    [JsReturns("ActionResult")]
    public JsValue Use(JsValue selector) => ConsumeOrEquip(selector, null);

    [JsReturns("ActionResult")]
    public JsValue Consume(JsValue selector) => ConsumeOrEquip(selector, "consume");

    [JsReturns("ActionResult")]
    public JsValue Equip(JsValue selector) => ConsumeOrEquip(selector, "equip");

    [JsReturns("ActionResult")]
    public JsValue Unequip(JsValue selector)
    {
        var owner = _provider.Owner;
        if (owner == null) return Result(false, "not-owner");
        var item = Resolve(selector);
        if (item == null) return Result(false, "no-item");
        var ok = VjsInventory.Unequip(owner, item);
        var reason = _provider.Save();
        return Result(ok, ok ? reason : "not-equipped");
    }

    [JsReturns("ActionResult")]
    public JsValue Drop(JsValue selector, double count = 1d)
    {
        var owner = _provider.Owner;
        if (owner == null) return Result(false, "not-owner");
        var item = Resolve(selector);
        if (item == null) return Result(false, "no-item");
        var ok = VjsInventory.Drop(owner, item, (int)Math.Round(count));
        var reason = _provider.Save();
        return Result(ok, ok ? reason : "no-item");
    }

    public bool CanUse(JsValue selector) => VjsInventory.CanUse(_provider.Owner, Resolve(selector));

    public bool CanConsume(JsValue selector)
    {
        var item = Resolve(selector);
        return item != null && VjsInventory.Kind(item) is "food" or "mead" &&
               VjsInventory.CanUse(_provider.Owner, item);
    }

    public bool CanEquip(JsValue selector)
    {
        var item = Resolve(selector);
        return item != null && VjsInventory.Kind(item) is "weapon" or "tool" or "armor" &&
               VjsInventory.CanUse(_provider.Owner, item);
    }

    private JsValue ConsumeOrEquip(JsValue selector, string? mode)
    {
        var owner = _provider.Owner;
        if (owner == null) return Result(false, "not-owner");
        var item = Resolve(selector);
        if (item == null) return Result(false, "no-item");
        var kind = VjsInventory.Kind(item);
        if (mode == "consume" && kind is not ("food" or "mead")) return Result(false, "not-food");
        if (mode == "equip" && kind is not ("weapon" or "tool" or "armor")) return Result(false, "not-equipment");
        var ok = VjsInventory.Use(owner, item);
        var reason = _provider.Save();
        return Result(ok, ok ? reason : mode == "equip" ? "not-equipped" : "cannot-use");
    }

    private ItemDrop.ItemData? Resolve(JsValue selector)
    {
        var (prefab, name, x, y) = Parse(selector);
        return VjsInventory.Resolve(_provider.Inventory, prefab, name, x, y);
    }

    private static (string? Prefab, string? Name, int? X, int? Y) Parse(JsValue value)
    {
        if (value.IsString())
        {
            return (value.AsString(), null, null, null);
        }

        if (value is ObjectInstance obj)
        {
            var prefabValue = obj.Get("prefab");
            var nameValue = obj.Get("name");
            string? prefab = prefabValue.IsString() ? prefabValue.AsString() : null;
            string? name = nameValue.IsString() ? nameValue.AsString() : null;
            int? x = null;
            int? y = null;
            if (obj.Get("slot") is ObjectInstance slot)
            {
                var slotX = slot.Get("x");
                var slotY = slot.Get("y");
                if (slotX.IsNumber()) x = (int)slotX.AsNumber();
                if (slotY.IsNumber()) y = (int)slotY.AsNumber();
            }

            return (prefab, name, x, y);
        }

        return (null, null, null, null);
    }

    private JsValue Result(bool ok, string? reason, int count = 0) =>
        JsPayload.FromPlain(_engine, new Dictionary<string, object?>
        {
            ["ok"] = ok,
            ["reason"] = reason,
            ["count"] = count
        });
}
