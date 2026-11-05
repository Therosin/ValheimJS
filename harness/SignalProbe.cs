using ValheimJS.Bindings;
using ValheimJS.Scripting;
using ValheimJS.Utils;

internal static class SignalProbe
{
    internal static int Run()
    {
        try
        {
            CheckObserver();
            CheckFailures();
            CheckBinding();
            Console.WriteLine("PASS: condition observer semantics and the signals binding");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: signals probe: " + exception);
            return 1;
        }
    }

    private static void CheckObserver()
    {
        var events = new List<string>();
        var value = false;
        var log = new List<string>();
        using var observer = new ConditionObserver(100, log.Add);
        observer.Observe("flag", () => value);
        observer.On("flag:Enter", () => events.Add("enter"));
        observer.On("flag:Exit", () => events.Add("exit"));
        observer.On("flag", () => events.Add("active"));
        observer.Once("flag:Enter", () => events.Add("once"));

        Assert(observer.Advance(50) == 0, "evaluated before the interval");
        value = true;
        Assert(observer.Advance(50) == 2, "rise dispatches enter and the one-shot");
        Assert(events.SequenceEqual(new[] { "enter", "once" }), "rise event order");
        Assert(observer.HandlerCount("flag:Enter") == 1, "once subscription survived its invocation");
        Assert(observer.Changed("flag"), "changed flag after the rise");
        Assert(observer.Advance(100) == 1 && events[^1] == "active", "active tick while true");
        Assert(!observer.Changed("flag"), "changed flag persisted after an unchanged sample");

        value = false;
        Assert(observer.Advance(100) == 1 && events[^1] == "exit", "fall dispatches exit");
        Assert(!observer.Is("flag"), "value after exit");
        Assert(observer.Snapshot()["flag"] == false, "snapshot value");

        observer.Reset("flag");
        Assert(!observer.Changed("flag"), "reset kept the dirty flag");
        Assert(observer.Remove("flag") && !observer.Has("flag"), "remove");
        Assert(observer.Count == 0, "count after remove");
    }

    private static void CheckFailures()
    {
        var log = new List<string>();
        var order = new List<string>();
        var attempts = 0;
        var observer = new ConditionObserver(100, log.Add);
        observer.Observe("ok", () => true);
        observer.Observe("boom", () =>
        {
            attempts++;
            throw new InvalidOperationException("predicate exploded");
        });
        observer.On("ok:Enter", () => throw new InvalidOperationException("handler exploded"));
        observer.On("ok:Enter", () => order.Add("second"));

        observer.Advance(100);
        Assert(attempts == 1 && order.SequenceEqual(new[] { "second" }), "throwing handler stopped the next handler");
        Assert(log.Count(message => message.Contains("condition 'boom' threw")) == 1, "predicate failure was not reported");
        Assert(log.Count(message => message.Contains("handler threw")) == 1, "handler failure was not reported");
        Assert(observer.Is("ok"), "handler failure changed the observed value");

        observer.Advance(100);
        Assert(log.Count(message => message.Contains("condition 'boom' threw")) == 1, "repeated predicate failures spammed the log");

        Throws(() => new ConditionObserver(0), "invalid interval");
        Throws(() => observer.Observe(" ", () => true), "blank condition name");
        observer.Dispose();
        Throws(() => observer.Advance(100), "use after dispose");
    }

    private static void CheckBinding()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-signals-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "plugins", "probe");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.ini"), "name=probe\napiVersion=1\n");
            File.WriteAllText(Path.Combine(directory, "shared.js"), """
                (function () {
                    var ctx = globalThis.__vjs_context;
                    var api = ctx.host.api.signals;
                    globalThis.__vjs_plugin = { __vjsPlugin:'object', value: {
                        setup: function () {
                            ctx.state.events = [];
                            ctx.state.flag = false;
                            ctx.state.id = api.create(50);
                            api.observe(ctx.state.id, 'flag', function () {
                                if (globalThis.__vjs_context !== ctx) throw Error('predicate lost context');
                                return ctx.state.flag === true;
                            });
                            api.on(ctx.state.id, 'flag:Enter', function () { ctx.state.events.push('enter'); });
                            api.on(ctx.state.id, 'flag:Exit', function () { ctx.state.events.push('exit'); });
                            api.on(ctx.state.id, 'flag', function () { ctx.state.events.push('active'); });
                            api.once(ctx.state.id, 'flag:Enter', function () { ctx.state.events.push('once'); });
                        },
                        flip: function (_ctx, value) { ctx.state.flag = value === true || value === 'true'; return ctx.state.flag; },
                        inspect: function () { return ctx.state.events; },
                        count: function () { return api.count(ctx.state.id); },
                        debug: function () {
                            return {
                                count: api.count(ctx.state.id),
                                interval: api.interval(ctx.state.id),
                                raw: ctx.state.flag,
                                sampled: api.is(ctx.state.id, 'flag'),
                                events: ctx.state.events
                            };
                        },
                        snapshot: function () { return api.snapshot(ctx.state.id); },
                        failing: function () {
                            var id = api.create(10);
                            api.observe(id, 'boom', function () { throw Error('predicate exploded'); });
                            api.observe(id, 'ok', function () { return true; });
                            api.on(id, 'ok:Enter', function () { throw Error('handler exploded'); });
                        }
                    }};
                })();
                """);

            var log = new ProbeLog();
            var host = new JsHost(root, ProcessSide.Client, log, bindingFilter: type => type == typeof(SignalBindings));
            try
            {
                host.Load();
                Assert(!host.HasErrors, string.Join(" | ", host.Errors));
                Assert(host.DispatchTo("probe", "count", "{}") == "1", "observer not created");

                host.DispatchTo("probe", "flip", "true");
                Assert(host.DispatchTo("probe", "debug", "{}")!.Contains("\"raw\":true"), "flip did not set the flag: " + host.DispatchTo("probe", "debug", "{}"));
                PumpUntil(host, () => Inspect(host).Contains("\"enter\""));
                var risen = Inspect(host);
                Assert(risen.Contains("\"enter\"") && risen.Contains("\"once\""), "rise events were " + risen);
                PumpTicks(host, 4);
                Assert(Inspect(host).Contains("\"active\""), "active tick missing: " + Inspect(host));

                host.DispatchTo("probe", "flip", "false");
                PumpUntil(host, () => Inspect(host).Contains("\"exit\""));
                var snapshot = host.DispatchTo("probe", "snapshot", "{}");
                Assert(snapshot != null, "snapshot hook returned null (failed=" + host.Plugins.First().Failed +
                    ", hooks=" + string.Join(",", host.Plugins.First().Hooks) + ")");
                Assert(snapshot!.Contains("\"flag\":false"), "snapshot state: " + snapshot);

                host.DispatchTo("probe", "failing", "{}");
                PumpTicks(host, 4);
                Assert(log.Errors.Any(error => error.Contains("condition 'boom' threw")), "predicate failure not isolated");
                Assert(log.Errors.Any(error => error.Contains("handler threw")), "handler failure not isolated");
                Assert(!host.HasErrors, "binding failure became a load error");

                host.Reload();
                Assert(!host.HasErrors && Inspect(host) == "[]", "reload did not rebuild the plugin state");
            }
            finally
            {
                host.Dispose();
            }
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("valheimjs-signals-", StringComparison.Ordinal))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static string Inspect(JsHost host) => host.DispatchTo("probe", "inspect", "{}") ?? "";

    private static void PumpUntil(JsHost host, Func<bool> condition)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            Thread.Sleep(20);
            host.Tick();
            if (condition())
            {
                return;
            }
        }

        throw new InvalidOperationException("condition never became true (events='" + Inspect(host) +
            "', debug=" + (host.DispatchTo("probe", "debug", "{}") ?? "null") +
            ", bindings=" + string.Join(",", host.BindingNamespaces) + ")");
    }

    private static void PumpTicks(JsHost host, int count)
    {
        for (var index = 0; index < count; index++)
        {
            Thread.Sleep(20);
            host.Tick();
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Throws(Action action, string message)
    {
        try
        {
            action();
        }
        catch
        {
            return;
        }

        throw new InvalidOperationException(message + " (no exception)");
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
