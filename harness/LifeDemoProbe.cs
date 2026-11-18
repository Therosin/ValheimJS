using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimJS.AI;
using ValheimJS.Core;

/// <summary>
/// Compiles the real life-demo YAML library and asserts that each activity reaches its expected
/// primitive sequence through imports. This is the composition contract the TypeScript
/// activity definitions rely on; selection and feedback are tested in the Bun suite.
/// </summary>
internal static class LifeDemoProbe
{
    internal static int Run()
    {
        try
        {
            var behaviours = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "scripting", "testing", "life-demo", "behaviours"));
            if (!Directory.Exists(behaviours))
            {
                throw new DirectoryNotFoundException("life-demo behaviours not found at " + behaviours);
            }

            var micro = Directory.GetFiles(Path.Combine(behaviours, "micro"), "*.yaml")
                .ToDictionary(path => Path.GetFileNameWithoutExtension(path), File.ReadAllText, StringComparer.Ordinal);

            Check(behaviours, micro, "wander.yaml", ["select-point", "move-to", "wait"]);
            Check(behaviours, micro, "forage.yaml", ["select-point", "move-to", "look-around", "wait"]);
            Check(behaviours, micro, "explore.yaml", ["select-point", "move-to", "look-around", "wait"]);
            Check(behaviours, micro, "return-home.yaml", ["select-home", "move-to", "wait"]);
            Check(behaviours, micro, "rest.yaml", ["select-home", "move-to", "wait"]);
            Check(behaviours, micro, "seek-home.yaml", ["select-shelter", "move-to", "claim-home", "wait"]);
            Check(behaviours, micro, "inspect.yaml", ["select-target", "move-to", "look-at", "mark-visited", "wait"]);
            Check(behaviours, micro, "socialize.yaml", ["select-target", "move-to", "face", "remember-meeting", "wait"]);
            Check(behaviours, micro, "flee.yaml", ["select-flee-point", "move-to", "wait"]);

            Console.WriteLine("PASS: life-demo YAML behaviours compose through imports");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: life-demo behaviour probe: " + exception);
            return 1;
        }
    }

    private static void Check(string root, IReadOnlyDictionary<string, string> micro, string file, string[] expected)
    {
        var path = Path.Combine(root, file);
        var tree = BehaviourTree.Parse(File.ReadAllText(path), path, null, alias =>
            micro.TryGetValue(alias, out var text)
                ? text
                : throw new BehaviourException($"import alias '{alias}' has no file in {file}"));

        var calls = new List<string>();
        var trace = new List<string>();
        var running = new Dictionary<string, int>(StringComparer.Ordinal);
        var state = tree.CreateInstance(
            host: null,
            action: (name, _) =>
            {
                if (name is "move-to" or "wait")
                {
                    running.TryGetValue(name, out var count);
                    running[name] = count + 1;
                    if (count == 0)
                    {
                        calls.Add(name);
                        return BehaviourResult.Running;
                    }
                    return BehaviourResult.Success();
                }
                calls.Add(name);
                return BehaviourResult.Success();
            },
            condition: (name, _) => name != "at-home",
            random: () => 0.5,
            trace: trace.Add);

        state.State["destination"] = new Dictionary<string, object?> { ["x"] = 1d, ["y"] = 2d, ["z"] = 3d };
        state.State["target"] = new Dictionary<string, object?> { ["id"] = "target", ["pos"] = state.State["destination"] };
        state.State["run"] = false;
        state.State["waitSeconds"] = 2d;

        var status = BehaviourStatus.Running;
        for (var tick = 0; tick < 32 && status == BehaviourStatus.Running; tick++)
        {
            status = state.Tick(0.5f).Status;
        }

        if (status != BehaviourStatus.Success)
        {
            throw new BehaviourException($"{file}: ended {status}, expected success");
        }

        if (!calls.SequenceEqual(expected))
        {
            throw new BehaviourException($"{file}: actions [{string.Join(", ", calls)}], expected [{string.Join(", ", expected)}]");
        }

        if (!trace.Any(line => line.Contains("tree ")) ||
            !trace.Any(line => line.Contains("use move")) ||
            !trace.Any(line => line.Contains("action move-to")))
        {
            throw new BehaviourException($"{file}: trace did not expose tree/move composition: {string.Join(" | ", trace)}");
        }
    }
}
