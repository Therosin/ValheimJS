using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimJS.World.Navigation;
using NumericVector3 = System.Numerics.Vector3;

namespace ValheimJS.World;

/// <summary>Terrain heights from the procedural world generator (available headless).</summary>
internal sealed class WorldGeneratorTerrain : ITerrainSource
{
    public bool TryHeight(float x, float z, out float y)
    {
        var generator = WorldGenerator.instance;
        if (generator == null)
        {
            y = 0f;
            return false;
        }

        y = generator.GetHeight(x, z);
        return true;
    }
}

/// <summary>
/// Stamps structural ZDOs and obstacle footprints into a nav zone. Trees block their cell, rocks
/// and pieces block by mesh footprint, stairs become ramps and floors become indoor surfaces.
/// Prefab names are Vanilla's canonical names (clone suffixes are stripped by Utils.PrefabName).
/// </summary>
internal sealed class ZdoObstacleSource : IObstacleSource
{
    private static readonly string[] Trees =
    {
        "Beech", "Birch", "Oak", "Pine", "Fir", "Ancient", "SwampTree", "Yggdrasil"
    };

    public void Stamp(NavZone zone)
    {
        var zdoManager = ZDOMan.instance;
        var scene = ZNetScene.instance;
        if (zdoManager == null || scene == null)
        {
            return;
        }

        var sector = ZoneSystem.GetZone(new Vector3(zone.CellWorldX(0), 0f, zone.CellWorldZ(0)));
        var objects = new List<ZDO>();
        zdoManager.FindSectorObjects(sector, new SimulationDistance(0, 0, false), objects);
        foreach (var zdo in objects)
        {
            var prefab = scene.GetPrefab(zdo.GetPrefab());
            if (prefab == null)
            {
                continue;
            }

            var name = prefab.name;
            var position = zdo.GetPosition();
            var rotation = zdo.GetRotation();
            var scale = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.one);
            if (scale == Vector3.zero)
            {
                scale = Vector3.one;
            }

            var bounds = BoundsOf(prefab, scale);
            var radius = Mathf.Clamp(Mathf.Max(bounds.extents.x, bounds.extents.z), 0.5f, 4f);

            if (IsTree(name))
            {
                zone.BlockAtWorld(position.x, position.z, 0.75f);
                continue;
            }

            if (IsRock(name))
            {
                zone.BlockAtWorld(position.x, position.z, Mathf.Clamp(radius, 0.5f, 4f));
                continue;
            }

            if (IsIgnored(name))
            {
                continue;
            }

            if (name.IndexOf("stair", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                StampStairs(zone, position, rotation, bounds, scale);
                continue;
            }

            if (name.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0 || bounds.size.y <= 0.3f)
            {
                var y = position.y + bounds.center.y * scale.y + bounds.extents.y * scale.y;
                zone.ClearBlockedAtWorld(position.x, position.z, Mathf.Max(0.5f, radius));
                zone.AddSurfaceAtWorld(position.x, position.z, y, NavFlags.Indoor);
                continue;
            }

            if (prefab.GetComponent<WearNTear>() != null || prefab.GetComponent<Piece>() != null)
            {
                zone.BlockAtWorld(position.x, position.z, Mathf.Clamp(radius, 0.5f, 2f));
            }
        }
    }

    private static void StampStairs(NavZone zone, Vector3 position, Quaternion rotation, Bounds bounds, Vector3 scale)
    {
        var length = Mathf.Max(2f, bounds.size.z * Mathf.Abs(scale.z));
        var height = Mathf.Max(0.5f, bounds.size.y * Mathf.Abs(scale.y));
        var baseY = position.y + (bounds.center.y - bounds.extents.y) * scale.y;
        var forward = rotation * Vector3.forward;
        var start = position - forward * (length * 0.5f);
        for (var sample = 0; sample <= 4; sample++)
        {
            var t = sample / 4f;
            var point = start + forward * (length * t);
            zone.ClearBlockedAtWorld(point.x, point.z, 0.5f);
            zone.AddSurfaceAtWorld(point.x, point.z, baseY + height * t, NavFlags.Stair);
        }
    }

    private static Bounds BoundsOf(GameObject prefab, Vector3 scale)
    {
        var filter = prefab.GetComponentInChildren<MeshFilter>();
        var mesh = filter != null ? filter.sharedMesh : null;
        if (mesh == null)
        {
            return new Bounds(Vector3.zero, new Vector3(1f * Mathf.Abs(scale.x), 1f * Mathf.Abs(scale.y), 1f * Mathf.Abs(scale.z)));
        }

        var bounds = mesh.bounds;
        return new Bounds(
            new Vector3(bounds.center.x * scale.x, bounds.center.y * scale.y, bounds.center.z * scale.z),
            new Vector3(bounds.size.x * Mathf.Abs(scale.x), bounds.size.y * Mathf.Abs(scale.y), bounds.size.z * Mathf.Abs(scale.z)));
    }

    private static bool IsTree(string name)
    {
        foreach (var prefix in Trees)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRock(string name) =>
        name.StartsWith("Rock", StringComparison.OrdinalIgnoreCase) ||
        name.IndexOf("stone", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsIgnored(string name) =>
        name.StartsWith("Bush", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Pickable", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("sapling", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Vine", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Fern", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Unity-facing adapter for <see cref="NavVolume"/>; scripts and AI use metres.</summary>
internal sealed class NavGridNavigation
{
    internal NavVolume Volume { get; }

    internal NavGridNavigation(NavVolume volume)
    {
        Volume = volume;
    }

    internal bool BuildPendingZone() => Volume.BuildPendingZone();

    internal bool FindPath(Vector3 from, Vector3 to, List<Vector3> points, out bool pending)
    {
        var raw = new List<NumericVector3>();
        var found = Volume.FindPath(ToNumeric(from), ToNumeric(to), raw, out pending);
        points.Clear();
        if (!found)
        {
            return false;
        }

        foreach (var point in raw)
        {
            points.Add(new Vector3(point.X, point.Y, point.Z));
        }

        return true;
    }

    internal bool TryRandomPoint(Vector3 center, float radius, out Vector3 point)
    {
        if (Volume.TryRandomPoint(ToNumeric(center), radius, out var numeric))
        {
            point = new Vector3(numeric.X, numeric.Y, numeric.Z);
            return true;
        }

        point = default;
        return false;
    }

    private static NumericVector3 ToNumeric(Vector3 value) => new(value.x, value.y, value.z);
}
