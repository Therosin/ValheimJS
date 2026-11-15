using System.Collections.Generic;
using UnityEngine;

namespace ValheimJS.World;

/// <summary>
/// Shares sector scans between NPCs that refresh in the same frame. Per-NPC scans dominated the
/// cost of perception; the objects in a sector only change on network updates, so one snapshot
/// per zone per frame is enough.
/// </summary>
internal static class NpcSectorScan
{
    private static readonly Dictionary<(int X, int Y), List<ZDO>> Cache = new();
    private static int _frame = -1;

    /// <summary>Returns the ZDOs in the NPC's sector, sharing the result with other NPCs this frame.</summary>
    internal static List<ZDO> Take(Vector3 position)
    {
        var manager = ZDOMan.instance;
        if (manager == null)
        {
            return new List<ZDO>();
        }

        if (Time.frameCount != _frame)
        {
            _frame = Time.frameCount;
            Cache.Clear();
        }

        var zone = ZoneSystem.GetZone(position);
        if (Cache.TryGetValue((zone.x, zone.y), out var cached))
        {
            return cached;
        }

        var objects = new List<ZDO>();
        manager.FindSectorObjects(zone, new SimulationDistance(1, 0, false), objects);
        Cache[(zone.x, zone.y)] = objects;
        return objects;
    }
}
