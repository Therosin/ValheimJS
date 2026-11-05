using ValheimJS.Core;

internal static class WorldProbe
{
    internal static int Run()
    {
        try
        {
            CheckOwnership();
            CheckRelease();
            CheckHandleUniqueness();
            Console.WriteLine("PASS: spawn handle ownership, foreign rejection and release");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: world probe: " + exception);
            return 1;
        }
    }

    private static void CheckOwnership()
    {
        var handles = new SpawnHandleTable<int>();
        var first = handles.Add("alpha", 11);
        var second = handles.Add("beta", 22);

        Assert(handles.Resolve("alpha", first, out var id) == HandleLookup.Ok && id == 11, "owner resolves own handle");
        Assert(handles.Resolve("beta", first, out _) == HandleLookup.Foreign, "foreign handle is refused");
        Assert(handles.Resolve("alpha", "spawn-999", out _) == HandleLookup.Unknown, "unknown handle is unknown");
        Assert(handles.Count == 2, "foreign or unknown lookups must not mutate the table");

        Assert(handles.Take("beta", first, out _) == HandleLookup.Foreign, "foreign take is refused");
        Assert(handles.Resolve("alpha", first, out var stillThere) == HandleLookup.Ok && stillThere == 11, "foreign take leaves the entry");
        Assert(handles.Take("beta", second, out var taken) == HandleLookup.Ok && taken == 22, "owner take resolves the id");
        Assert(handles.Resolve("beta", second, out _) == HandleLookup.Unknown, "taken handle is forgotten");
    }

    private static void CheckRelease()
    {
        var handles = new SpawnHandleTable<int>();
        var alpha = handles.Add("alpha", 1);
        var beta = handles.Add("beta", 2);

        Assert(handles.Release("alpha") == 1, "release drops only the owner's handles");
        Assert(handles.Resolve("alpha", alpha, out _) == HandleLookup.Unknown, "released handle is gone");
        Assert(handles.Resolve("beta", beta, out _) == HandleLookup.Ok, "other owner survives release");
        Assert(handles.Count == 1, "release leaves unrelated entries");
        Assert(handles.Release("alpha") == 0, "repeated release is a no-op");
    }

    private static void CheckHandleUniqueness()
    {
        var handles = new SpawnHandleTable<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 64; index++)
        {
            Assert(seen.Add(handles.Add(index % 2 == 0 ? "alpha" : "beta", index)), "handle names are never reused");
        }

        Assert(handles.Release("alpha") == 32 && handles.Count == 32, "release counts owned handles");
        var afterRelease = handles.Add("alpha", 99);
        Assert(!seen.Contains(afterRelease), "a new handle does not collide with released names");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
