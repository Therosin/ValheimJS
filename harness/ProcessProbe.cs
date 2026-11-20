using ValheimJS.Scripting;
using ValheimJS.Scripting.Binding;

internal static class ProcessProbe
{
    private static int ServerConstructions;
    private static int ClientConstructions;
    [Shared] private sealed class Common { }
    [ServerOnly] private sealed class Server { public Server() { ServerConstructions++; } }
    [ClientOnly] private sealed class Client { public Client() { ClientConstructions++; } }
    private sealed class Missing { }
    [Shared, ServerOnly] private sealed class Conflicting { }

    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckPolicy();
            CheckSide(root, "server");
            CheckSide(root, "client");
            CheckFailures(root);
            CheckDiagnostics(root);
            CheckStorage(root);
            Console.WriteLine("PASS: process identity, metadata filtering, components, context, dependencies, rollback, diagnostics, storage and reload");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: process probe: " + exception);
            return 1;
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(full).StartsWith("valheimjs-process-", StringComparison.Ordinal))
                Directory.Delete(full, true);
        }
    }

    private static void CheckPolicy()
    {
        Assert(ProcessPolicy.Resolve("VALHEIM") == ProcessSide.Client, "client detection");
        Assert(ProcessPolicy.Resolve("valheim_server") == ProcessSide.Server, "server detection without batchmode");
        Assert(ProcessPolicy.Resolve("renamed", "server") == ProcessSide.Server, "server override");
        Assert(ProcessPolicy.Resolve("valheim_server", "client") == ProcessSide.Client, "client override");
        Throws(() => ProcessPolicy.Resolve("renamed"), "unknown executable");
        Throws(() => ProcessPolicy.Resolve("valheim", "invalid"), "invalid override");
        var types = new[] { typeof(Server), typeof(Client), typeof(Common) };
        Assert(ProcessPolicy.Select(types, ProcessSide.Server).SequenceEqual(new[] { typeof(Common), typeof(Server) }), "server modules/patch selection");
        Assert(ProcessPolicy.Select(types, ProcessSide.Client).SequenceEqual(new[] { typeof(Common), typeof(Client) }), "client modules/patch selection");
        Throws(() => ProcessPolicy.Select(new[] { typeof(Common), typeof(Missing) }, ProcessSide.Client), "missing annotation");
        Throws(() => ProcessPolicy.Select(new[] { typeof(Conflicting) }, ProcessSide.Client), "conflicting annotations");
        foreach (var type in ProcessPolicy.Select(types, ProcessSide.Client)) Activator.CreateInstance(type, true);
        Assert(ServerConstructions == 0 && ClientConstructions == 1, "inactive startup module constructed");

        ProbeServerBindings.Constructions = 0;
        using var context = new JsBindingContext(ProcessSide.Client, "data", "config", "plugins", "logs", name => name, name => name, (_, _) => { });
        using var installer = new JsBindingInstaller(JsBindingDiscovery.Discover(typeof(JsHost).Assembly, type => !BindingProbe.IsFixture(type)), context);
        Assert(ProbeServerBindings.Constructions == 0, "inactive binder constructor executed");
        Assert(!installer.InstalledNamespaces.Contains("probeServer"), "server binder exposed on client");
    }

    private static void CheckSide(string root, string side)
    {
        var scenario = Path.Combine(root, side);
        WritePlugin(scenario, "combined", new Dictionary<string, string>
        {
            ["shared.js"] = """
                    var initial = globalThis.__vjs_context;
                    initial.state.order = ['shared-eval'];
                    initial.host.log('info', 'shared-eval');
                    export default { __vjsPlugin: 'object', value: {
                        exports: { common: 'ready' },
                        setup: function (ctx) {
                            if (ctx !== initial || ctx.exports.common !== 'ready') throw Error('shared context');
                            ctx.state.order.push('shared-setup');
                            ctx.host.log('info', 'shared-setup');
                        },
                        event: function (ctx) { ctx.state.order.push('shared-hook'); },
                        status: function (ctx) { return [ctx.host.api.game.side(), ctx.host.api.game.sessionReady(), ctx.host.api.game.isServer()]; },
                        teardown: function (ctx) { ctx.host.log('info', 'shared-stop'); }
                    }};
                """,
            [side + ".js"] = """
                    var initial = globalThis.__vjs_context;
                    if (initial.state.order.join(',') !== 'shared-eval,shared-setup') throw Error('side loaded too early');
                    function Branch() { this.exports = { branch: 'ready' }; this.marker = 7; }
                    Branch.prototype.setup = function (ctx) {
                        if (ctx !== initial || this.ctx !== ctx || this.exports !== ctx.exports) throw Error('branch context');
                        ctx.host.log('info', 'side-setup');
                    };
                    Branch.prototype.event = function () {
                        if (this.marker !== 7) throw Error('class this');
                        this.ctx.state.order.push('side-hook');
                        return this.ctx.state.order;
                    };
                    Branch.prototype.teardown = function () { this.ctx.host.log('info', 'side-stop'); };
                    export default { __vjsPlugin: 'class', ctor: Branch };
                """,
            [(side == "server" ? "client" : "server") + ".js"] = "throw Error('OPPOSITE BRANCH EXECUTED');"
        });
        WritePlugin(scenario, "dependent", new() { ["shared.js"] = "globalThis.__vjs_plugin={__vjsPlugin:'object',value:{setup:function(ctx){if(ctx.dep('combined').branch!=='ready')throw Error('dependency exports');}}};" }, "depends=combined@*\n");
        WritePlugin(scenario, "disabled", new() { ["shared.js"] = "throw Error('disabled');" }, "enabled=false\n");
        WritePlugin(scenario, "other-side", new() { [(side == "server" ? "client" : "server") + ".js"] = "throw Error('ineligible');" });
        var log = new CaptureLog();
        var host = Host(scenario, side, log);
        try
        {
            host.Load();
            Assert(!host.HasErrors, string.Join(" | ", host.Errors));
            GameProbeBindings.Session = false;
            GameProbeBindings.Authority = false;
            Assert(host.DispatchTo("combined", "status", "{}") == $"[\"{side}\",false,false]", "pre-session authority");
            GameProbeBindings.Session = true;
            GameProbeBindings.Authority = true;
            Assert(host.DispatchTo("combined", "status", "{}") == $"[\"{side}\",true,true]", "authority changed process branch");
            GameProbeBindings.Session = false;
            GameProbeBindings.Authority = false;
            Assert(host.DispatchTo("combined", "status", "{}") == $"[\"{side}\",false,false]", "authority persisted after session");
            Assert(host.LoadedNames.SequenceEqual(new[] { "combined", "dependent" }), "eligibility/disabled filtering");
            Assert(host.DispatchTo("combined", "event", "{}") == "[\"shared-eval\",\"shared-setup\",\"shared-hook\",\"side-hook\"]", "hook order and result");
            var sharedPath = Path.Combine(scenario, "plugins", "combined", "shared.js");
            File.WriteAllText(sharedPath, File.ReadAllText(sharedPath).Replace("shared-eval", "updated-eval"));
            // The side component expects the shared setup's state; update its expectation too.
            var sidePath = Path.Combine(scenario, "plugins", "combined", side + ".js");
            File.WriteAllText(sidePath, File.ReadAllText(sidePath).Replace("shared-eval", "updated-eval"));
            host.Reload();
            Assert(!host.HasErrors, "reload errors");
            Assert(host.DispatchTo("combined", "event", "{}") == "[\"updated-eval\",\"shared-setup\",\"shared-hook\",\"side-hook\"]", "module reload did not load updated source");
        }
        finally { host.Dispose(); }
        Assert(log.Messages.Count(message => message == "side-stop") == 2, "side teardown count");
        Assert(log.Messages.Count(message => message == "shared-stop") == 2, "shared teardown count");
        var firstSideStop = log.Messages.IndexOf("side-stop");
        Assert(firstSideStop >= 0 && log.Messages[firstSideStop + 1] == "shared-stop", "reverse teardown");
    }

    private static void CheckFailures(string root)
    {
        var scenario = Path.Combine(root, "failures");
        var shared = "globalThis.__vjs_plugin={__vjsPlugin:'object',value:{exports:{collision:1},teardown:function(ctx){ctx.host.log('info','rollback');}}};";
        WritePlugin(scenario, "bad", new()
        {
            ["shared.js"] = shared,
            ["server.js"] = "setTimeout(function(){throw Error('leaked timer');},1);globalThis.__vjs_plugin={__vjsPlugin:'object',value:{setup:function(){throw Error('setup failure');}}};"
        });
        WritePlugin(scenario, "dependent", new() { ["shared.js"] = "throw Error('DEPENDENT EVALUATED');" }, "depends=bad@*\n");
        WritePlugin(scenario, "missing-dep", new() { ["shared.js"] = "throw Error('MISSING DEP EVALUATED');" }, "depends=absent@*\n");
        WritePlugin(scenario, "duplicate", new() { ["shared.js"] = shared, ["server.js"] = shared });
        WritePlugin(scenario, "async", new() { ["shared.js"] = "globalThis.__vjs_plugin={__vjsPlugin:'object',value:{setup:function(){return Promise.resolve();}}};" });
        WritePlugin(scenario, "conflict", new()
        {
            ["shared.js"] = "globalThis.__vjs_plugin={__vjsPlugin:'object',value:{event:function(){return 1;},teardown:function(){throw Error('teardown failure');}}};",
            ["server.js"] = "globalThis.__vjs_plugin={__vjsPlugin:'object',value:{event:function(){return 2;},teardown:function(ctx){ctx.host.log('info','conflict-stop');}}};"
        });
        WritePlugin(scenario, "healthy", new() { ["shared.js"] = "globalThis.__vjs_plugin={__vjsPlugin:'object',value:{event:function(){return 'ok';}}};" });
        WritePlugin(scenario, "empty", new());
        WritePlugin(scenario, "mismatch", new() { ["shared.js"] = "throw Error('MISMATCH EVALUATED');" });
        File.WriteAllText(Path.Combine(scenario, "plugins", "mismatch", "manifest.ini"), "name=other\napiVersion=1\n");
        WritePlugin(scenario, "cycle-a", new() { ["shared.js"] = "throw Error('cycle');" }, "depends=cycle-b@*\n");
        WritePlugin(scenario, "cycle-b", new() { ["shared.js"] = "throw Error('cycle');" }, "depends=cycle-a@*\n");
        var log = new CaptureLog();
        var host = Host(scenario, "server", log);
        try
        {
            host.Load();
            Assert(host.LoadedNames.SequenceEqual(new[] { "conflict", "healthy" }), "failed plugins were retained: " + string.Join(",", host.LoadedNames));
            Assert(host.Errors.Any(error => error.Contains("must match the plugin folder")), "manifest/folder mismatch accepted");
            Assert(!host.LoadedNames.Contains("mismatch"), "mismatched plugin loaded");
            Assert(host.Errors.Any(error => error.Contains("server.js") && error.Contains("setup failure")), "entry error attribution");
            Assert(host.Errors.Any(error => error.Contains("Duplicate export")), "duplicate exports accepted");
            Assert(host.Errors.Any(error => error.Contains("synchronous")), "async lifecycle accepted");
            Assert(log.Messages.Count(message => message == "rollback") == 2, "partial initialization did not roll back");
            host.DispatchTo("conflict", "event", "{}");
            Assert(host.Plugins.Single(plugin => plugin.Name == "conflict").Failed, "conflicting results not rejected");
            Assert(log.Messages.Contains("conflict-stop"), "failed hook did not clean up");
            Assert(host.DispatchTo("healthy", "event", "{}") == "\"ok\"", "unrelated plugin failed");
            Thread.Sleep(10);
            host.Tick();
            Assert(!log.Messages.Any(message => message.Contains("leaked timer") || message.Contains("DEPENDENT EVALUATED") || message.Contains("MISSING DEP EVALUATED")), "failed plugin work executed");
        }
        finally { host.Dispose(); }
    }

    private static void CheckDiagnostics(string root)
    {
        var scenario = Path.Combine(root, "diagnostics");
        WritePlugin(scenario, "diagnostics", new()
        {
            ["shared.js"] = "globalThis.__vjs_plugin={__vjsPlugin:'object',value:{event:function(){throw new Error('mapped failure');}}};",
            ["shared.js.map"] = """{"version":3,"sources":["original.ts"],"mappings":"AACA"}"""
        });

        var log = new CaptureLog();
        var host = Host(scenario, "server", log);
        try
        {
            host.Load();
            Assert(!host.HasErrors, string.Join(" | ", host.Errors));
            host.DispatchTo("diagnostics", "event", "{}");
            var message = log.Messages.FirstOrDefault(entry => entry.Contains("hook 'event' threw")) ?? "";
            Assert(message.Contains("mapped failure"), "script message was lost");
            Assert(message.Contains("original.ts:2:0"), "source map was not applied: " + message);
        }
        finally
        {
            host.Dispose();
        }
    }

    private static void CheckStorage(string root)
    {
        var scenario = Path.Combine(root, "storage");
        WritePlugin(scenario, "store", new()
        {
            ["shared.js"] = """
                globalThis.__vjs_plugin={__vjsPlugin:'object',value:{check:function(ctx){
                    var value="tab\tnewline\nunicode-\u00e9-\u0001";
                    ctx.host.storage.set("value", value);
                    if (ctx.host.storage.get("value") !== value) throw Error("storage roundtrip");
                    var json=JSON.stringify({text:value});
                    ctx.host.storage.set("json", json);
                    if (ctx.host.storage.get("json") !== json) throw Error("storage json roundtrip");
                }}};
                """
        });
        WritePlugin(scenario, "legacy", new()
        {
            ["shared.js"] = """
                globalThis.__vjs_plugin={__vjsPlugin:'object',value:{check:function(ctx){
                    if (ctx.host.storage.get("greeting") !== "Hello") throw Error("legacy read");
                    ctx.host.storage.set("other", "value");
                    if (ctx.host.storage.get("greeting") !== "Hello") throw Error("legacy lost after write");
                }}};
                """
        });

        var legacyDirectory = Path.Combine(scenario, "data", "legacy");
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "storage.json"), "greeting\tHello\n");

        var log = new CaptureLog();
        var host = Host(scenario, "server", log);
        try
        {
            host.Load();
            Assert(!host.HasErrors, string.Join(" | ", host.Errors));
            host.DispatchTo("store", "check", "{}");
            host.DispatchTo("legacy", "check", "{}");
            Assert(!host.Plugins.Any(plugin => plugin.Failed), "storage plugin failed: " + string.Join(" | ", log.Messages));

            var store = File.ReadAllText(Path.Combine(scenario, "data", "store", "storage.json"));
            Assert(store.StartsWith("{", StringComparison.Ordinal), "storage is not JSON: " + store);
            Assert(!store.Contains('\t') && !store.Contains('\n'), "storage contains raw separators");
            var legacy = File.ReadAllText(Path.Combine(legacyDirectory, "storage.json"));
            Assert(legacy.StartsWith("{", StringComparison.Ordinal), "legacy storage was not migrated");
            Assert(legacy.Contains("Z3JlZXRpbmc="), "legacy key was not preserved");
        }
        finally
        {
            host.Dispose();
        }
    }

    private static JsHost Host(string root, string side, CaptureLog log) => new(root, side == "server" ? ProcessSide.Server : ProcessSide.Client, log, bindingFilter: type => !BindingProbe.IsFixture(type));
    private static void WritePlugin(string root, string name, Dictionary<string, string> entries, string settings = "")
    {
        var directory = Path.Combine(root, "plugins", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.ini"), $"name={name}\napiVersion=1\n{settings}");
        foreach (var entry in entries) File.WriteAllText(Path.Combine(directory, entry.Key), entry.Value);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected rejection: " + message);
    }
    private sealed class CaptureLog : IJsLog
    {
        internal readonly List<string> Messages = new();
        public void Write(string level, string plugin, string message) => Messages.Add(message);
    }
}
