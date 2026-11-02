using System;
using System.Collections.Generic;
using System.IO;
using Jint;
using Jint.Native;

namespace ValheimJS.Scripting;

/// <summary>
/// One Jint engine hosting every plugin. Shared state is what makes cross-plugin
/// <c>exports</c> and <c>deps</c> work — separate engines cannot hand each other objects.
/// Isolation is per plugin instead: a throwing handler disables only that plugin.
/// </summary>
internal sealed class JsRuntime : IDisposable
{
    private readonly IJsLog _log;

    public JsRuntime(IJsLog log, JsRuntimeOptions? options = null)
    {
        _log = log;
        options ??= new JsRuntimeOptions();

        Engine = new Engine(opt =>
        {
            // Plugins are untrusted: bound the engine instead of trusting plugin authors. CLR
            // access stays off unless a binding explicitly hands a delegate over.
            if (options.MaxStatements > 0)
            {
                opt.MaxStatements((int)Math.Min(options.MaxStatements, int.MaxValue));
            }

            if (options.TimeoutMs > 0)
            {
                opt.TimeoutInterval(TimeSpan.FromMilliseconds(options.TimeoutMs));
            }

            if (options.LimitRecursion > 0)
            {
                opt.LimitRecursion(options.LimitRecursion);
            }

            // A host delegate that throws should surface as a JS error the plugin can catch,
            // not as an exception that escapes the engine and kills the whole tick.
            opt.CatchClrExceptions();

            if (options.MemoryLimitBytes > 0)
            {
                opt.LimitMemory(options.MemoryLimitBytes);
            }
        });

        Engine.Execute(BridgeScript);
    }

    internal Engine Engine { get; }

    public void SetHost(string name, Delegate callback) => Engine.SetValue(name, callback);

    /// <summary>Installs dotted binding paths (<c>fs.read</c>) as nested JS functions under host.api.</summary>
    public void InstallBindings(IReadOnlyDictionary<string, ValheimJS.Scripting.Binding.JsBindingFunction> functions)
    {
        var index = 0;
        foreach (var (path, binding) in functions)
        {
            var slot = "__vjs_binding_" + index++;
            Engine.SetValue(slot, binding.Callback);
            Engine.SetValue("__vjs_api_path", path);
            var json = binding.Kind == ValheimJS.Scripting.Binding.JsReturnKind.Json ? "true" : "false";
            var scoped = binding.Scoped ? "true" : "false";
            Engine.Execute($"__vjs_api_set(__vjs_api_path, {slot}, {json}, {scoped})");
        }
    }

    public int Register(string pluginName, string bundlePath, string pluginDirectory)
    {
        Engine.Execute(File.ReadAllText(bundlePath));
        Engine.SetValue("__vjs_loading", pluginName);
        Engine.SetValue("__vjs_loading_dir", pluginDirectory);
        var index = Engine.Evaluate("__vjs_register(__vjs_plugin, __vjs_loading, __vjs_loading_dir)").AsNumber();
        Engine.SetValue("__vjs_plugin", JsValue.Undefined);
        return (int)index;
    }

    public string? Invoke(int index, string hook, string? payloadJson)
    {
        Engine.SetValue("__vjs_arg", payloadJson ?? string.Empty);
        var result = Engine.Evaluate($"__vjs_invoke({index}, '{hook}', __vjs_arg)");
        return result.IsUndefined() || result.IsNull() ? null : result.ToString();
    }

    public void PumpTimers()
    {
        Engine.SetValue("__vjs_clock", (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Engine.Evaluate("__vjs_pump(__vjs_clock)");
    }

    public void Dispose() => Engine.Dispose();

    /// <summary>
    /// Host-side glue. Each plugin gets a <c>ctx</c> with its own <c>state</c>, <c>exports</c>,
    /// <c>dep(name)</c> and a <c>host</c> API bound to its name — no shared mutable "current plugin",
    /// so timers from different plugins cannot cross their storage or logs. Bundles stay plain
    /// scripts: they only set <c>globalThis.__vjs_plugin</c>.
    /// </summary>
    private const string BridgeScript = """
        globalThis.__vjs_active = null;

        function __vjs_withCtx(ctx, fn) {
            var previous = globalThis.__vjs_active;
            globalThis.__vjs_active = ctx;
            try { return fn(); } finally { globalThis.__vjs_active = previous; }
        }

        function __vjs_makeHost(name, dir, paths) {
            return {
                name: name,
                dir: dir,
                log: function (level, message) { __vjs_log(name, String(level), String(message)); },
                now: function () { return __vjs_now(); },
                config: function (key) { return __vjs_config(name, String(key)); },
                storage: {
                    get: function (key) { return __vjs_storage_get(name, String(key)); },
                    set: function (key, value) { __vjs_storage_set(name, String(key), String(value)); }
                },
                info: function () { return JSON.parse(__vjs_info(name, dir)); },
                api: null,
                paths: paths
            };
        }

        globalThis.__vjs_api = {};
        globalThis.__vjs_api_scoped = new Set();

        globalThis.__vjs_api_set = function (path, fn, returnsJson, scoped) {
            var parts = String(path).split('.');
            var node = globalThis.__vjs_api;
            for (var i = 0; i < parts.length - 1; i++) {
                if (!node[parts[i]]) { node[parts[i]] = {}; }
                node = node[parts[i]];
            }
            var name = parts[parts.length - 1];
            node[name] = returnsJson
                ? function () {
                    var raw = fn.apply(null, arguments);
                    return raw === null || raw === undefined ? raw : JSON.parse(String(raw));
                }
                : fn;
            if (scoped) { globalThis.__vjs_api_scoped.add(path); }
        };

        // Per-plugin view of the API: scoped bindings receive the plugin name first, so they
        // can resolve that plugin's own data/config directories.
        globalThis.__vjs_makeApi = function (pluginName) {
            var scoped = globalThis.__vjs_api_scoped;
            function build(node, prefix) {
                var result = {};
                Object.keys(node).forEach(function (key) {
                    var path = prefix ? prefix + '.' + key : key;
                    var value = node[key];
                    if (typeof value === 'function') {
                        result[key] = scoped.has(path)
                            ? function () {
                                var args = [pluginName];
                                for (var i = 0; i < arguments.length; i++) { args.push(arguments[i]); }
                                return value.apply(null, args);
                            }
                            : value;
                    } else {
                        result[key] = build(value, path);
                    }
                });
                return result;
            }
            return build(globalThis.__vjs_api, '');
        };

        globalThis.__vjs_registry = { entries: [], byName: new Map() };

        globalThis.__vjs_register = function (module, name, dir) {
            if (!module || typeof module !== 'object') {
                throw new Error('plugin bundle did not export an object via globalThis.__vjs_plugin');
            }

            var paths = JSON.parse(__vjs_paths(name));
            var host = __vjs_makeHost(name, dir, paths);
            host.api = globalThis.__vjs_makeApi(name);
            var ctx = {
                name: name,
                dir: dir,
                paths: paths,
                host: host,
                state: {},
                exports: module.exports || {},
                dep: function (dependencyName) {
                    var entry = globalThis.__vjs_registry.byName.get(String(dependencyName));
                    if (!entry) { throw new Error('unknown dependency: ' + dependencyName); }
                    return entry.ctx.exports;
                }
            };

            var entry = { module: module, ctx: ctx, failed: false };
            globalThis.__vjs_registry.entries.push(entry);
            if (!globalThis.__vjs_registry.byName.has(name)) {
                globalThis.__vjs_registry.byName.set(name, entry);
            }
            return globalThis.__vjs_registry.entries.length - 1;
        };

        globalThis.__vjs_invoke = function (index, hook, payloadJson) {
            var entry = globalThis.__vjs_registry.entries[index];
            if (!entry) { throw new Error('no plugin registered at index ' + index); }
            if (entry.failed) { return null; }
            var handler = entry.module[hook];
            if (typeof handler !== 'function') { return null; }
            var payload = payloadJson ? JSON.parse(payloadJson) : undefined;
            var result = __vjs_withCtx(entry.ctx, function () { return handler(entry.ctx, payload); });
            if (result === undefined || result === null) { return null; }
            // async handlers return a promise; its value is not a hook result
            if (typeof result.then === 'function') { return null; }
            return JSON.stringify(result);
        };

        globalThis.__vjs_markFailed = function (index) {
            var entry = globalThis.__vjs_registry.entries[index];
            if (entry) { entry.failed = true; }
        };

        globalThis.__vjs_timers = (function () {
            var nextId = 1;
            var active = new Map();
            function schedule(ctx, callback, ms, repeat) {
                var id = nextId++;
                var delay = Math.max(1, Number(ms) || 1);
                active.set(id, { ctx: ctx, callback: callback, due: __vjs_now() + delay, period: delay, repeat: !!repeat });
                return id;
            }
            function cancel(id) { active.delete(id); }
            function pump(now) {
                if (active.size === 0) { return; }
                var due = [];
                active.forEach(function (timer, id) { if (now >= timer.due) { due.push(id); } });
                for (var i = 0; i < due.length; i++) {
                    var id = due[i];
                    var timer = active.get(id);
                    if (!timer) { continue; }
                    if (timer.repeat) { timer.due = now + timer.period; } else { active.delete(id); }
                    try { __vjs_withCtx(timer.ctx, timer.callback); }
                    catch (error) {
                        try {
                            var text = String(error) + (error && error.stack ? ' | ' + error.stack : '');
                            (timer.ctx && timer.ctx.host ? timer.ctx.host : __vjs_active.host).log('error', 'timer: ' + text);
                        } catch (ignored) { }
                    }
                }
            }
            return { schedule: schedule, cancel: cancel, pump: pump };
        })();

        globalThis.setTimeout = function (callback, ms) {
            return __vjs_timers.schedule(globalThis.__vjs_active, callback, ms, false);
        };
        globalThis.setInterval = function (callback, ms) {
            return __vjs_timers.schedule(globalThis.__vjs_active, callback, ms, true);
        };
        globalThis.clearTimeout = function (id) { __vjs_timers.cancel(id); };
        globalThis.clearInterval = function (id) { __vjs_timers.cancel(id); };
        globalThis.__vjs_pump = function (now) { __vjs_timers.pump(now); };
        """;
}
