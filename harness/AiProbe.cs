using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimJS.Bindings;
using ValheimJS.Core;
using ValheimJS.Entities;
using ValheimJS.Scripting;

internal static class AiProbe
{
    internal static int Run()
    {
        CheckPerceptionModel();
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-ai-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "plugins", "probe");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.ini"), "name=probe\napiVersion=1\n");
            File.WriteAllText(Path.Combine(directory, "shared.js"), """
                (function () {
                    var ctx = globalThis.__vjs_context;
                    var api = ctx.host.api.ai;

                    var brain = [
                        "id: probe",
                        "state:",
                        "  attempts: 0",
                        "  target: null",
                        "  extra: 0",
                        "  flag: false",
                        "root:",
                        "  type: sequence",
                        "  execBefore: \"start\"",
                        "  execAfter: \"end\"",
                        "  children:",
                        "    - action: work",
                        "      id: work",
                        "      args:",
                        "        label: \"@[extra]\"",
                        "      out: \"@[target]\"",
                        "    - condition: ready",
                        "      true:",
                        "        action: done",
                        "      false:",
                        "        goto: work"
                    ].join("\n");

                    ctx.state.execs = [];
                    ctx.state.importCalls = 0;
                    ctx.state.host = { tag: "npc" };
                    ctx.state.tree = api.compile(brain, { source: "probe.yaml" });
                    ctx.state.id = api.instantiate(ctx.state.tree, {
                        this: ctx.state.host,
                        state: { extra: 3 },
                        exec: function (code, c) {
                            ctx.state.execs.push(code + ":" + (c.this === ctx.state.host) + ":" + c.deltaTime);
                        },
                        action: function (name, c) {
                            if (c.this !== ctx.state.host) throw Error("this mismatch");
                            if (name === "work") {
                                c.state.attempts = (c.state.attempts || 0) + 1;
                                return c.state.attempts < 2
                                    ? "running"
                                    : { status: "success", output: "found:" + c.args.label };
                            }
                            if (name === "done") return "success";
                            return "failure";
                        },
                        condition: function (name, c) {
                            return name === "ready" && c.state.attempts >= 2;
                        },
                        random: function () { return 0; }
                    });

                    var child = [
                        "id: with-child",
                        "imports:",
                        "  child: ./child.yaml",
                        "state:",
                        "  hits: 0",
                        "root:",
                        "  type: sequence",
                        "  children:",
                        "    - use: child",
                        "    - action: done"
                    ].join("\n");
                    ctx.state.childTree = api.compile(child, {
                        source: "child-main.yaml",
                        imports: {
                            child: "state:\n  hits: 0\nroot:\n  action: imported\n"
                        }
                    });
                    var senseTree = api.compile([
                        "state:",
                        "  ignored: 0",
                        "root:",
                        "  condition: sees",
                        "  true:",
                        "    action: spotted"
                    ].join("\n"));
                    ctx.state.senseId = api.instantiate(senseTree, {
                        this: { id: "fake" },
                        condition: function (name, c) {
                            return name === "sees" && !!c.state.senses && c.state.senses.nearby[0].name === "player";
                        },
                        action: function () { ctx.state.spotted = true; return "success"; }
                    });

                    ctx.state.childId = api.instantiate(ctx.state.childTree, {
                        action: function (name, c) {
                            if (name === "imported") ctx.state.importCalls++;
                            c.state.hits = (c.state.hits || 0) + 1;
                            return "success";
                        }
                    });

                    globalThis.__vjs_plugin = { __vjsPlugin:'object', value: {
                        setup: function () {},
                        step: function () { return api.tick(ctx.state.id, 0.1); },
                        status: function () { return api.status(ctx.state.id); },
                        snapshot: function () { return api.state(ctx.state.id); },
                        setExtra: function (_ctx, value) { api.set(ctx.state.id, "extra", value); return api.state(ctx.state.id); },
                        cancel: function () { api.cancel(ctx.state.id); return api.status(ctx.state.id); },
                        reset: function () { api.reset(ctx.state.id, true); return api.status(ctx.state.id); },
                        execs: function () { return ctx.state.execs; },
                        childTick: function () { return api.tick(ctx.state.childId, 0.1); },
                        sensesTick: function () { return api.tick(ctx.state.senseId, 0.1); },
                        spotted: function () { return ctx.state.spotted === true; },
                        childCalls: function () { return ctx.state.importCalls; },
                        errors: function () {
                            var result = {};
                            try { api.compile("root:\n  action: a\n  nope: 1\n"); result.compile = "no-error"; }
                            catch (e) { result.compile = String(e.message || e); }
                            try { api.tick("missing", 0.1); result.tick = "no-error"; }
                            catch (e) { result.tick = String(e.message || e); }
                            try {
                                api.compile("imports:\n  a: ./missing.yaml\nroot:\n  use: a\n");
                                result.disk = "no-error";
                            } catch (e) { result.disk = String(e.message || e); }
                            try {
                                api.compile("imports:\n  a: ./a.yaml\nroot:\n  use: a\n", {
                                    imports: {
                                        a: "imports:\n  b: ./b.yaml\nroot:\n  use: b\n",
                                        b: "imports:\n  a: ./a.yaml\nroot:\n  use: a\n"
                                    }
                                });
                                result.cycle = "no-error";
                            } catch (e) { result.cycle = String(e.message || e); }
                            return result;
                        },
                        warn: function () {
                            var calls = 0;
                            api.compile([
                                "imports:",
                                "  one: ./one.yaml",
                                "  two: ./two.yaml",
                                "root:",
                                "  action: main"
                            ].join("\n"), {
                                warn: function () { calls++; },
                                imports: {
                                    one: "state:\n  shared: one\nroot:\n  action: one\n",
                                    two: "state:\n  shared: two\nroot:\n  action: two\n"
                                }
                            });
                            return calls;
                        },
                        unload: function () {
                            var tree = api.compile("root:\n  action: x\n");
                            var id = api.instantiate(tree, { action: function () { return "success"; } });
                            var removed = api.unload(tree);
                            try { api.tick(id, 0.1); return { removed: removed, tick: "no-error" }; }
                            catch (e) { return { removed: removed, tick: String(e.message || e) }; }
                        }
                    }};
                })();
                """);

            AiSenses.Publish("probe", "fake", new Dictionary<string, object?>
            {
                ["nearby"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = "player",
                        ["distance"] = 3d,
                        ["enemy"] = true
                    }
                },
                ["remembered"] = new List<object?>(),
                ["intent"] = null
            });

            var log = new ProbeLog();
            var host = new JsHost(root, ProcessSide.Client, log, bindingFilter: type => type == typeof(AiBindings));
            try
            {
                host.Load();
                Assert(!host.HasErrors, string.Join(" | ", host.Errors));
                Assert(host.BindingNamespaces.Contains("ai"), "ai binding missing: " + string.Join(",", host.BindingNamespaces));

                var first = host.DispatchTo("probe", "step", "{}") ?? "null";
                Assert(first.Contains("\"status\":\"running\""), "first tick: " + first);

                var second = host.DispatchTo("probe", "step", "{}") ?? "null";
                Assert(second.Contains("\"status\":\"success\""), "second tick: " + second);

                var state = host.DispatchTo("probe", "snapshot", "{}") ?? "null";
                Assert(state.Contains("\"attempts\":2") && state.Contains("\"target\":\"found:3\""),
                    "state: " + state + " | errors: " + string.Join(" || ", log.Errors));

                var execs = host.DispatchTo("probe", "execs", "{}") ?? "null";
                Assert(execs.Contains("start:true:0.1") && execs.Contains("end:true:0.1"), "exec hooks: " + execs);

                var updated = host.DispatchTo("probe", "setExtra", "9") ?? "null";
                Assert(updated.Contains("\"extra\":9"), "set: " + updated);

                Assert(host.DispatchTo("probe", "status", "{}") == "\"success\"", "status after success");

                var child = host.DispatchTo("probe", "childTick", "{}") ?? "null";
                Assert(child.Contains("\"status\":\"success\""), "import tick: " + child);
                var childCalls = host.DispatchTo("probe", "childCalls", "{}");
                Assert(childCalls == "1", "imported action count: " + childCalls);

                var cancelled = host.DispatchTo("probe", "cancel", "{}");
                Assert(cancelled == "\"cancelled\"", "cancel status: " + cancelled);
                var reset = host.DispatchTo("probe", "reset", "{}");
                Assert(reset == "\"running\"", "reset status: " + reset);
                var afterReset = host.DispatchTo("probe", "step", "{}") ?? "null";
                Assert(afterReset.Contains("\"status\":\"running\""), "reset tick: " + afterReset);
                var afterResetState = host.DispatchTo("probe", "snapshot", "{}") ?? "null";
                Assert(afterResetState.Contains("\"attempts\":1"), "reset restored state: " + afterResetState);

                var senses = host.DispatchTo("probe", "sensesTick", "{}") ?? "null";
                Assert(senses.Contains("\"status\":\"success\""), "senses tick: " + senses);
                Assert(host.DispatchTo("probe", "spotted", "{}") == "true", "senses condition");

                var errors = host.DispatchTo("probe", "errors", "{}") ?? "null";
                Assert(errors.Contains("unknown key") && errors.Contains("does not exist") &&
                       errors.Contains("was not supplied") && errors.Contains("Circular"), "errors: " + errors);
                Assert(host.DispatchTo("probe", "warn", "{}") == "1", "warn callback count");

                var unloaded = host.DispatchTo("probe", "unload", "{}") ?? "null";
                Assert(unloaded.Contains("\"removed\":true") && unloaded.Contains("does not exist"), "unload: " + unloaded);

                host.Reload();
                Assert(!host.HasErrors, "reload errors: " + string.Join(" | ", host.Errors));
                var reloaded = host.DispatchTo("probe", "step", "{}") ?? "null";
                Assert(reloaded.Contains("\"status\":\"running\""), "tick after reload: " + reloaded);

                Console.WriteLine("PASS: ai binding compiles trees, bridges callbacks, shares state and isolates plugins");
                return 0;
            }
            finally
            {
                host.Dispose();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: ai probe: " + exception);
            return 1;
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("valheimjs-ai-", StringComparison.Ordinal))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void CheckPerceptionModel()
    {
        var player = new NpcSensedEntity
        {
            Id = "player1",
            Name = "Bjorn",
            Prefab = "Player",
            Faction = "Players",
            X = 10d,
            Z = 0d,
            InRange = true,
            Visible = true,
            IsPlayer = true,
            Enemy = true,
            Health = 100f,
            MaxHealth = 100f,
            FirstSeenAt = 0f,
            LastSeenAt = 0f,
            SeenCount = 2
        };
        var creature = new NpcSensedEntity
        {
            Id = "boar1",
            Name = "Boar",
            Prefab = "Boar",
            Faction = "AnimalsVeg",
            X = 0d,
            Z = 10d,
            InRange = true,
            Visible = false,
            IsPlayer = false,
            Enemy = true,
            FirstSeenAt = 1f
        };
        var lost = new NpcSensedEntity
        {
            Id = "lost1",
            Name = "Lost",
            Prefab = "Dverger",
            X = 0d,
            Z = 20d,
            InRange = false,
            LastSeenAt = 2f,
            FirstSeenAt = 0f
        };

        var north = new NpcSenseQuery
        {
            DirectionX = 0d,
            DirectionZ = 1d,
            Angle = 30f,
            Kinds = new[] { "creature" }
        };
        Assert(north.Allows(creature, 0d, 0d, 0d, 1d), "direction/kind filter includes the boar");
        Assert(!north.Allows(player, 0d, 0d, 0d, 1d), "direction/kind filter excludes the player");

        var remembered = new NpcSenseQuery { IncludeRemembered = true };
        Assert(remembered.Allows(lost, 0d, 0d, 0d, 1d), "remembered entries are queryable");
        Assert(!new NpcSenseQuery().Allows(lost, 0d, 0d, 0d, 1d), "lost entries are excluded by default");

        var visible = new NpcSenseQuery { VisibleOnly = true };
        Assert(visible.Allows(player, 0d, 0d, 0d, 1d) && !visible.Allows(creature, 0d, 0d, 0d, 1d),
            "visible filter");

        var chest = new NpcSensedEntity
        {
            Id = "chest1",
            Name = "Chest",
            Kind = "container",
            IsObject = true,
            InRange = true,
            Visible = true,
            X = 2d,
            FirstSeenAt = 0f,
            LastSeenAt = 0f
        };
        Assert(new NpcSenseQuery { Kinds = new[] { "container" } }.Allows(chest, 0d, 0d, 0d, 1d),
            "object class filter includes the chest");
        Assert(!new NpcSenseQuery { PlayersOnly = true }.Allows(chest, 0d, 0d, 0d, 1d),
            "object is not a player");

        var plain = player.ToPlain(0d, 0d, 0d, 0f, 5f);
        Assert((string?)plain["name"] == "Bjorn", "sensed name");
        Assert((double)plain["distance"]! > 9.9d, "sensed distance");
        Assert((double)plain["lastSeen"]! == 5d, "sensed lastSeen");
        Assert((double)plain["firstSeen"]! == 5d, "sensed firstSeen");
        var rel = (Dictionary<string, object?>)plain["rel"]!;
        Assert((double)rel["x"]! == 10d, "sensed relative position");

        var perception = new NpcPerception
        {
            Id = "self",
            Kind = "wanderer",
            X = 0d,
            Y = 0d,
            Z = 0d,
            Nearby = new[] { player, creature },
            Remembered = new[] { lost }
        };
        var view = perception.ToPlain(0f, 5f);
        Assert(((List<object?>)view["nearby"]!).Count == 2, "perception nearby");
        Assert(((List<object?>)view["remembered"]!).Count == 1, "perception remembered");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("assertion failed: " + message);
        }
    }

    private sealed class ProbeLog : IJsLog
    {
        internal List<string> Errors { get; } = new();

        public void Write(string level, string plugin, string message)
        {
            if (level == "error")
            {
                Errors.Add(message);
            }
        }
    }
}
