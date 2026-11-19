using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ValheimJS.AI;
using ValheimJS.Bindings;
using ValheimJS.Core;
using ValheimJS.Scripting;

internal static class PlanningProbe
{
    private static int _nativeCalls;
    private static string _nativeMode = "success";

    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-planning-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "plugins", "probe");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.ini"), "name=probe\napiVersion=1\n");
            File.WriteAllText(Path.Combine(directory, "shared.js"), Fixture());

            var other = Path.Combine(root, "plugins", "other");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "manifest.ini"), "name=other\napiVersion=1\ndepends=probe\n");
            File.WriteAllText(Path.Combine(other, "shared.js"), """
                (function () {
                    var ctx = globalThis.__vjs_context;
                    var api = ctx.host.api.planning;
                    globalThis.__vjs_plugin = { __vjsPlugin: "object", value: {
                        read: function (_ctx, payload) {
                            try { api.planStatus(payload.id); return false; }
                            catch (error) { return true; }
                        },
                        readPlanner: function (_ctx, payload) {
                            try { api.plannerCurrent(payload.id); return false; }
                            catch (error) { return true; }
                        }
                    }};
                })();
                """);

            AiNative.Resolve = (_, _, _, name, args, _) =>
            {
                _nativeCalls++;
                if (_nativeMode == "pending" && _nativeCalls <= 2)
                {
                    return AiNativeResult.Pending();
                }

                if (_nativeMode == "paused")
                {
                    return AiNativeResult.Pending(paused: true);
                }

                if (_nativeMode == "fail")
                {
                    return AiNativeResult.Fail("boom");
                }

                if (name == "moveTo" && args.GetValueOrDefault("target") is not IReadOnlyDictionary<string, object?>)
                {
                    return AiNativeResult.Fail("target-missing");
                }

                return AiNativeResult.Ok();
            };

            var log = new ProbeLog();
            var host = new JsHost(root, ProcessSide.Server, log,
                bindingFilter: type => type == typeof(PlanningBindings));
            try
            {
                host.Load();
                Assert(!host.HasErrors, string.Join(" | ", host.Errors));
                Assert(host.BindingNamespaces.Contains("planning"), "planning binding missing");

                _nativeMode = "pending";
                _nativeCalls = 0;
                var id = host.DispatchTo("probe", "native", "{}") ?? "null";
                Assert(id.StartsWith("\"plan"), "native plan id: " + id);
                var firstTick = Tick(host, id, 0);
                Assert(firstTick == "running:0:", "native first tick: " + firstTick);
                Assert(Tick(host, id, 1) == "running:0:", "native pending tick");
                Assert(Tick(host, id, 2) == "running:1:", "native advance");
                Assert(Tick(host, id, 3) == "success:2:", "native completion");
                Assert(Tick(host, id, 4) == "success:2:", "terminal immutable");

                _nativeMode = "fail";
                _nativeCalls = 0;
                var retry = host.DispatchTo("probe", "retry", "{}") ?? "null";
                Assert(Tick(host, retry, 0) == "running:0:", "retry holds");
                Assert(Tick(host, retry, 1) == "failure:0:boom", "retry exhausted");

                _nativeMode = "success";
                var planTimeout = host.DispatchTo("probe", "planTimeout", "{}") ?? "null";
                var timeoutHold = Tick(host, planTimeout, 0);
                Assert(timeoutHold == "running:0:", "plan timeout holds: " + timeoutHold);
                Assert(Tick(host, planTimeout, 10) == "failure:0:plan-timeout", "plan timeout");

                var stepTimeout = host.DispatchTo("probe", "stepTimeout", "{}") ?? "null";
                Assert(Tick(host, stepTimeout, 0) == "running:0:", "step timeout holds");
                Assert(Tick(host, stepTimeout, 10) == "failure:0:step-timeout", "step timeout");

                _nativeMode = "paused";
                _nativeCalls = 0;
                var paused = host.DispatchTo("probe", "paused", "{}") ?? "null";
                Assert(Tick(host, paused, 0) == "running:0:", "paused holds");
                Assert(Tick(host, paused, 10) == "failure:0:paused", "pause grace");

                var js = host.DispatchTo("probe", "jsSteps", "{}") ?? "null";
                var jsFirst = Tick(host, js, 0);
                Assert(jsFirst == "running:1:", "js step one: " + jsFirst);
                var jsSecond = Tick(host, js, 1);
                Assert(jsSecond == "success:2:", "js state and step two: " + jsSecond);

                var unknown = host.DispatchTo("probe", "unknown", "{}") ?? "null";
                Assert(Tick(host, unknown, 0) == "failure:0:unknown-action", "unknown action");

                var cancelled = host.DispatchTo("probe", "cancelled", "{}") ?? "null";
                Assert(host.DispatchTo("probe", "cancel", JsonSerializer.Serialize(new { id = JsonSerializer.Deserialize<string>(cancelled) })) == "true", "cancel");
                Assert(Tick(host, cancelled, 0) == "cancelled:0:plugin-cancel", "cancel immutable");

                _nativeMode = "success";
                var released = host.DispatchTo("probe", "release", "{}") ?? "null";
                Assert(host.DispatchTo("probe", "cancel", JsonSerializer.Serialize(new { id = JsonSerializer.Deserialize<string>(released) })) == "false", "release drops plan");

                var basic = Text(host, "plannerBasic");
                Assert(basic == "high:low,high", "planner basic: " + basic);

                var policy = Text(host, "plannerPolicy");
                Assert(policy == "low", "planner policy: " + policy);

                var order = Text(host, "plannerOrder");
                Assert(order == "work:null:enter,tick,exit:success", "planner order: " + order);

                var cooldown = Text(host, "plannerCooldown");
                Assert(cooldown == "work:null:work", "planner cooldown: " + cooldown);

                var preempt = Text(host, "plannerPreempt");
                Assert(preempt == "work:challenger", "planner preempt: " + preempt);

                var request = Text(host, "plannerRequest");
                Assert(request == "manual", "planner request: " + request);

                var planned = Text(host, "plannerPlan");
                Assert(planned == "work:work:work", "planner plan: " + planned);

                var plannerScoped = host.DispatchTo("probe", "plannerScope", "{}") ?? "null";
                var plannerPayload = JsonSerializer.Serialize(new { id = JsonSerializer.Deserialize<string>(plannerScoped) });
                Assert(host.DispatchTo("other", "readPlanner", plannerPayload) == "true",
                    "other plugin cannot access planner: " + plannerScoped);

                var fallback = Text(host, "utilityFallback");
                Assert(fallback == "fallback", "utility fallback: " + fallback + " errors=" + string.Join(" | ", log.Errors));

                var describe = Text(host, "utilityDescribe");
                Assert(describe == "described:0", "utility describe: " + describe);

                var available = Text(host, "utilityAvailable");
                Assert(available == "inspect", "utility available: " + available);

                var stats = Text(host, "utilityStats");
                Assert(stats == "0.8:1:0", "utility stats: " + stats);

                var feedback = Text(host, "utilityFeedback");
                Assert(feedback == "hunt:hunt:1:20:1:0.23:0.23", "utility feedback: " + feedback);

                var followPlan = host.DispatchTo("probe", "followCreate", "{}") ?? "null";
                var followId = JsonSerializer.Deserialize<string>(followPlan)!;
                Assert(FollowTick(host, followId, 10, 0, 0) == "running:0:", "follow starts");
                Assert(FollowTick(host, followId, 2, 0, 1000) == "running:0:", "follow within range waits for stop");
                Assert(FollowTick(host, followId, 2, 0, 1200) == "running:0:", "follow stop grace holds");
                Assert(FollowTick(host, followId, 2, 0, 1300) == "success:1:", "follow completes when stationary");

                var behaviourState = Text(host, "behaviourState");
                Assert(behaviourState == "18", "behaviour state args: " + behaviourState);

                var behaviour = Text(host, "behaviourGoal");
                Assert(behaviour == "work:1:success", "behaviour goal: " + behaviour);

                var utilityPlan = Text(host, "utilityPlan");
                Assert(utilityPlan == "work:work:success", "utility plan: " + utilityPlan);

                var prereq = Text(host, "utilityPrereq");
                Assert(prereq == "gather-wood:gather-wood:cook:gather-wood,cook",
                    "utility prerequisite: " + prereq);

                var scoped = host.DispatchTo("probe", "scope", "{}") ?? "null";
                var scopedPayload = JsonSerializer.Serialize(new { id = JsonSerializer.Deserialize<string>(scoped) });
                Assert(host.DispatchTo("other", "read", scopedPayload) == "true", "other plugin cannot access plan: " + scoped);

                var noResolver = host.DispatchTo("probe", "nullResolver", "{}") ?? "null";
                AiNative.Resolve = null;
                var noResolverTick = Tick(host, noResolver, 0);
                Assert(noResolverTick == "failure:0:no-native-resolver", "missing resolver: " + noResolverTick);

                Assert(log.Errors.Count == 0, string.Join(" | ", log.Errors));
                Console.WriteLine("PASS: native plans, planners, utility selection, feedback and scoping");
                return 0;
            }
            finally
            {
                host.Dispose();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: planning probe: " + exception);
            return 1;
        }
        finally
        {
            AiNative.Resolve = null;
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("valheimjs-planning-", StringComparison.Ordinal))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static string FollowTick(JsHost host, string id, double x, double z, double now)
    {
        var payload = JsonSerializer.Serialize(new { x, z, now });
        var answer = host.DispatchTo("probe", "followTick", payload);
        return answer == null ? "null" : JsonSerializer.Deserialize<string>(answer) ?? answer;
    }

    private static string Text(JsHost host, string hook)
    {
        var answer = host.DispatchTo("probe", hook, "{}");
        return answer == null ? "null" : JsonSerializer.Deserialize<string>(answer) ?? answer;
    }

    private static string Tick(JsHost host, string id, double now)
    {
        var planId = JsonSerializer.Deserialize<string>(id)
            ?? throw new InvalidOperationException("plan id did not decode: " + id);
        var payload = JsonSerializer.Serialize(new { id = planId, now });
        var answer = host.DispatchTo("probe", "tick", payload);
        return answer == null ? "null" : JsonSerializer.Deserialize<string>(answer) ?? answer;
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
                Errors.Add(plugin + ": " + message);
            }
        }
    }

    private static string Fixture() => """
        (function () {
            var ctx = globalThis.__vjs_context;
            var api = ctx.host.api.planning;

            function view() {
                return { id: "npc", action: null };
            }

            function dispatch(_kind, step, current, now, state, local) {
                if (typeof step.call === "function") {
                    return step.call({ view: current, now: now, state: state, local: local });
                }
                if (typeof step.until === "function") {
                    return step.until({ view: current, now: now, state: state, local: local })
                        ? "success"
                        : "running";
                }
                if (typeof step.equip === "string" || typeof step.craft === "string") {
                    state.inventory = step.equip || step.craft;
                    return "success";
                }
                return { status: "failure", reason: "unhandled" };
            }

            function create(steps, options) {
                return api.planCreate({}, steps, options || {}, dispatch);
            }

            function utilityContext(utilityId, goalId, current, now, planStatus, planReason) {
                return {
                    view: current, actor: {}, now: now, goal: goalId,
                    planStatus: planStatus, planReason: planReason,
                    log: function () {},
                    moveTo: function () { return { ok: true }; },
                    wait: function () { return { ok: true }; },
                    face: function () { return { ok: true }; },
                    pick: function () { return { ok: true }; },
                    interact: function () { return { ok: true, reason: null, verb: "", count: 0 }; },
                    affordances: function (id) {
                        return { id: id, kind: "", name: "", hoverName: null, hoverText: null,
                            supported: false, playerOnly: false, reason: "unsupported", verbs: [],
                            gives: [], requires: [], toolTier: 0, distance: 0, inRange: false };
                    },
                    run: function (steps, options) {
                        var planId = api.planCreate({}, steps, options || {}, dispatch);
                        api.utilitySetPlan(utilityId, planId);
                        return { id: planId };
                    },
                    cancelPlan: function () { api.utilitySetPlan(utilityId, null); },
                    sense: function () { return null; }
                };
            }

            function makeUtility(goals, options) {
                options = options || {};
                var byId = {};
                goals.forEach(function (goal) { byId[goal.id] = goal; });
                var utilityId = null;
                var saved = null;
                function invoke(kind, a, b, c, d, e, f) {
                    var goal = byId[a];
                    switch (kind) {
                        case "score": return goal.score(b);
                        case "eligible": return goal.eligible ? goal.eligible(b) : true;
                        case "describe": return goal.describe
                            ? (goal.describe({ view: b, now: c, stats: d }) || {})
                            : null;
                        case "consideration": {
                            var fn = (options.considerations || {})[a];
                            if (!fn) return 0;
                            var description = c || {};
                            return fn({
                                goal: byId[b], description: description, factors: description.factors || {},
                                view: d, now: e, stats: f
                            });
                        }
                        case "enter": return goal.enter
                            ? goal.enter(utilityContext(utilityId, a, b, c, e, f)) : undefined;
                        case "tick": return goal.tick(utilityContext(utilityId, a, b, c, e, f));
                        case "exit": return goal.exit
                            ? goal.exit(utilityContext(utilityId, a, b, c, e, f), d) : undefined;
                        case "feedback": {
                            var rule = (options.feedback || []).find(function (item) { return item.id === a; });
                            return rule ? rule.adjust(b) : {};
                        }
                        case "snapshot":
                            return options.snapshot
                                ? options.snapshot(utilityContext(utilityId, a, b, c, d, e)) : {};
                        case "random": return options.random ? options.random() : 0.5;
                        case "save": saved = a; return undefined;
                        case "debug": if (options.debug) options.debug(a); return undefined;
                        case "log": if (options.log) options.log(a); return undefined;
                    }
                }
                utilityId = api.utilityCreate({}, options, invoke);
                api.utilitySetGoals(utilityId, goals.map(function (goal) {
                    return { id: goal.id, hasTick: typeof goal.tick === "function",
                        cooldownMs: goal.cooldownMs, hysteresis: goal.hysteresis,
                        commitMs: goal.commitMs, urgent: goal.urgent, behaviour: goal.behaviour };
                }));
                return { id: utilityId, saved: function () { return saved; }, byId: byId };
            }


            function goalContext(plannerId, goalId, current, now, planStatus, planReason, stepIndex) {
                return {
                    view: current,
                    actor: {},
                    now: now,
                    goal: goalId,
                    planStatus: planStatus,
                    planReason: planReason,
                    log: function () {},
                    moveTo: function () { return { ok: true }; },
                    wait: function () { return { ok: true }; },
                    face: function () { return { ok: true }; },
                    pick: function () { return { ok: true }; },
                    interact: function () { return { ok: true, reason: null, verb: "", count: 0 }; },
                    affordances: function (id) {
                        return { id: id, kind: "", name: "", hoverName: null, hoverText: null,
                            supported: false, playerOnly: false, reason: "unsupported", verbs: [],
                            gives: [], requires: [], toolTier: 0, distance: 0, inRange: false };
                    },
                    run: function (steps, options) {
                        var planId = api.planCreate({}, steps, options || {}, dispatch);
                        api.plannerSetPlan(plannerId, planId);
                        return { id: planId };
                    },
                    cancelPlan: function () { api.plannerSetPlan(plannerId, null); },
                    sense: function () { return null; }
                };
            }

            function makePlanner(goals, options) {
                var byId = {};
                goals.forEach(function (goal) { byId[goal.id] = goal; });
                var plannerId = null;
                function invoke(kind, a, b, c, d, e, f) {
                    var goal = byId[a];
                    switch (kind) {
                        case "score": return goal.score(b);
                        case "eligible": return goal.eligible ? goal.eligible(b) : true;
                        case "enter": return goal.enter ? goal.enter(goalContext(plannerId, a, b, c, e, f)) : undefined;
                        case "tick": return goal.tick(goalContext(plannerId, a, b, c, e, f));
                        case "exit": return goal.exit ? goal.exit(goalContext(plannerId, a, b, c, e, f), d) : undefined;
                        case "select": return options.select ? options.select(a, b, c) : null;
                        case "debug": if (options.debug) options.debug(a); return undefined;
                        case "log": if (options.log) options.log(a); return undefined;
                    }
                }
                plannerId = api.plannerCreate({}, options || {}, invoke);
                api.plannerSetGoals(plannerId, goals.map(function (goal) {
                    return { id: goal.id, hasTick: typeof goal.tick === "function",
                        cooldownMs: goal.cooldownMs, hysteresis: goal.hysteresis,
                        commitMs: goal.commitMs, urgent: goal.urgent, behaviour: goal.behaviour };
                }));
                return plannerId;
            }

            globalThis.__vjs_plugin = { __vjsPlugin: "object", value: {
                native: function () {
                    return create([
                        { moveTo: { x: 1, y: 0, z: 2 }, run: true },
                        { interact: { target: "2:2", verb: "pickup", approach: false } }
                    ]);
                },
                followCreate: function () {
                    ctx.state.followTarget = {
                        id: "follow-target",
                        pos: { x: 10, y: 0, z: 0 },
                        distance: 10
                    };
                    ctx.state.followPlan = create([{ follow: { target: "follow-target", distance: 3 } }]);
                    return ctx.state.followPlan;
                },
                followTick: function (_ctx, payload) {
                    var target = ctx.state.followTarget;
                    target.pos.x = payload.x;
                    target.pos.z = payload.z;
                    target.distance = Math.hypot(target.pos.x, target.pos.z);
                    var current = view();
                    current.objects = [target];
                    var result = api.planTick(ctx.state.followPlan, current, payload.now);
                    return result.status + ":" + result.index + ":" + (result.reason || "");
                },
                retry: function () {
                    return create([
                        { moveTo: { x: 1, y: 0, z: 0 }, retry: { attempts: 1 } }
                    ]);
                },
                planTimeout: function () {
                    return create([{ until: function () { return false; } }], { timeoutMs: 5 });
                },
                stepTimeout: function () {
                    return create([{ call: function () { return "running"; }, timeoutMs: 5 }]);
                },
                paused: function () {
                    return create([{ moveTo: { x: 1, y: 0, z: 0 } }], { pauseGraceMs: 5 });
                },
                jsSteps: function () {
                    return create([
                        {
                            call: function (c) {
                                c.state.calls = (c.state.calls || 0) + 1;
                                return "success";
                            }
                        },
                        {
                            call: function (c) {
                                if (c.state.calls !== 1) throw Error("state did not persist");
                                c.local.seen = true;
                                return c.local.seen ? "success" : "failure";
                            }
                        }
                    ]);
                },
                unknown: function () {
                    return create([{ nope: true }]);
                },
                cancelled: function () {
                    return create([{ until: function () { return false; } }]);
                },
                release: function () {
                    var id = create([{ until: function () { return false; } }]);
                    api.planRelease(id);
                    return id;
                },
                nullResolver: function () {
                    return create([{ moveTo: { x: 1, y: 0, z: 0 } }]);
                },
                tick: function (_ctx, payload) {
                    var result = api.planTick(payload.id, view(), payload.now);
                    return result.status + ":" + result.index + ":" + (result.reason || "");
                },
                cancel: function (_ctx, payload) {
                    try {
                        api.planCancel(payload.id, "plugin-cancel");
                        return true;
                    } catch (error) {
                        return false;
                    }
                },

                plannerBasic: function () {
                    var id = makePlanner([
                        { id: "low", score: function () { return 1; }, tick: function () { return "running"; } },
                        { id: "blocked", score: function () { return 100; }, eligible: function () { return false; },
                            tick: function () { return "running"; } },
                        { id: "high", score: function () { return 5; }, tick: function () { return "running"; } }
                    ], {});
                    var active = api.plannerTick(id, view(), 0);
                    var candidates = api.plannerCandidates(id, view(), 0)
                        .map(function (candidate) { return candidate.goal; }).join(",");
                    return active + ":" + candidates;
                },
                plannerPolicy: function () {
                    var id = makePlanner([
                        { id: "high", score: function () { return 10; }, tick: function () { return "running"; } },
                        { id: "low", score: function () { return 1; }, tick: function () { return "running"; } }
                    ], { select: function (candidates) {
                        var low = candidates.find(function (candidate) { return candidate.goal === "low"; });
                        return low ? low.goal : null;
                    }});
                    return api.plannerTick(id, view(), 0);
                },
                plannerOrder: function () {
                    var events = [];
                    var id = makePlanner([{
                        id: "work",
                        score: function () { return 5; },
                        cooldownMs: 1000,
                        enter: function () { events.push("enter"); },
                        tick: function () { events.push("tick"); return "success"; },
                        exit: function (_ctx, reason) { events.push("exit:" + reason); }
                    }], {});
                    var first = api.plannerTick(id, view(), 0);
                    var second = api.plannerTick(id, view(), 1);
                    return first + ":" + second + ":" + events.join(",");
                },
                plannerCooldown: function () {
                    var id = makePlanner([{
                        id: "work", score: function () { return 5; }, cooldownMs: 100,
                        tick: function () { return "success"; }
                    }], {});
                    var first = api.plannerTick(id, view(), 0);
                    var second = api.plannerTick(id, view(), 50);
                    var third = api.plannerTick(id, view(), 101);
                    return first + ":" + second + ":" + third;
                },
                plannerPreempt: function () {
                    var score = 0;
                    var id = makePlanner([
                        { id: "work", score: function () { return 5; }, hysteresis: 2,
                            tick: function () { return "running"; } },
                        { id: "challenger", score: function () { return score; },
                            tick: function () { return "running"; } }
                    ], { replanMs: 0 });
                    var first = api.plannerTick(id, view(), 0);
                    score = 9;
                    var second = api.plannerTick(id, view(), 0);
                    return first + ":" + second;
                },
                plannerRequest: function () {
                    var id = makePlanner([
                        { id: "high", score: function () { return 10; }, tick: function () { return "running"; } },
                        { id: "manual", score: function () { return 1; }, tick: function () { return "running"; } }
                    ], {});
                    api.plannerRequest(id, "manual", 1000);
                    return api.plannerTick(id, view(), 0);
                },
                plannerPlan: function () {
                    var ready = false;
                    var id = makePlanner([{
                        id: "work",
                        score: function () { return 5; },
                        enter: function (ctx) {
                            ctx.run([{ until: function () { return ready; } }]);
                        },
                        tick: function (ctx) { return ctx.planStatus === "success" ? "success" : "running"; }
                    }], {});
                    var first = api.plannerTick(id, view(), 0);
                    ready = true;
                    var second = api.plannerTick(id, view(), 1);
                    var third = api.plannerTick(id, view(), 2);
                    return first + ":" + second + ":" + (third || "idle");
                },
                plannerScope: function () {
                    return makePlanner([{
                        id: "work", score: function () { return 1; }, tick: function () { return "running"; }
                    }], {});
                },

                utilityFallback: function () {
                    var u = makeUtility([{
                        id: "fallback", score: function () { return 5; },
                        tick: function () { return "success"; }
                    }]);
                    return api.utilityTick(u.id, view(), 0);
                },
                utilityDescribe: function () {
                    var scoreCalls = 0;
                    var u = makeUtility([{
                        id: "described",
                        score: function () { scoreCalls++; return 999; },
                        describe: function () { return { base: 1, factors: { curiosity: 1 } }; },
                        tick: function () { return "success"; }
                    }], {
                        stats: { Curiosity: 1, Predictability: 1 },
                        considerations: {
                            curiosity: function (c) { return (c.factors.curiosity || 0) * c.stats.Curiosity * 10; }
                        }
                    });
                    var active = api.utilityTick(u.id, view(), 0);
                    return active + ":" + scoreCalls;
                },
                utilityAvailable: function () {
                    var u = makeUtility([
                        { id: "inspect", describe: function () { return { base: 1 }; },
                            tick: function () { return "success"; } },
                        { id: "blocked", describe: function () { return { base: 100 }; },
                            eligible: function () { return false; }, tick: function () { return "success"; } }
                    ], { stats: { Predictability: 1 } });
                    return api.utilityAvailable(u.id, view(), 0)
                        .map(function (candidate) { return candidate.goal; }).join(",");
                },
                utilityStats: function () {
                    var u = makeUtility([], { stats: { Explorer: 0.8 } });
                    var before = api.utilityStats(u.id).Explorer;
                    api.utilitySetStats(u.id, { Explorer: 1.7, Hunter: -1 });
                    var after = api.utilityStats(u.id);
                    return before + ":" + after.Explorer + ":" + after.Hunter;
                },
                utilityFeedback: function () {
                    var ticks = 0;
                    var food = 0;
                    var u = makeUtility([{
                        id: "hunt",
                        describe: function () { return { base: 10 }; },
                        tick: function () { return ++ticks === 1 ? "running" : "success"; }
                    }], {
                        stats: { Predictability: 1 },
                        maxStep: 0.02,
                        snapshot: function () { return { food: food }; },
                        feedback: [{
                            id: "wilderness",
                            adjust: function (context) {
                                var away = context.events.some(function (event) {
                                    return event.name === "damaged" && (event.observed.awayFromHome || 0) >= 10;
                                });
                                return away ? { Explorer: -0.02 } : {};
                            }
                        }]
                    });
                    var away = view(); away.x = 20;
                    var home = view();
                    var first = api.utilityTick(u.id, away, 0);
                    api.utilityRecord(u.id, "damaged", { damage: 10 }, away);
                    food = 1;
                    var second = api.utilityTick(u.id, home, 1);
                    var outcome = api.utilityLastOutcome(u.id);
                    var stats = api.utilityStats(u.id);
                    return first + ":" + second + ":" + outcome.events.length + ":" +
                        outcome.events[0].observed.awayFromHome + ":" + outcome.observed.food + ":" +
                        stats.Explorer.toFixed(2) + ":" + u.saved().Explorer.toFixed(2);
                },
                behaviourState: function () {
                    var seen = null;
                    var tree = api.behaviourCompile([
                        "imports:",
                        "  wait: ./wait.yaml",
                        "root:",
                        "  use: wait"
                    ].join("\n"), {
                        imports: {
                            wait: [
                                "root:",
                                "  action: probe",
                                "  args:",
                                "    seconds: \"@[waitSeconds]\""
                            ].join("\n")
                        }
                    });
                    var u = makeUtility([{
                        id: "work",
                        describe: function () { return { base: 10 }; },
                        behaviour: {
                            tree: tree,
                            state: function () { return { waitSeconds: 18 }; },
                            action: function (_name, context) { seen = context.args.seconds; return "success"; }
                        }
                    }], { stats: { Predictability: 1 } });
                    api.utilityTick(u.id, view(), 0);
                    return String(seen);
                },
                behaviourGoal: function () {
                    var calls = 0;
                    var tree = api.behaviourCompile("root:\n  action: mark\n");
                    var u = makeUtility([{
                        id: "work",
                        describe: function () { return { base: 10 }; },
                        behaviour: {
                            tree: tree,
                            action: function (name) { calls++; return "success"; }
                        }
                    }], { stats: { Predictability: 1 } });
                    var first = api.utilityTick(u.id, view(), 0);
                    var outcome = api.utilityLastOutcome(u.id);
                    return first + ":" + calls + ":" + (outcome ? outcome.status : "none");
                },
                utilityPlan: function () {
                    var ready = false;
                    var u = makeUtility([{
                        id: "work",
                        score: function () { return 5; },
                        enter: function (ctx) { ctx.run([{ until: function () { return ready; } }]); },
                        tick: function (ctx) {
                            return ctx.planStatus === "success" ? "success" : "running";
                        }
                    }], { stats: { Predictability: 1 } });
                    var first = api.utilityTick(u.id, view(), 0);
                    ready = true;
                    var second = api.utilityTick(u.id, view(), 1);
                    var outcome = api.utilityLastOutcome(u.id);
                    return first + ":" + second + ":" + (outcome ? outcome.planStatus : "none");
                },
                utilityPrereq: function () {
                    var wood = false;
                    var selected = [];
                    var u = makeUtility([
                        { id: "cook", describe: function () { return { base: 10, requires: wood ? [] : ["wood"] }; },
                            enter: function () { selected.push("cook"); }, tick: function () { return "success"; } },
                        { id: "gather-wood", describe: function () { return { base: 1, provides: ["wood"] }; },
                            enter: function () { selected.push("gather-wood"); },
                            tick: function () { wood = true; return "success"; } }
                    ], { stats: { Predictability: 1 } });
                    var available = api.utilityAvailable(u.id, view(), 0)
                        .map(function (candidate) { return candidate.goal; }).join(",");
                    var first = api.utilityTick(u.id, view(), 0);
                    var second = api.utilityTick(u.id, view(), 1);
                    return available + ":" + first + ":" + second + ":" + selected.join(",");
                },
                scope: function () {
                    return create([{ until: function () { return false; } }]);
                }
            }};
        })();
        """;
}
