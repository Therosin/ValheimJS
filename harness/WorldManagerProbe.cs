using System.Numerics;
using ValheimJS.World;

internal static class WorldManagerProbe
{
    internal static int Run()
    {
        try
        {
            CheckRelevance();
            CheckMessages();
            Console.WriteLine("PASS: world relevance and world messages");
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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
