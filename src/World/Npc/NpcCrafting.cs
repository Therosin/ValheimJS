using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimJS.World;

/// <summary>One reason a recipe cannot be crafted right now.</summary>
internal sealed class CraftReason
{
    internal string Code = "";
    internal string? Item;
    internal int Need;
    internal int Have;
    internal string? Station;
    internal int Level;
    internal string? Condition;

    internal Dictionary<string, object?> ToPlain()
    {
        var plain = new Dictionary<string, object?> { ["code"] = Code };
        if (Item != null) plain["item"] = Item;
        if (Need > 0) plain["need"] = Need;
        if (Have > 0) plain["have"] = Have;
        if (Station != null) plain["station"] = Station;
        if (Level > 0) plain["level"] = Level;
        if (Condition != null) plain["condition"] = Condition;
        return plain;
    }
}

internal sealed class CraftCheck
{
    internal bool Ok;
    internal readonly List<CraftReason> Reasons = new();
    internal Recipe? Recipe;

    internal Dictionary<string, object?> ToPlain() => new()
    {
        ["ok"] = Ok,
        ["reasons"] = Reasons.ConvertAll(reason => (object?)reason.ToPlain())
    };

    internal CraftCheck Fail(CraftReason reason)
    {
        Reasons.Add(reason);
        Ok = false;
        return this;
    }

    internal void Result()
    {
        Ok = Reasons.Count == 0;
    }
}

internal sealed class CraftResult
{
    internal bool Ok;
    internal string? Reason;
    internal int Count;
    internal List<CraftReason> Reasons = new();

    internal Dictionary<string, object?> ToPlain() => new()
    {
        ["ok"] = Ok,
        ["reason"] = Reason,
        ["count"] = Count,
        ["reasons"] = Reasons.ConvertAll(reason => (object?)reason.ToPlain())
    };
}

/// <summary>
/// Standard recipe crafting for NPC bodies: native ObjectDB recipes, station/level checks,
/// ingredients, and output into the inventory or on the ground when it does not fit.
/// Cooking (CookingStation), smelting and building are separate systems.
/// </summary>
internal static class NpcCrafting
{
    private static readonly int StationMask = LayerMask.GetMask("piece", "Default", "static_solid");
    private static readonly Collider[] StationHits = new Collider[16];

    internal static CraftCheck Check(WorldObject obj, string recipeName)
    {
        var check = new CraftCheck();
        var humanoid = obj.Character as Humanoid;
        if (humanoid == null)
        {
            return check.Fail(new CraftReason { Code = "body-lost" });
        }

        var inventory = humanoid.GetInventory();
        if (inventory == null)
        {
            return check.Fail(new CraftReason { Code = "no-inventory" });
        }

        var recipe = FindRecipe(recipeName);
        if (recipe == null)
        {
            return check.Fail(new CraftReason { Code = "recipe-missing" });
        }

        check.Recipe = recipe;
        if (!recipe.m_enabled)
        {
            check.Fail(new CraftReason { Code = "recipe-disabled" });
        }

        if (recipe.m_noCraftOnlyUpgrade)
        {
            check.Fail(new CraftReason { Code = "upgrade-only" });
        }

        if (recipe.m_requireOnlyOneIngredient)
        {
            check.Fail(new CraftReason { Code = "unsupported" });
        }

        if (recipe.m_item == null)
        {
            check.Fail(new CraftReason { Code = "invalid-recipe" });
        }

        if (recipe.m_craftingStation != null)
        {
            var station = FindStation(recipe.m_craftingStation, humanoid, out var level);
            if (station == null)
            {
                check.Fail(new CraftReason
                {
                    Code = "station-missing",
                    Station = PrefabName(recipe.m_craftingStation.gameObject)
                });
            }
            else if (level < recipe.m_minStationLevel)
            {
                check.Fail(new CraftReason
                {
                    Code = "station-level",
                    Station = PrefabName(recipe.m_craftingStation.gameObject),
                    Level = recipe.m_minStationLevel,
                    Need = recipe.m_minStationLevel,
                    Have = level
                });
            }
        }

        foreach (var requirement in recipe.m_resources)
        {
            if (requirement.m_resItem == null)
            {
                continue;
            }

            var name = requirement.m_resItem.m_itemData.m_shared.m_name;
            var amount = Mathf.Max(1, requirement.m_amount);
            var have = inventory.CountItems(name);
            if (have < amount)
            {
                check.Fail(new CraftReason
                {
                    Code = "missing-ingredient",
                    Item = name,
                    Need = amount,
                    Have = have
                });
            }
        }

        check.Result();
        return check;
    }

    internal static CraftResult Craft(WorldObject obj, string recipeName)
    {
        var check = Check(obj, recipeName);
        if (!check.Ok || check.Recipe == null)
        {
            return new CraftResult
            {
                Ok = false,
                Reason = check.Reasons.Count > 0 ? check.Reasons[0].Code : "invalid",
                Reasons = check.Reasons
            };
        }

        var recipe = check.Recipe;
        var humanoid = obj.Character as Humanoid;
        var inventory = humanoid?.GetInventory();
        if (humanoid == null || inventory == null)
        {
            return new CraftResult { Ok = false, Reason = "body-lost" };
        }

        var consumed = new List<(Piece.Requirement Requirement, int Amount)>();
        foreach (var requirement in recipe.m_resources)
        {
            if (requirement.m_resItem == null)
            {
                continue;
            }

            var name = requirement.m_resItem.m_itemData.m_shared.m_name;
            var need = Mathf.Max(1, requirement.m_amount);
            if (!Consume(inventory, name, need))
            {
                Refund(inventory, humanoid, consumed);
                return new CraftResult { Ok = false, Reason = "missing-ingredient" };
            }

            consumed.Add((requirement, need));
        }

        var amount = Mathf.Max(1, recipe.m_amount);
        var data = recipe.m_item.m_itemData.Clone();
        data.m_dropPrefab = recipe.m_item.gameObject;
        data.m_stack = amount;
        data.m_quality = 1;
        data.m_worldLevel = (byte)Game.m_worldLevel;
        if (data.m_shared.m_maxDurability > 0f)
        {
            data.m_durability = data.m_shared.m_maxDurability;
        }

        var nameOfOutput = data.m_shared.m_name;
        var before = inventory.CountItems(nameOfOutput);
        inventory.AddItem(data);
        var added = Mathf.Clamp(inventory.CountItems(nameOfOutput) - before, 0, amount);
        var missing = amount - added;
        if (missing > 0)
        {
            var drop = ItemDrop.DropItem(data, missing, humanoid.transform.position + Vector3.up * 0.5f, Quaternion.identity);
            var view = drop != null ? drop.GetComponent<ZNetView>() : null;
            if (drop == null || view == null || view.GetZDO() == null)
            {
                if (added == 0)
                {
                    Refund(inventory, humanoid, consumed);
                }

                return new CraftResult
                {
                    Ok = false,
                    Reason = added == 0 ? "output-failed" : "output-partial",
                    Count = added
                };
            }
        }

        return new CraftResult { Ok = true, Count = amount };
    }

    private static Recipe? FindRecipe(string name)
    {
        var database = ObjectDB.instance;
        if (database == null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var recipe in database.m_recipes)
        {
            if (recipe != null && string.Equals(recipe.name, name, StringComparison.OrdinalIgnoreCase))
            {
                return recipe;
            }
        }

        return null;
    }

    private static CraftingStation? FindStation(CraftingStation required, Humanoid humanoid, out int level)
    {
        level = 0;
        var count = Physics.OverlapSphereNonAlloc(humanoid.transform.position, 5f, StationHits, StationMask);
        var name = PrefabName(required.gameObject);
        CraftingStation? best = null;
        for (var index = 0; index < count; index++)
        {
            var station = StationHits[index]?.GetComponentInParent<CraftingStation>();
            if (station == null || !string.Equals(PrefabName(station.gameObject), name, StringComparison.Ordinal))
            {
                continue;
            }

            if (Vector3.Distance(humanoid.transform.position, station.transform.position) > station.m_useDistance + 1f)
            {
                continue;
            }

            var stationLevel = station.GetLevel();
            if (level < stationLevel)
            {
                level = stationLevel;
                best = station;
            }
        }

        return best;
    }

    private static string PrefabName(GameObject instance)
    {
        var name = instance.name;
        var clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
        return clone >= 0 ? name.Substring(0, clone) : name;
    }

    private static bool Consume(Inventory inventory, string name, int amount)
    {
        var remaining = amount;
        foreach (var item in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
        {
            if (!string.Equals(item.m_shared.m_name, name, StringComparison.Ordinal))
            {
                continue;
            }

            var take = Mathf.Min(remaining, item.m_stack);
            if (!inventory.RemoveItem(item, take))
            {
                return false;
            }

            remaining -= take;
            if (remaining <= 0)
            {
                return true;
            }
        }

        return remaining <= 0;
    }

    private static void Refund(Inventory inventory, Humanoid humanoid, List<(Piece.Requirement Requirement, int Amount)> consumed)
    {
        foreach (var (requirement, amount) in consumed)
        {
            if (requirement.m_resItem == null)
            {
                continue;
            }

            var data = requirement.m_resItem.m_itemData.Clone();
            data.m_dropPrefab = requirement.m_resItem.gameObject;
            data.m_stack = Mathf.Max(1, amount);
            if (data.m_stack > 1 && data.m_shared.m_maxStackSize > 1)
            {
                data.m_stack = Mathf.Min(data.m_stack, data.m_shared.m_maxStackSize);
            }

            if (!inventory.AddItem(data))
            {
                ItemDrop.DropItem(data, data.m_stack, humanoid.transform.position + Vector3.up * 0.5f, Quaternion.identity);
            }
        }
    }
}
