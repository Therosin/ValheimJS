using System.Numerics;
using ValheimJS.World;
using ValheimJS.World.Navigation;

internal static class WorldManagerProbe
{
    internal static int Run()
    {
        try
        {
            CheckRelevance();
            CheckFlatPath();
            CheckWallDetour();
            CheckStairs();
            CheckWater();
            CheckCache();
            CheckMessages();
            Console.WriteLine("PASS: world relevance, navigation, cache and world messages");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: world manager probe: " + exception);
            return 1;
        }
    }

    private static void CheckRelevance()
    {
        Assert(WorldRelevancy.IsRelevant(9f, 0f, 0f, 0f, 10f, false), "inside radius is relevant");
        Assert(!WorldRelevancy.IsRelevant(12f, 0f, 0f, 0f, 10f, false), "outside radius is not relevant");
        Assert(WorldRelevancy.IsRelevant(12f, 0f, 0f, 0f, 10f, true), "hysteresis keeps a previous subscriber");
        Assert(!WorldRelevancy.IsRelevant(13f, 0f, 0f, 0f, 10f, true), "hysteresis exits beyond 1.25x");

        var objects = new List<(string Handle, float X, float Z)>
        {
            ("a", 5f, 0f), ("b", 12f, 0f), ("c", 100f, 0f)
        };
        var previous = new HashSet<string> { "b" };
        var next = new HashSet<string>();
        var add = new List<string>();
        var remove = new List<string>();
        WorldRelevancy.Diff(objects, previous, 0f, 0f, 10f, next, add, remove);
        Assert(next.SetEquals(new[] { "a", "b" }), "diff keeps inside and hysteresis members");
        Assert(add.SequenceEqual(new[] { "a" }), "diff adds only new members");
        Assert(remove.Count == 0, "diff does not remove hysteresis members");

        var moved = new HashSet<string>();
        WorldRelevancy.Diff(objects, next, 100f, 0f, 10f, moved, add, remove);
        Assert(moved.SetEquals(new[] { "c" }), "diff moves the set with the peer");
        Assert(add.SequenceEqual(new[] { "c" }), "diff adds the new member");
        Assert(remove.OrderBy(value => value).SequenceEqual(new[] { "a", "b" }), "diff removes the old members");
    }

    private static void CheckFlatPath()
    {
        var volume = NewVolume(new FakeTerrain((x, z) => 0f), null);
        var path = Find(volume, new Vector3(0.5f, 0f, 0.5f), new Vector3(10.5f, 0f, 10.5f));
        Assert(path.Count > 0, "flat ground produces a path");
        Assert(Vector3.Distance(path[^1], new Vector3(10.5f, 0f, 10.5f)) < 0.01f, "path ends at the target");
    }

    private static void CheckWallDetour()
    {
        var wall = new FakeObstacle(zone =>
        {
            for (var z = -8f; z <= 8f; z += 0.5f)
            {
                zone.BlockAtWorld(0f, z, 0.5f);
            }
        });
        var volume = NewVolume(new FakeTerrain((x, z) => 0f), wall);
        var path = Find(volume, new Vector3(-5f, 0f, 0f), new Vector3(5f, 0f, 0f));
        Assert(path.Count > 0, "wall has a detour");
        Assert(path.Any(point => Math.Abs(point.Z) > 1f), "detour leaves the straight line");
    }

    private static void CheckStairs()
    {
        var structures = new FakeObstacle(zone =>
        {
            for (var x = -24f; x < 24f; x += 0.5f)
            {
                var y = x < 0f ? 0f : x >= 4f ? 3f : x * 0.75f;
                if (x >= 0f && x < 4f)
                {
                    zone.ClearBlockedAtWorld(x, 0f, 0.6f);
                    zone.AddSurfaceAtWorld(x, 0f, y, NavFlags.Stair);
                }
                else if (x >= 4f)
                {
                    for (var z = -3f; z <= 3f; z += 0.5f)
                    {
                        zone.ClearBlockedAtWorld(x, z, 0.6f);
                        zone.AddSurfaceAtWorld(x, z, 3f, NavFlags.Indoor);
                    }
                }
            }
        });
        var volume = NewVolume(new FakeTerrain((x, z) => 0f), structures);
        var path = Find(volume, new Vector3(-2f, 0f, 0f), new Vector3(6f, 3f, 0f));
        Assert(path.Count > 0, "stacked platform is reachable by stairs");
        Assert(path[^1].Y >= 2.5f, "path reaches the upper floor");
    }

    private static void CheckWater()
    {
        var volume = NewVolume(new FakeTerrain((x, z) => -5f), null, waterLevel: 0f);
        var path = Find(volume, new Vector3(0.5f, 0f, 0.5f), new Vector3(4.5f, 0f, 4.5f));
        Assert(path.Count == 0, "water has no walkable path");
        Assert(!volume.TryRandomPoint(new Vector3(0f, 0f, 0f), 5f, out _), "random point never lands in water");
    }

    private static void CheckCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-nav-" + Guid.NewGuid().ToString("N"));
        try
        {
            var terrain = new FakeTerrain((x, z) => MathF.Sin(x * 0.1f) + MathF.Cos(z * 0.1f));
            var first = NewVolume(terrain, null, worldUid: 42, cacheRoot: root);
            Assert(first.TryGetZone(0, 0, out _) == false, "uncached zone is queued");
            Assert(first.BuildPendingZone(), "queued zone builds");
            Assert(first.TryGetZone(0, 0, out var built), "built zone is available");

            var second = NewVolume(terrain, null, worldUid: 42, cacheRoot: root);
            Assert(second.TryGetZone(0, 0, out var loaded), "cached zone loads without queueing");
            for (var index = 0; index < built.Cells.Length; index++)
            {
                var left = built.Cells[index].Surfaces;
                var right = loaded.Cells[index].Surfaces;
                Assert(left.Count == right.Count, $"cached surface count at {index}");
                for (var surface = 0; surface < left.Count; surface++)
                {
                    Assert(Math.Abs(left[surface].Y - right[surface].Y) < 0.001f, $"cached surface height at {index}");
                    Assert(left[surface].Flags == right[surface].Flags, $"cached surface flags at {index}");
                }
            }

            var path = NavCache.PathFor(root, 42, 0, 0)!;
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            var corrupt = NewVolume(terrain, null, worldUid: 42, cacheRoot: root);
            Assert(!corrupt.TryGetZone(0, 0, out _), "corrupt cache is treated as uncached");
            Assert(corrupt.BuildPendingZone(), "corrupt cache rebuilds");
            Assert(corrupt.TryGetZone(0, 0, out _), "rebuilt zone is available");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void CheckMessages()
    {
        var message = new WorldRelevanceMessage
        {
            Add = new[] { new WorldEntityInfo { Id = "1:2", Kind = "wanderer", Plugin = "demo", Name = "Wanderer" } },
            Remove = new[] { "3:4" }
        };
        var json = ValheimJS.Networking.NetworkJson.Write(message);
        var round = ValheimJS.Networking.NetworkJson.Read<WorldRelevanceMessage>(json);
        Assert(round.Add.Length == 1 && round.Add[0].Id == "1:2" && round.Add[0].Kind == "wanderer", "relevance DTO round-trips");
        Assert(round.Remove.Length == 1 && round.Remove[0] == "3:4", "relevance removals round-trip");

        var entity = new WorldEntityMessage
        {
            Op = "spawn",
            Entity = new WorldEntityInfo { Id = "5:6", Kind = "wanderer", Plugin = "demo", Name = "New" }
        };
        var entityRound = ValheimJS.Networking.NetworkJson.Read<WorldEntityMessage>(ValheimJS.Networking.NetworkJson.Write(entity));
        Assert(entityRound.Op == "spawn" && entityRound.Entity.Id == "5:6", "entity DTO round-trips");
    }

    private static NavVolume NewVolume(ITerrainSource terrain, IObstacleSource? obstacles, float waterLevel = -100f, long worldUid = 0, string? cacheRoot = null)
        => new(terrain, obstacles, cacheRoot ?? Path.Combine(Path.GetTempPath(), "valheimjs-nav-unused"), worldUid, waterLevel);

    private static List<Vector3> Find(NavVolume volume, Vector3 from, Vector3 to)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var path = new List<Vector3>();
            if (volume.FindPath(from, to, path, out var pending))
            {
                return path;
            }

            if (!pending)
            {
                return new List<Vector3>();
            }

            volume.BuildPendingZone();
        }

        return new List<Vector3>();
    }

    private sealed class FakeTerrain : ITerrainSource
    {
        private readonly Func<float, float, float> _height;

        public FakeTerrain(Func<float, float, float> height) => _height = height;

        public bool TryHeight(float x, float z, out float y)
        {
            y = _height(x, z);
            return true;
        }
    }

    private sealed class FakeObstacle : IObstacleSource
    {
        private readonly Action<NavZone> _stamp;

        public FakeObstacle(Action<NavZone> stamp) => _stamp = stamp;

        public void Stamp(NavZone zone) => _stamp(zone);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
