using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimJS.World;

/// <summary>What one recognized target supports for a scripted body.</summary>
internal sealed class NpcAffordances
{
    internal string Id = "";
    internal string Kind = "";
    internal string Name = "";
    internal string? HoverName;
    internal string? HoverText;
    internal bool Supported;
    internal bool PlayerOnly;
    internal string? Reason;
    internal IReadOnlyList<string> Verbs = Array.Empty<string>();
    internal float Distance;
    internal bool InRange;

    internal Dictionary<string, object?> ToPlain() => new()
    {
        ["id"] = Id,
        ["kind"] = Kind,
        ["name"] = Name,
        ["hoverName"] = HoverName,
        ["hoverText"] = HoverText,
        ["supported"] = Supported,
        ["playerOnly"] = PlayerOnly,
        ["reason"] = Reason,
        ["verbs"] = new List<string>(Verbs),
        ["distance"] = Distance,
        ["inRange"] = InRange
    };
}

/// <summary>Outcome of one scripted interaction.</summary>
internal sealed class NpcInteractionResult
{
    internal bool Ok;
    internal string? Reason;
    internal string Verb = "";
    internal int Count;

    internal Dictionary<string, object?> ToPlain() => new()
    {
        ["ok"] = Ok,
        ["reason"] = Reason,
        ["verb"] = Verb,
        ["count"] = Count
    };

    internal static NpcInteractionResult Fail(string reason, string verb = "") => new()
    {
        Ok = false,
        Reason = reason,
        Verb = verb
    };
}

/// <summary>Options for <c>npc.interact</c>.</summary>
internal sealed class NpcInteractOptions
{
    internal string? Verb;
    internal string? Prefab;
    internal string? Name;
    internal int? X;
    internal int? Y;
    internal int Count = 1;
    internal bool Repeat;
    internal bool Alt;
}

/// <summary>
/// Server-side interaction executor. Native <see cref="Interactable"/> calls are used only where a
/// plain Humanoid is valid; player/UI-only components get direct adapters or an explicit refusal.
/// </summary>
internal static class NpcInteraction
{
    private const float Range = 3.5f;

    internal static NpcAffordances Inspect(WorldObject obj, string handle)
    {
        var result = new NpcAffordances { Id = handle };
        if (!WorldManager.TryParseHandle(handle, out var id))
        {
            result.Reason = "invalid-target";
            return result;
        }

        var instance = ZNetScene.instance?.FindInstance(id);
        if (instance == null)
        {
            result.Reason = "not-loaded";
            return result;
        }

        result.Name = instance.name;
        result.Distance = Vector3.Distance(obj.Position, instance.transform.position);
        result.InRange = result.Distance <= Range;

        var hoverable = Find<Hoverable>(instance);
        if (hoverable != null)
        {
            result.HoverName = hoverable.GetHoverName();
            result.HoverText = hoverable.GetHoverText();
        }

        var pickable = Find<Pickable>(instance);
        var container = Find<Container>(instance);
        var smelter = Find<Smelter>(instance);
        var bed = Find<Bed>(instance);
        var station = Find<CraftingStation>(instance);
        var cooking = Find<CookingStation>(instance);
        var portal = Find<TeleportWorld>(instance);
        var trader = Find<Trader>(instance);
        var door = Find<Door>(instance);
        var interactable = Find<Interactable>(instance);

        if (pickable != null)
        {
            result.Kind = "pickable";
            result.Verbs = new[] { "pick" };
            result.Supported = true;
        }
        else if (container != null)
        {
            result.Kind = "container";
            result.Verbs = new[] { "store", "take" };
            result.Supported = true;
        }
        else if (smelter != null)
        {
            result.Kind = "smelter";
            result.Verbs = new[] { "addOre", "addFuel" };
            result.Supported = true;
        }
        else if (door != null)
        {
            result.Kind = "door";
            result.Verbs = new[] { "open", "close", "toggle" };
            result.Supported = interactable != null;
        }
        else if (bed != null)
        {
            result.Kind = "bed";
            result.Verbs = new[] { "sleep" };
            result.PlayerOnly = true;
            result.Reason = "player-only";
        }
        else if (station != null)
        {
            result.Kind = "station";
            result.Verbs = new[] { "craft", "repair" };
            result.PlayerOnly = true;
            result.Reason = "player-only";
        }
        else if (cooking != null)
        {
            result.Kind = "cooking";
            result.Verbs = new[] { "cook" };
            result.PlayerOnly = true;
            result.Reason = "player-only";
        }
        else if (portal != null)
        {
            result.Kind = "portal";
            result.Verbs = new[] { "use" };
            result.PlayerOnly = true;
            result.Reason = "player-only";
        }
        else if (trader != null)
        {
            result.Kind = "trader";
            result.Verbs = new[] { "trade" };
            result.PlayerOnly = true;
            result.Reason = "player-only";
        }
        else if (interactable != null)
        {
            result.Kind = KindOf(instance);
            result.Verbs = new[] { "use" };
            result.Supported = true;
        }
        else
        {
            result.Reason = "not-interactable";
        }

        return result;
    }

    internal static NpcInteractionResult Interact(WorldObject obj, string handle, NpcInteractOptions options)
    {
        var information = Inspect(obj, handle);
        if (!information.Supported)
        {
            return NpcInteractionResult.Fail(information.Reason ?? "unsupported");
        }

        if (!information.InRange)
        {
            return NpcInteractionResult.Fail("out-of-range");
        }

        if (!WorldManager.TryParseHandle(handle, out var id))
        {
            return NpcInteractionResult.Fail("invalid-target");
        }

        var instance = ZNetScene.instance?.FindInstance(id);
        var humanoid = obj.Character as Humanoid;
        if (instance == null || humanoid == null)
        {
            return NpcInteractionResult.Fail("not-loaded");
        }

        var verb = options.Verb ?? DefaultVerb(information.Verbs);
        if (verb == null)
        {
            return NpcInteractionResult.Fail("verb-required");
        }

        var view = instance.GetComponent<ZNetView>();
        if (view != null && !view.IsOwner())
        {
            view.ClaimOwnership();
        }

        switch (verb)
        {
            case "pick":
                return VjsActions.Pick(obj, handle, out var reason)
                    ? new NpcInteractionResult { Ok = true, Verb = verb, Count = 1 }
                    : NpcInteractionResult.Fail(reason ?? "rejected", verb);

            case "store":
            case "take":
                return Transfer(instance, humanoid, verb == "store", options, verb);

            case "addOre":
                if (view == null || string.IsNullOrWhiteSpace(options.Prefab))
                {
                    return NpcInteractionResult.Fail("invalid-item", verb);
                }

                view.InvokeRPC("RPC_AddOre", options.Prefab, false);
                return new NpcInteractionResult { Ok = true, Verb = verb, Count = 1 };

            case "addFuel":
                if (view == null)
                {
                    return NpcInteractionResult.Fail("not-loaded", verb);
                }

                view.InvokeRPC("RPC_AddFuel");
                return new NpcInteractionResult { Ok = true, Verb = verb, Count = 1 };

            default:
            {
                var interactable = Find<Interactable>(instance);
                if (interactable == null)
                {
                    return NpcInteractionResult.Fail("not-interactable", verb);
                }

                if (!PrivateArea.CheckAccess(instance.transform.position))
                {
                    return NpcInteractionResult.Fail("no-access", verb);
                }

                return interactable.Interact(humanoid, options.Repeat, options.Alt)
                    ? new NpcInteractionResult { Ok = true, Verb = verb, Count = 1 }
                    : NpcInteractionResult.Fail("rejected", verb);
            }
        }
    }

    private static NpcInteractionResult Transfer(
        GameObject instance,
        Humanoid humanoid,
        bool store,
        NpcInteractOptions options,
        string verb)
    {
        var container = Find<Container>(instance);
        var npcInventory = humanoid.GetInventory();
        var containerInventory = container?.GetInventory();
        if (npcInventory == null || containerInventory == null)
        {
            return NpcInteractionResult.Fail("not-loaded", verb);
        }

        if (!PrivateArea.CheckAccess(instance.transform.position))
        {
            return NpcInteractionResult.Fail("no-access", verb);
        }

        var from = store ? npcInventory : containerInventory;
        var to = store ? containerInventory : npcInventory;
        var item = VjsInventory.Resolve(from, options.Prefab, options.Name, options.X, options.Y);
        if (item == null)
        {
            return NpcInteractionResult.Fail("no-item", verb);
        }

        var count = Mathf.Clamp(options.Count, 1, item.m_stack);
        var data = item.Clone();
        data.m_stack = count;
        if (!from.RemoveItem(item, count))
        {
            return NpcInteractionResult.Fail("no-item", verb);
        }

        if (!to.AddItem(data))
        {
            from.AddItem(data);
            return NpcInteractionResult.Fail("no-room", verb);
        }

        return new NpcInteractionResult { Ok = true, Verb = verb, Count = count };
    }

    private static string? DefaultVerb(IReadOnlyList<string> verbs)
    {
        if (Contains(verbs, "pick")) return "pick";
        if (Contains(verbs, "use")) return "use";
        if (Contains(verbs, "toggle")) return "toggle";
        if (Contains(verbs, "open")) return "open";
        return null;
    }

    private static bool Contains(IReadOnlyList<string> verbs, string value)
    {
        foreach (var verb in verbs)
        {
            if (string.Equals(verb, value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string KindOf(GameObject instance) => instance.GetComponent<Door>() != null
        ? "door"
        : instance.GetComponent<Fireplace>() != null
            ? "fireplace"
            : instance.GetComponent<Beehive>() != null
                ? "beehive"
                : instance.GetComponent<Vagon>() != null
                    ? "vagon"
                    : instance.GetComponent<ItemStand>() != null
                        ? "itemstand"
                        : instance.GetComponent<ArmorStand>() != null
                            ? "armorstand"
                            : instance.GetComponent<Chair>() != null
                                ? "chair"
                                : "interactable";

    private static T? Find<T>(GameObject instance) where T : class
    {
        foreach (var behaviour in instance.GetComponentsInParent<MonoBehaviour>(true))
        {
            if (behaviour is T match)
            {
                return match;
            }
        }

        return null;
    }
}
