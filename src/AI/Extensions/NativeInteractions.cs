using System;
using UnityEngine;
using ValheimJS.World;

namespace ValheimJS.AI.Extensions;

/// <summary>
/// Server-side adapters for native interactables whose player-only callbacks cannot be used by a
/// scripted Humanoid. Adapters own the complete game-side interaction and surface results through
/// NpcAffordances / NpcInteractionResult or NPC board events.
/// </summary>
internal interface INpcInteractionExtension
{
    bool TryDescribe(GameObject instance, NpcAffordances result);

    NpcInteractionResult? Interact(
        WorldObject obj,
        GameObject instance,
        string verb,
        NpcInteractOptions options);
}

internal static class NativeInteractions
{
    private static readonly INpcInteractionExtension[] Extensions =
    {
        new CookingStationExtension(),
        new FireplaceExtension()
    };

    internal static bool TryDescribe(GameObject instance, NpcAffordances result)
    {
        foreach (var extension in Extensions)
        {
            if (extension.TryDescribe(instance, result))
            {
                return true;
            }
        }

        return false;
    }

    internal static NpcInteractionResult? TryInteract(
        WorldObject obj,
        GameObject instance,
        string verb,
        NpcInteractOptions options)
    {
        foreach (var extension in Extensions)
        {
            var result = extension.Interact(obj, instance, verb, options);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }
}
