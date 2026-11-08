using System;
using System.Collections.Generic;
using Vector3 = System.Numerics.Vector3;

namespace ValheimJS.World.Navigation;

/// <summary>
/// Lazily built navigation volume. Coordinates are metres; cells are 1x1 m and zones 64x64 m,
/// matching Valheim's zone grid. Terrain zones are disk-cached; obstacle/structure surfaces are
/// stamped on every build.
/// </summary>
internal sealed class NavVolume
{
    private const int MaxExpansions = 20_000;
    private const float DefaultTolerance = 0.55f;

    private readonly ITerrainSource _terrain;
    private readonly IObstacleSource? _obstacles;
    private readonly string _cacheRoot;
    private readonly long _worldUid;
    private readonly float _waterLevel;
    private readonly Dictionary<long, NavZone> _zones = new();
    private readonly HashSet<long> _pending = new();
    private readonly Queue<long> _buildQueue = new();
    private readonly Random _random = new(1337);

    public NavVolume(ITerrainSource terrain, IObstacleSource? obstacles, string cacheRoot, long worldUid, float waterLevel)
    {
        _terrain = terrain;
        _obstacles = obstacles;
        _cacheRoot = cacheRoot;
        _worldUid = worldUid;
        _waterLevel = waterLevel;
    }

    public long WorldUid => _worldUid;

    public int ZoneCount => _zones.Count;

    public int CellCount => _zones.Count * NavZone.CellsPerSide * NavZone.CellsPerSide;

    public IEnumerable<NavZone> Zones => _zones.Values;

    public bool BuildPendingZone()
    {
        if (_buildQueue.Count == 0)
        {
            return false;
        }

        var key = _buildQueue.Dequeue();
        _pending.Remove(key);
        if (_zones.ContainsKey(key))
        {
            return true;
        }

        var zx = (int)(key >> 32);
        var zy = (int)(key & 0xFFFFFFFFL);
        _zones[key] = BuildZone(zx, zy);
        return true;
    }

    public bool TryGetZone(int zx, int zy, out NavZone zone)
    {
        var key = ZoneKey(zx, zy);
        if (_zones.TryGetValue(key, out var existing))
        {
            zone = existing;
            return true;
        }

        if (NavCache.TryLoad(_cacheRoot, _worldUid, zx, zy, out var cached))
        {
            _obstacles?.Stamp(cached);
            _zones[key] = cached;
            zone = cached;
            return true;
        }

        if (_pending.Add(key))
        {
            _buildQueue.Enqueue(key);
        }

        zone = null!;
        return false;
    }

    public bool FindPath(Vector3 from, Vector3 to, List<Vector3> points, out bool pending)
    {
        points.Clear();
        pending = false;

        var startGlobalX = GlobalCell(from.X);
        var startGlobalZ = GlobalCell(from.Z);
        var goalGlobalX = GlobalCell(to.X);
        var goalGlobalZ = GlobalCell(to.Z);
        if (!TryGetZone(FloorDiv(startGlobalX, NavZone.CellsPerSide), FloorDiv(startGlobalZ, NavZone.CellsPerSide), out _) ||
            !TryGetZone(FloorDiv(goalGlobalX, NavZone.CellsPerSide), FloorDiv(goalGlobalZ, NavZone.CellsPerSide), out _))
        {
            pending = true;
            return false;
        }

        if (!TrySurface(startGlobalX, startGlobalZ, from.Y, 2.5f, out var start) ||
            !TrySurface(goalGlobalX, goalGlobalZ, to.Y, 2.5f, out var goal))
        {
            return false;
        }

        if (start.Equals(goal))
        {
            points.Add(to);
            return true;
        }

        var open = new SortedSet<QueueItem>();
        var gScore = new Dictionary<Node, float>();
        var cameFrom = new Dictionary<Node, Node>();
        var closed = new HashSet<Node>();
        var sequence = 0L;

        gScore[start] = 0f;
        open.Add(new QueueItem { F = Heuristic(start, goal), Gx = start.Gx, Gz = start.Gz, Si = start.Si, Seq = sequence++ });

        while (open.Count > 0)
        {
            if (gScore.Count > MaxExpansions)
            {
                return false;
            }

            var item = open.Min!;
            open.Remove(item);
            var current = new Node(item.Gx, item.Gz, item.Si);
            if (!closed.Add(current))
            {
                continue;
            }

            if (current.Equals(goal))
            {
                Reconstruct(cameFrom, start, goal, from, to, points);
                return true;
            }

            if (!TryGetNodeSurface(current, out var zone, out var cell, out var surface))
            {
                continue;
            }

            var baseCost = gScore[current];

            // Vertical transitions inside one cell (stacked floors/stairs).
            for (var other = 0; other < cell.Surfaces.Count; other++)
            {
                if (other == current.Si)
                {
                    continue;
                }

                var neighbour = new Node(current.Gx, current.Gz, other);
                var dy = Math.Abs(cell.Surfaces[other].Y - surface.Y);
                var limit = (surface.Flags | cell.Surfaces[other].Flags).HasFlag(NavFlags.Stair) ? 0.8f : 0.55f;
                if (dy > limit)
                {
                    continue;
                }

                Relax(neighbour, baseCost + 0.5f + dy * 3f);
            }

            for (var direction = 0; direction < Directions.Length; direction++)
            {
                var (dx, dz) = Directions[direction];
                var nextGlobalX = current.Gx + dx;
                var nextGlobalZ = current.Gz + dz;
                var nextZoneX = FloorDiv(nextGlobalX, NavZone.CellsPerSide);
                var nextZoneZ = FloorDiv(nextGlobalZ, NavZone.CellsPerSide);
                if (!TryGetZone(nextZoneX, nextZoneZ, out var nextZone))
                {
                    pending = true;
                    continue;
                }

                if (!nextZone.TryGetGlobalCell(nextGlobalX, nextGlobalZ, out var nextCell, out _, out _) || nextCell.Blocked)
                {
                    continue;
                }

                if (dx != 0 && dz != 0 &&
                    (!HasSurfaceNear(nextGlobalX - dx, nextGlobalZ, surface.Y, 0.8f) ||
                     !HasSurfaceNear(nextGlobalX, nextGlobalZ - dz, surface.Y, 0.8f)))
                {
                    continue;
                }

                for (var other = 0; other < nextCell.Surfaces.Count; other++)
                {
                    var target = nextCell.Surfaces[other];
                    var dy = Math.Abs(target.Y - surface.Y);
                    var limit = (surface.Flags | target.Flags).HasFlag(NavFlags.Stair) ? 0.8f : 0.55f;
                    if (dy > limit)
                    {
                        continue;
                    }

                    var step = dx != 0 && dz != 0 ? 1.4142f : 1f;
                    Relax(new Node(nextGlobalX, nextGlobalZ, other), baseCost + step + dy * 3f);
                }
            }

            void Relax(Node next, float score)
            {
                if (closed.Contains(next))
                {
                    return;
                }

                if (gScore.TryGetValue(next, out var existing) && existing <= score)
                {
                    return;
                }

                gScore[next] = score;
                cameFrom[next] = current;
                open.Add(new QueueItem
                {
                    F = score + Heuristic(next, goal),
                    Gx = next.Gx,
                    Gz = next.Gz,
                    Si = next.Si,
                    Seq = sequence++
                });
            }
        }

        return false;
    }

    public bool TryRandomPoint(Vector3 center, float radius, out Vector3 point)
    {
        point = default;
        if (!TryGetZone(FloorDiv(GlobalCell(center.X), NavZone.CellsPerSide), FloorDiv(GlobalCell(center.Z), NavZone.CellsPerSide), out _))
        {
            return false;
        }

        for (var attempt = 0; attempt < 32; attempt++)
        {
            var angle = _random.NextDouble() * Math.PI * 2.0;
            var distance = Math.Sqrt(_random.NextDouble()) * radius;
            var x = center.X + (float)(Math.Cos(angle) * distance);
            var z = center.Z + (float)(Math.Sin(angle) * distance);
            var globalX = GlobalCell(x);
            var globalZ = GlobalCell(z);
            var zoneX = FloorDiv(globalX, NavZone.CellsPerSide);
            var zoneZ = FloorDiv(globalZ, NavZone.CellsPerSide);
            if (!_zones.TryGetValue(ZoneKey(zoneX, zoneZ), out var zone) ||
                !zone.TryGetGlobalCell(globalX, globalZ, out var cell, out _, out _) ||
                cell.Blocked ||
                cell.Surfaces.Count == 0)
            {
                continue;
            }

            var surface = cell.Surfaces[0];
            for (var index = 1; index < cell.Surfaces.Count; index++)
            {
                if ((cell.Surfaces[index].Flags & NavFlags.Walk) != 0)
                {
                    surface = cell.Surfaces[index];
                    break;
                }
            }

            point = new Vector3(x, surface.Y, z);
            return true;
        }

        return false;
    }

    public bool CanWalkLine(Vector3 from, Vector3 to, float tolerance = DefaultTolerance)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var distance = (float)Math.Sqrt(dx * dx + dz * dz);
        var steps = Math.Max(1, (int)Math.Ceiling(distance / 0.5f));
        for (var step = 0; step <= steps; step++)
        {
            var t = (float)step / steps;
            var x = from.X + dx * t;
            var z = from.Z + dz * t;
            var y = from.Y + (to.Y - from.Y) * t;
            var globalX = GlobalCell(x);
            var globalZ = GlobalCell(z);
            var zoneX = FloorDiv(globalX, NavZone.CellsPerSide);
            var zoneZ = FloorDiv(globalZ, NavZone.CellsPerSide);
            if (!_zones.TryGetValue(ZoneKey(zoneX, zoneZ), out var zone) ||
                !zone.TryGetGlobalCell(globalX, globalZ, out var cell, out _, out _) ||
                cell.Blocked)
            {
                return false;
            }

            var found = false;
            foreach (var surface in cell.Surfaces)
            {
                if (Math.Abs(surface.Y - y) <= tolerance)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private NavZone BuildZone(int zx, int zy)
    {
        NavZone zone;
        var loaded = NavCache.TryLoad(_cacheRoot, _worldUid, zx, zy, out zone);
        if (!loaded)
        {
            zone = new NavZone(zx, zy);
            var any = false;
            for (var cellZ = 0; cellZ < NavZone.CellsPerSide; cellZ++)
            {
                for (var cellX = 0; cellX < NavZone.CellsPerSide; cellX++)
                {
                    var worldX = zone.CellWorldX(cellX);
                    var worldZ = zone.CellWorldZ(cellZ);
                    if (!_terrain.TryHeight(worldX, worldZ, out var height))
                    {
                        continue;
                    }

                    any = true;
                    if (height < _waterLevel)
                    {
                        continue;
                    }

                    var slope = 0f;
                    if (_terrain.TryHeight(worldX + 1f, worldZ, out var neighbourX) &&
                        _terrain.TryHeight(worldX, worldZ + 1f, out var neighbourZ))
                    {
                        slope = (float)Math.Atan(Math.Max(Math.Abs(neighbourX - height), Math.Abs(neighbourZ - height)));
                    }

                    if (slope > 0.70f)
                    {
                        continue;
                    }

                    zone.AddSurface(cellX, cellZ, height, NavFlags.Walk, slope);
                }
            }

            if (any)
            {
                NavCache.Save(_cacheRoot, _worldUid, zone);
            }
        }

        _obstacles?.Stamp(zone);
        return zone;
    }

    private bool TrySurface(int globalX, int globalZ, float y, float tolerance, out Node node)
    {
        node = default;
        var bestScore = float.MaxValue;
        var found = false;

        // An entity can stand on a blocked cell (inside a tree/rock footprint) or between
        // cell centres. Search the immediate neighbourhood and start from the nearest surface.
        for (var dz = -2; dz <= 2; dz++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var candidateX = globalX + dx;
                var candidateZ = globalZ + dz;
                var zoneX = FloorDiv(candidateX, NavZone.CellsPerSide);
                var zoneZ = FloorDiv(candidateZ, NavZone.CellsPerSide);
                if (!_zones.TryGetValue(ZoneKey(zoneX, zoneZ), out var zone) ||
                    !zone.TryGetGlobalCell(candidateX, candidateZ, out var cell, out _, out _) ||
                    cell.Blocked ||
                    cell.Surfaces.Count == 0)
                {
                    continue;
                }

                for (var index = 0; index < cell.Surfaces.Count; index++)
                {
                    var vertical = Math.Abs(cell.Surfaces[index].Y - y);
                    if (vertical > tolerance + 1f)
                    {
                        continue;
                    }

                    var score = vertical + 0.25f * (Math.Abs(dx) + Math.Abs(dz));
                    if (score >= bestScore)
                    {
                        continue;
                    }

                    bestScore = score;
                    node = new Node(candidateX, candidateZ, index);
                    found = true;
                }
            }
        }

        return found;
    }

    private bool TryGetNodeSurface(Node node, out NavZone zone, out NavCell cell, out NavSurface surface)
    {
        zone = null!;
        cell = null!;
        surface = null!;
        var zoneX = FloorDiv(node.Gx, NavZone.CellsPerSide);
        var zoneZ = FloorDiv(node.Gz, NavZone.CellsPerSide);
        if (!_zones.TryGetValue(ZoneKey(zoneX, zoneZ), out var foundZone) ||
            !foundZone.TryGetGlobalCell(node.Gx, node.Gz, out var foundCell, out _, out _) ||
            node.Si < 0 || node.Si >= foundCell.Surfaces.Count)
        {
            return false;
        }

        zone = foundZone;
        cell = foundCell;
        surface = foundCell.Surfaces[node.Si];
        return true;
    }

    private bool HasSurfaceNear(int globalX, int globalZ, float y, float tolerance)
    {
        var zoneX = FloorDiv(globalX, NavZone.CellsPerSide);
        var zoneZ = FloorDiv(globalZ, NavZone.CellsPerSide);
        if (!_zones.TryGetValue(ZoneKey(zoneX, zoneZ), out var zone) ||
            !zone.TryGetGlobalCell(globalX, globalZ, out var cell, out _, out _) ||
            cell.Blocked)
        {
            return false;
        }

        foreach (var surface in cell.Surfaces)
        {
            if (Math.Abs(surface.Y - y) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private void Reconstruct(
        Dictionary<Node, Node> cameFrom,
        Node start,
        Node goal,
        Vector3 from,
        Vector3 to,
        List<Vector3> points)
    {
        var nodes = new List<Node> { goal };
        var current = goal;
        while (!current.Equals(start))
        {
            if (!cameFrom.TryGetValue(current, out var previous))
            {
                return;
            }

            nodes.Add(previous);
            current = previous;
        }

        nodes.Reverse();
        var raw = new List<Vector3>(nodes.Count);
        foreach (var node in nodes)
        {
            if (!TryGetNodeSurface(node, out _, out _, out var surface))
            {
                continue;
            }

            raw.Add(new Vector3(NavZone.WorldX(node.Gx), surface.Y, NavZone.WorldZ(node.Gz)));
        }

        if (raw.Count == 0)
        {
            return;
        }

        raw[0] = new Vector3(from.X, raw[0].Y, from.Z);
        raw[raw.Count - 1] = new Vector3(to.X, raw[raw.Count - 1].Y, to.Z);

        points.Add(raw[0]);
        var cursor = 0;
        while (cursor < raw.Count - 1)
        {
            var furthest = cursor + 1;
            for (var candidate = raw.Count - 1; candidate > cursor; candidate--)
            {
                if (CanWalkLine(raw[cursor], raw[candidate]))
                {
                    furthest = candidate;
                    break;
                }
            }

            points.Add(raw[furthest]);
            cursor = furthest;
        }
    }

    private static float Heuristic(Node from, Node goal)
    {
        var dx = NavZone.WorldX(from.Gx) - NavZone.WorldX(goal.Gx);
        var dz = NavZone.WorldZ(from.Gz) - NavZone.WorldZ(goal.Gz);
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static int GlobalCell(float value) => (int)Math.Floor(value + 32f);

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        var remainder = value % divisor;
        if (remainder != 0 && ((remainder < 0) != (divisor < 0)))
        {
            quotient--;
        }

        return quotient;
    }

    private static long ZoneKey(int zx, int zy) => ((long)zx << 32) | (uint)zy;

    private static readonly (int X, int Z)[] Directions =
    {
        (0, 1), (1, 0), (0, -1), (-1, 0),
        (1, 1), (1, -1), (-1, -1), (-1, 1)
    };

    private readonly struct Node : IEquatable<Node>
    {
        public readonly int Gx;
        public readonly int Gz;
        public readonly int Si;

        public Node(int gx, int gz, int si)
        {
            Gx = gx;
            Gz = gz;
            Si = si;
        }

        public bool Equals(Node other) => Gx == other.Gx && Gz == other.Gz && Si == other.Si;

        public override bool Equals(object? obj) => obj is Node other && Equals(other);

        public override int GetHashCode() => ((Gx * 397) ^ Gz) * 397 ^ Si;
    }

    private sealed class QueueItem : IComparable<QueueItem>
    {
        public float F;
        public int Gx;
        public int Gz;
        public int Si;
        public long Seq;

        public int CompareTo(QueueItem? other)
        {
            if (other == null)
            {
                return 1;
            }

            var score = F.CompareTo(other.F);
            return score != 0 ? score : Seq.CompareTo(other.Seq);
        }
    }
}
