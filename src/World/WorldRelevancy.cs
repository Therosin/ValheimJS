using System;
using System.Collections.Generic;

namespace ValheimJS.World;

/// <summary>Pure relevance rules, shared by the server tick and the headless probe.</summary>
internal static class WorldRelevancy
{
    internal const float ExitFactor = 1.25f;

    internal static bool IsRelevant(float ax, float az, float px, float pz, float radius, bool wasRelevant)
    {
        var dx = ax - px;
        var dz = az - pz;
        var distanceSquared = dx * dx + dz * dz;
        var limit = wasRelevant ? radius * ExitFactor : radius;
        return distanceSquared <= limit * limit;
    }

    /// <summary>
    /// Computes the next subscription set and the exact add/remove diff. Inputs are the current
    /// objects (handle + world position) and the peer's position; <paramref name="previous"/> is
    /// the set used for hysteresis and is not mutated.
    /// </summary>
    internal static void Diff(
        IReadOnlyList<(string Handle, float X, float Z)> objects,
        HashSet<string> previous,
        float peerX,
        float peerZ,
        float radius,
        HashSet<string> next,
        List<string> add,
        List<string> remove)
    {
        next.Clear();
        add.Clear();
        remove.Clear();

        foreach (var item in objects)
        {
            if (IsRelevant(item.X, item.Z, peerX, peerZ, radius, previous.Contains(item.Handle)))
            {
                next.Add(item.Handle);
                if (!previous.Contains(item.Handle))
                {
                    add.Add(item.Handle);
                }
            }
        }

        foreach (var handle in previous)
        {
            if (!next.Contains(handle))
            {
                remove.Add(handle);
            }
        }
    }
}
