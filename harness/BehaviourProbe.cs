using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimJS.AI;
using ValheimJS.Core;
using ValheimJS.Utils;

internal static class BehaviourProbe
{
    internal static int Run()
    {
        try
        {
            CheckYaml();
            CheckSequenceAndSelector();
            CheckConditionBranches();
            CheckRunningAction();
            CheckHooks();
            CheckRandomSelection();
            CheckReferencesAndOutput();
            CheckStateIsolation();
            CheckImports();
            CheckPlanExample();
            CheckGoto();
            CheckCancellation();
            CheckErrors();
            Console.WriteLine("PASS: yaml parser and behaviour tree execution");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: behaviour probe: " + exception);
            return 1;
        }
    }

    private static void CheckYaml()
    {
        var document = Cast(Yaml.Parse("""
            # leading comment
            name: probe
            nested:
              list: [a, "b c", 3]
              map:
                x: 1
            quoted: 'it''s'
            literal: |
              first
              second
            folded: >-
              one
              two
            empty-value: ~
            enabled: true
            ratio: 1.5
            inline: {a: 1, b: [2, 3]}
            flow: [
              alpha,
              beta
            ]
            # trailing comment
            """));

        Assert((string?)document["name"] == "probe", "plain scalar");
        var nested = Cast(document["nested"]);
        var list = (List<object?>)nested["list"]!;
        Assert(list.Count == 3 && (string?)list[0] == "a" && (string?)list[1] == "b c" && (long)list[2]! == 3, "flow sequence");
        Assert((long)Cast(nested["map"])["x"]! == 1, "nested mapping");
        Assert((string?)document["quoted"] == "it's", "single-quoted escape");
        Assert((string?)document["literal"] == "first\nsecond\n", "literal block scalar");
        Assert((string?)document["folded"] == "one two", "folded block scalar");
        Assert(document["empty-value"] == null, "null scalar");
        Assert((bool)document["enabled"]! && Math.Abs((double)document["ratio"]! - 1.5) < 0.0001, "boolean and float scalars");
        var inline = Cast(document["inline"]);
        Assert((long)inline["a"]! == 1 && ((List<object?>)inline["b"]!).Count == 2, "flow mapping");
        var flow = (List<object?>)document["flow"]!;
        Assert(flow.Count == 2 && (string?)flow[1] == "beta", "multi-line flow sequence");

        var sameIndent = Cast(Yaml.Parse("items:\n- one\n- two\n"));
        Assert(((List<object?>)sameIndent["items"]!).Count == 2, "same-indent block sequence");

        var apostrophe = (List<object?>)Cast(Yaml.Parse("items: [don't, \"x\"]\n"))["items"]!;
        Assert((string?)apostrophe[0] == "don't" && (string?)apostrophe[1] == "x", "apostrophe inside a flow scalar");

        Throws<YamlException>(() => Yaml.Parse("a: 1\na: 2\n"), "duplicate key");
        Throws<YamlException>(() => Yaml.Parse("a:\n\tb: 1\n"), "tab indentation");
        Throws<YamlException>(() => Yaml.Parse("a: [1, 2\n"), "unterminated flow collection");
    }

    private static void CheckSequenceAndSelector()
    {
        var order = new List<string>();
        var tree = Parse("""
            root:
              type: sequence
              children:
                - action: first
                - action: second
            """);
        var instance = tree.CreateInstance(action: (name, _) =>
        {
            order.Add(name);
            return BehaviourResult.Success();
        });
        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Success, "sequence completes");
        Assert(order.SequenceEqual(new[] { "first", "second" }), "sequence order");

        order.Clear();
        tree = Parse("""
            root:
              type: selector
              children:
                - action: reject
                - action: accept
            """);
        instance = tree.CreateInstance(action: (name, _) =>
        {
            order.Add(name);
            return name == "reject" ? BehaviourResult.Failure() : BehaviourResult.Success();
        });
        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Success, "selector falls through");
        Assert(order.SequenceEqual(new[] { "reject", "accept" }), "selector order");
    }

    private static void CheckConditionBranches()
    {
        var visited = new List<string>();
        var flag = true;
        var tree = Parse("""
            root:
              condition: danger
              true:
                action: flee
              false:
                action: wander
            """);
        var instance = tree.CreateInstance(
            condition: (name, _) => name == "danger" && flag,
            action: (name, _) =>
            {
                visited.Add(name);
                return BehaviourResult.Success();
            });
        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Success && visited.SequenceEqual(new[] { "flee" }), "true branch");
        flag = false;
        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Success && visited.Last() == "wander", "false branch");

        tree = Parse("""
            root:
              condition: empty
            """);
        instance = tree.CreateInstance(condition: (_, _) => false);
        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Failure, "branchless condition fails");
    }

    private static void CheckRunningAction()
    {
        var calls = 0;
        var followers = 0;
        var tree = Parse("""
            root:
              type: sequence
              children:
                - action: work
                - action: after
            """);
        var instance = tree.CreateInstance(action: (name, _) =>
        {
            if (name == "work")
            {
                calls++;
                return calls < 2 ? BehaviourResult.Running : BehaviourResult.Success();
            }

            followers++;
            return BehaviourResult.Success();
        });

        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Running, "running action holds the sequence");
        Assert(calls == 1 && followers == 0, "running action did not advance");
        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Success, "action finishes on the next tick");
        Assert(calls == 2 && followers == 1, "action resumed instead of restarting");
    }

    private static void CheckHooks()
    {
        var events = new List<string>();
        var interrupted = new List<bool>();
        var actionCalls = 0;
        var tree = Parse("""
            root:
              action: work
              args:
                label: "@[label]"
              execBefore: "before"
              exec: "each"
              execAfter: "after"
            """);
        var instance = tree.CreateInstance(
            execute: (code, context) =>
            {
                events.Add(code + ":" + context.Args["label"]);
                if (code == "after")
                {
                    interrupted.Add(context.Interrupted);
                }

                return null;
            },
            action: (_, _) =>
            {
                actionCalls++;
                return actionCalls < 2 ? BehaviourResult.Running : BehaviourResult.Success();
            });

        instance.State["label"] = "alpha";
        instance.Tick(0.1f);
        Assert(events.SequenceEqual(new[] { "before:alpha", "each:alpha" }), "entry hooks");
        instance.Tick(0.1f);
        Assert(events.SequenceEqual(new[] { "before:alpha", "each:alpha", "each:alpha", "after:alpha" }), "completion hooks");
        instance.Tick(0.1f);
        Assert(events.Count == 7 && events[4] == "before:alpha" && interrupted.SequenceEqual(new[] { false, false }),
            "re-entry hooks");
    }

    private static void CheckRandomSelection()
    {
        var chosen = new List<string>();
        var slowCalls = 0;
        var tree = Parse("""
            root:
              type: random
              children:
                - action: slow
                  weight: 3
                - action: other
            """);
        var instance = tree.CreateInstance(
            action: (name, _) =>
            {
                chosen.Add(name);
                if (name != "slow")
                {
                    return BehaviourResult.Success();
                }

                slowCalls++;
                return slowCalls < 2 ? BehaviourResult.Running : BehaviourResult.Success();
            },
            random: () => 0);

        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Running, "random child running");
        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Success, "random child completes");
        Assert(chosen.Count == 2 && chosen.All(name => name == "slow"), "selection persisted while running");
        instance.Tick(0.1f);
        Assert(chosen.Count == 3 && chosen.All(name => name == "slow"), "selection after completion");

        Throws<BehaviourException>(() => Parse("""
            root:
              type: random
              children:
                - action: a
                  weight: -1
            """), "negative weight");
        Throws<BehaviourException>(() => Parse("""
            root:
              type: random
              children:
                - action: a
                  weight: 0
            """), "all zero weights");
    }

    private static void CheckReferencesAndOutput()
    {
        object? seen = null;
        object? speed = null;
        List<object?>? nested = null;
        var tree = Parse("""
            state:
              target: null
              settings:
                speed: walk
            root:
              type: sequence
              children:
                - action: find
                  out: "@[target]"
                - action: move
                  args:
                    target: "@[target]"
                    speed: "@[settings.speed]"
                    nested:
                      - "@[target]"
                      - 7
            """);
        var instance = tree.CreateInstance(action: (name, context) =>
        {
            if (name == "find")
            {
                return BehaviourResult.Success("monster");
            }

            seen = context.Args["target"];
            speed = context.Args["speed"];
            nested = context.Args["nested"] as List<object?>;
            return BehaviourResult.Success();
        });

        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Success, "reference tree completes");
        Assert((string?)seen == "monster" && (string?)speed == "walk", "resolved state references");
        Assert(nested != null && (string?)nested[0] == "monster" && (long)nested[1]! == 7, "nested reference values");
        Assert((string?)instance.State["target"] == "monster", "action output assigned");

        tree = Parse("""
            state:
              target: null
            root:
              action: find
              out: "@[target]"
            """);
        instance = tree.CreateInstance(action: (_, _) => BehaviourResult.Failure("nope"));
        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Failure && instance.State["target"] == null, "failure keeps output unassigned");

        tree = Parse("""
            root:
              action: read
              args:
                value: "@[missing]"
            """);
        instance = tree.CreateInstance(action: (_, _) => BehaviourResult.Success());
        Throws<BehaviourException>(() => instance.Tick(0.1f), "missing state reference");
    }

    private static void CheckStateIsolation()
    {
        var tree = Parse("""
            state:
              count: 0
            root:
              action: bump
            """);
        BehaviourActionExecutor bump = (_, context) =>
        {
            context.State["count"] = (long)context.State["count"]! + 1;
            return BehaviourResult.Success();
        };

        var first = tree.CreateInstance(action: bump);
        var second = tree.CreateInstance(action: bump);
        first.Tick(0.1f);
        second.Tick(0.1f);
        first.Tick(0.1f);
        Assert((long)first.State["count"]! == 2 && (long)second.State["count"]! == 1, "instances keep independent state");

        first.Reset(resetState: true);
        Assert((long)first.State["count"]! == 0, "reset restores initial state");
    }

    private static void CheckImports()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-behaviour-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "inner.yaml"), """
            id: inner
            state:
              depth: deep
              shared: inner
            root:
              action: inner_action
            """);
        File.WriteAllText(Path.Combine(root, "middle.yaml"), """
            id: middle
            imports:
              inner: ./inner.yaml
            state:
              shared: middle
            root:
              type: sequence
              children:
                - use: inner
            """);
        File.WriteAllText(Path.Combine(root, "main.yaml"), """
            id: main
            imports:
              middle: ./middle.yaml
            state:
              shared: main
              extra: 1
            root:
              type: sequence
              children:
                - use: middle
            """);

        var warnings = new List<string>();
        var tree = BehaviourTree.Load(Path.Combine(root, "main.yaml"), warnings.Add);
        Assert(tree.Id == "main", "tree id");
        Assert(tree.InitialState.Count == 3, "merged state keys");
        Assert((string?)tree.InitialState["shared"] == "main", "parent state overrides imports");
        Assert((string?)tree.InitialState["depth"] == "deep", "nested import state merged");
        Assert((long)tree.InitialState["extra"]! == 1, "own state kept");

        object? observed = null;
        var instance = tree.CreateInstance(action: (name, context) =>
        {
            observed = context.State["shared"];
            Assert(name == "inner_action", "imported action name");
            return BehaviourResult.Success();
        });
        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Success, "imported tree runs");
        Assert((string?)observed == "main", "imports share instance state");

        File.WriteAllText(Path.Combine(root, "a.yaml"), "imports:\n  b: ./b.yaml\nroot:\n  action: a\n");
        File.WriteAllText(Path.Combine(root, "b.yaml"), "imports:\n  a: ./a.yaml\nroot:\n  action: b\n");
        Throws<BehaviourException>(() => BehaviourTree.Load(Path.Combine(root, "a.yaml")), "circular import");

        File.WriteAllText(Path.Combine(root, "missing.yaml"), "imports:\n  gone: ./gone.yaml\nroot:\n  action: a\n");
        Throws<BehaviourException>(() => BehaviourTree.Load(Path.Combine(root, "missing.yaml")), "missing import");

        File.WriteAllText(Path.Combine(root, "one.yaml"), "state:\n  shared: one\nroot:\n  action: one\n");
        File.WriteAllText(Path.Combine(root, "two.yaml"), "state:\n  shared: two\nroot:\n  action: two\n");
        File.WriteAllText(Path.Combine(root, "conflict.yaml"), """
            imports:
              one: ./one.yaml
              two: ./two.yaml
            root:
              action: main
            """);
        warnings.Clear();
        BehaviourTree.Load(Path.Combine(root, "conflict.yaml"), warnings.Add);
        Assert(warnings.Count == 1 && warnings[0].Contains("shared"), "import state conflict warned");

        File.WriteAllText(Path.Combine(root, "loop.yaml"), """
            id: loop
            root:
              type: sequence
              children:
                - action: count
                  id: again
                - condition: done
                  false:
                    goto: again
            """);
        File.WriteAllText(Path.Combine(root, "outer.yaml"), """
            imports:
              loop: ./loop.yaml
            root:
              type: sequence
              children:
                - use: loop
            """);
        var counted = 0;
        var checks = 0;
        var looping = BehaviourTree.Load(Path.Combine(root, "outer.yaml")).CreateInstance(
            action: (_, _) =>
            {
                counted++;
                return BehaviourResult.Success();
            },
            condition: (_, _) =>
            {
                checks++;
                return checks > 1;
            });
        Assert(looping.Tick(0.1f).Status == BehaviourStatus.Success, "goto inside an imported tree");
        Assert(counted == 2 && checks == 2, "imported goto stayed in its own scope");

        File.WriteAllText(Path.Combine(root, "inner-id.yaml"), "id: inner\nroot:\n  action: x\n  id: inside\n");
        File.WriteAllText(Path.Combine(root, "cross.yaml"), "imports:\n  inner: ./inner-id.yaml\nroot:\n  goto: inside\n");
        var crossError = Catch<BehaviourException>(() => BehaviourTree.Load(Path.Combine(root, "cross.yaml")),
            "goto cannot reach an imported id");
        Assert(crossError.Message.Contains("not declared in this file"), "goto scope reported precisely");
    }

    private static void CheckPlanExample()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "wander.yaml"), "id: wander\nroot:\n  action: wander\n");
        File.WriteAllText(Path.Combine(root, "flee.yaml"), "id: flee\nroot:\n  action: flee\n");
        File.WriteAllText(Path.Combine(root, "example.yaml"), """
            id: example
            description: Example of a reusable behaviour

            imports:
              wander: ./wander.yaml
              flee: ./flee.yaml

            state:
              target: null
              safezone: null
              flee_speed: run
              attempts: 0

            root:
              type: selector
              execBefore: |
                ctx.this.log("Starting behaviour")

              children:
                - condition: danger
                  true:
                    use: flee
                  false:
                    type: random
                    children:
                      - use: wander
                        weight: 3

                      - id: find_target
                        action: find_target
                        args:
                          radius: 35
                          filters: [tree, ore]
                        out: "@[target]"

                      - type: sequence
                        children:
                          - action: find_safe_position
                            args:
                              radius: 40
                            out: "@[safezone]"

                          - action: move_to
                            args:
                              target: "@[safezone]"
                              speed: "@[flee_speed]"

                            execBefore: |
                              ctx.state.attempts++;

                            exec: |
                              ctx.this.log("Moving");

                            execAfter: |
                              ctx.this.log("Movement finished");

                - goto: find_target

              execAfter: |
                ctx.this.log("Behaviour ended")
            """);

        var host = new object();
        var actions = new List<string>();
        var scripts = new List<string>();
        var sameHost = true;
        var tree = BehaviourTree.Load(Path.Combine(root, "example.yaml"));
        var instance = tree.CreateInstance(
            host: host,
            execute: (code, context) =>
            {
                sameHost &= ReferenceEquals(context.This, host);
                scripts.Add(code.Trim());
                return null;
            },
            action: (name, context) =>
            {
                actions.Add(name);
                return name switch
                {
                    "find_target" => BehaviourResult.Success("monster"),
                    "wander" => BehaviourResult.Failure(),
                    _ => BehaviourResult.Success()
                };
            },
            condition: (_, _) => false,
            random: () => 0);

        var result = instance.Tick(0.1f);
        Assert(result.Status == BehaviourStatus.Success, "plan example completes");
        Assert(actions.SequenceEqual(new[] { "wander", "find_target" }), "plan example goto reached");
        Assert((string?)instance.State["target"] == "monster", "plan example output assigned");
        Assert(sameHost, "host object flows into scripts");
        Assert(scripts.Contains("ctx.this.log(\"Starting behaviour\")") &&
               scripts.Contains("ctx.this.log(\"Behaviour ended\")"), "root hooks ran");
        Assert(scripts.Contains("ctx.this.log(\"Moving\")") == false, "unreached branch hooks did not run");
    }

    private static void CheckGoto()
    {
        var attempts = 0;
        var evaluations = 0;
        var tree = Parse("""
            root:
              type: sequence
              children:
                - action: count
                  id: first
                - condition: stop
                  false:
                    goto: first
            """);
        var instance = tree.CreateInstance(
            action: (_, _) =>
            {
                attempts++;
                return BehaviourResult.Success();
            },
            condition: (_, _) =>
            {
                evaluations++;
                return evaluations > 1;
            });

        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Success, "goto resumes the tree");
        Assert(attempts == 2 && evaluations == 2, "goto re-ran the named node inside the same tick");

        tree = Parse("""
            root:
              type: sequence
              children:
                - action: count
                  id: first
                - condition: never
                  false:
                    goto: first
            """);
        instance = tree.CreateInstance(
            action: (_, _) => BehaviourResult.Success(),
            condition: (_, _) => false)
            ;
        instance.MaxJumpsPerTick = 5;
        Throws<BehaviourException>(() => instance.Tick(0.1f), "goto loop budget");

        Throws<BehaviourException>(() => Parse("""
            root:
              goto: nowhere
            """), "unknown goto target");
    }

    private static void CheckCancellation()
    {
        var cleanups = 0;
        var interrupted = false;
        var tree = Parse("""
            root:
              action: work
              execAfter: "cleanup"
            """);
        var instance = tree.CreateInstance(
            execute: (_, context) =>
            {
                cleanups++;
                interrupted = context.Interrupted;
                return null;
            },
            action: (_, _) => BehaviourResult.Running);

        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Running, "running before cancel");
        instance.Cancel();
        Assert(cleanups == 1 && interrupted, "cancel ran execAfter as interrupted");
        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Cancelled, "cancelled instance is inert");

        instance.Reset();
        Assert(instance.Tick(0.1f).Status == BehaviourStatus.Running, "reset makes the instance tickable");
        instance.Reset();
        Assert(cleanups == 2 && interrupted, "reset ran execAfter as interrupted");
    }

    private static void CheckErrors()
    {
        var tree = Parse("""
            root:
              action: work
            """);
        var instance = tree.CreateInstance();
        Throws<BehaviourException>(() => instance.Tick(0.1f), "missing action executor");

        tree = Parse("""
            root:
              action: work
              exec: "boom"
            """);
        instance = tree.CreateInstance(
            action: (_, _) => BehaviourResult.Success(),
            execute: (_, _) => throw new InvalidOperationException("script exploded"));
        Throws<InvalidOperationException>(() => instance.Tick(0.1f), "script exception surfaced");

        Throws<BehaviourException>(() => Parse("root:\n  type: sequence\n  children: []\n"), "empty children");
        Throws<BehaviourException>(() => Parse("root:\n  type: bump\n"), "unknown type");
        Throws<BehaviourException>(() => Parse("root:\n  action: a\n  colour: red\n"), "unknown key");
        Throws<BehaviourException>(() => Parse("root:\n  type: sequence\n  children:\n    - id: x\n      action: a\n    - id: x\n      action: b\n"),
            "duplicate ids");
        Throws<BehaviourException>(() => Parse("root:\n  action: a\n  out: target\n"), "invalid out reference");
        Throws<BehaviourException>(() => Parse("root:\n  action: a\n  weight: 2\n"), "weight outside random");
    }

    private static BehaviourTree Parse(string yaml) =>
        BehaviourTree.Parse(yaml, Path.Combine(Path.GetTempPath(), "valheimjs-behaviour", "tree.yaml"));

    private static YamlMap Cast(object? value) =>
        value as YamlMap ?? throw new InvalidOperationException("expected a YAML mapping, got " + (value?.GetType().Name ?? "null"));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("assertion failed: " + message);
        }
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception => Catch<TException>(action, message);

    private static TException Catch<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("expected " + typeof(TException).Name + ": " + message);
    }
}
