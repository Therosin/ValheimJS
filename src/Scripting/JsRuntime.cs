using System;
using System.Collections.Generic;
using System.IO;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Scripting;

/// <summary>Paths a plugin may touch, exposed to scripts as <c>ctx.paths</c>.</summary>
public sealed class PluginPaths
{
    public string Root { get; set; } = "";

    public string Plugins { get; set; } = "";

    public string Plugin { get; set; } = "";

    public string Data { get; set; } = "";

    public string Config { get; set; } = "";

    public string Logs { get; set; } = "";

    public string LogFile { get; set; } = "";
}

/// <summary>Manifest facts and host callbacks needed to build one plugin's context.</summary>
public sealed class PluginServices
{
    public PluginPaths Paths { get; set; } = new();

    public string Version { get; set; } = "0.0.0";

    public string ApiVersion { get; set; } = HostApiVersion.Current;

    /// <summary>Writes one plugin-scoped line to the shared log and its per-plugin file.</summary>
    public Action<string, string>? Log { get; set; }

    public Func<string, string?> Config { get; set; } = _ => null;

    public Func<string, string?> StorageGet { get; set; } = _ => null;

    public Action<string, string> StorageSet { get; set; } = (_, _) => { };
}

/// <summary>One plugin as the engine sees it: descriptor, context, hook table and exports.</summary>
internal sealed class PluginRegistration
{
    public string Name { get; init; } = "";

    public JsObject Context { get; init; } = null!;

    public JsValue Exports { get; init; } = null!;

    public Dictionary<string, Function> Hooks { get; init; } = new();

    public JsValue Api { get; init; } = null!;
    public List<PluginComponent> Components { get; } = new();
}

internal sealed class PluginComponent
{
    public string Entry { get; init; } = "";
    public JsValue Instance { get; init; } = null!;
    public bool IsClass { get; init; }
    public Dictionary<string, Function> Hooks { get; init; } = new();
}

/// <summary>
/// One Jint engine hosting every plugin. Shared state is what makes cross-plugin
/// <c>exports</c> and <c>dep(name)</c> work — separate engines cannot hand each other objects.
/// Isolation is per plugin instead: a throwing handler disables only that plugin.
/// <para>
/// Lifecycle — class instantiation, context, dependency exports, hook discovery, timers and
/// teardown — is owned here. Bundles are plain scripts that set
/// <c>globalThis.__vjs_plugin</c> to a descriptor produced by the SDK trampoline.
/// </para>
/// </summary>
internal sealed class JsRuntime : IDisposable
{
    private const string DescriptorMarker = "__vjsPlugin";
    private const string ClassKind = "class";
    private const string ObjectKind = "object";

    private static readonly HashSet<string> ReservedMembers = new(StringComparer.Ordinal)
    {
        "constructor", "ctx", "host", "state", "exports", "version", "description",
        "setup", "teardown", "onLoad", "onUnload", DescriptorMarker, "value",
        "then", "toJSON"
    };

    private readonly IJsLog _log;
    private readonly Engine _engine;
    private readonly JsBindingInstaller _bindings;
    private readonly Dictionary<string, PluginRegistration> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsValue> _contextsByPlugin = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginServices> _servicesByPlugin = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Timer> _timers = new();
    private readonly List<string> _active = new();
    private int _nextTimer = 1;

    public JsRuntime(IJsLog log, JsBindingContext bindingContext, JsRuntimeOptions? options = null, Func<Type, bool>? bindingFilter = null)
    {
        _log = log;
        options ??= new JsRuntimeOptions();
        _bindings = new JsBindingInstaller(JsBindingDiscovery.Discover(typeof(JsRuntime).Assembly, bindingFilter), bindingContext);

        _engine = new Engine(opt =>
        {
            opt.Host.JobCallbacks = new PluginJobCallbacks();
            // Plugins are untrusted: bound the engine instead of trusting plugin authors. CLR
            // access stays off unless a binding explicitly hands a function over.
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

            // Stack frames use the same source map as the primary location, so a frame inside a
            // bundled SDK module still names the original TypeScript file.
            opt.SetBuildCallStackHandler((description, location, _) =>
            {
                var mapped = SourceMapRegistry.Map(location);
                var frame = string.IsNullOrWhiteSpace(description) ? "    at " : $"    at {description} ";
                if (string.IsNullOrEmpty(mapped.SourceFile))
                {
                    return string.IsNullOrWhiteSpace(description)
                        ? string.Empty
                        : $"    at {description}{Environment.NewLine}";
                }

                return $"{frame}({mapped.SourceFile}:{mapped.Start.Line}:{mapped.Start.Column}){Environment.NewLine}";
            });

            if (options.MemoryLimitBytes > 0)
            {
                opt.LimitMemory(options.MemoryLimitBytes);
            }
        });

        bindingContext.AttachEngine(_engine, new ValheimJS.Scripting.Types.JsTypeRegistry(_engine));
        InstallTimers();
    }

    internal Engine Engine => _engine;

    public int InstalledBindingCount => _bindings.InstalledMethodCount;

    public IReadOnlyList<string> InstalledBindings => _bindings.InstalledNamespaces;

    /// <summary>Registers a plugin's descriptor, builds its context and discovers its hooks.</summary>
    public PluginRegistration Register(
        string name,
        IReadOnlyList<string> bundlePaths,
        string pluginDirectory,
        PluginServices services)
    {
        var api = _bindings.BuildApi(_engine, name);
        var exports = new JsObject(_engine);
        var context = BuildContext(name, pluginDirectory, services, api, exports);
        var registration = new PluginRegistration
        {
            Name = name,
            Context = context,
            Exports = exports,
            Api = api
        };

        _plugins[name] = registration;
        _contextsByPlugin[name] = context;
        _servicesByPlugin[name] = services;

        try
        {
            foreach (var path in bundlePaths)
            {
                var fullPath = Path.GetFullPath(path);
                try
                {
                    Run(name, () =>
                    {
                        _engine.SetValue("__vjs_plugin", JsValue.Undefined);
                        _engine.Execute(File.ReadAllText(fullPath), fullPath);
                        var module = _engine.GetValue("__vjs_plugin");
                        _engine.SetValue("__vjs_plugin", JsValue.Undefined);
                        if (module is not ObjectInstance descriptor)
                            throw new InvalidOperationException("Entry must export definePlugin(...).");
                        var kind = descriptor.Get(DescriptorMarker);
                        if (!kind.IsString() || (kind.AsString() != ClassKind && kind.AsString() != ObjectKind))
                            throw new InvalidOperationException("Unknown descriptor; export definePlugin(...).");
                        var isClass = kind.AsString() == ClassKind;
                        var holder = descriptor.Get(isClass ? "ctor" : "value");
                        if (isClass ? holder is not Function : holder is not ObjectInstance)
                            throw new InvalidOperationException("Invalid entry descriptor.");
                        var instance = isClass ? Construct(holder.As<Function>()!) : holder;
                        var target = instance.AsObject();
                        var declared = target.Get("exports");
                        if (declared.IsObject())
                        {
                            foreach (var key in declared.AsObject().GetOwnPropertyKeys(Jint.Runtime.Types.String))
                            {
                                if (exports.HasOwnProperty(key)) throw new InvalidOperationException($"Duplicate export '{key}'.");
                                exports.Set(key, declared.AsObject().Get(key), true);
                            }
                        }
                        target.Set("ctx", context, true);
                        target.Set("host", context.Get("host"), true);
                        target.Set("state", context.Get("state"), true);
                        target.Set("exports", exports, true);
                        var component = new PluginComponent { Entry = path, Instance = instance, IsClass = isClass, Hooks = DiscoverHooks(target) };
                        // Include partially initialized components in rollback cleanup.
                        registration.Components.Add(component);
                        foreach (var hook in component.Hooks) registration.Hooks[hook.Key] = hook.Value;
                        InvokeLifecycle(registration, component, "setup", "onLoad");
                    });
                    LoadSourceMap(fullPath);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"{name} [{Path.GetFileName(path)}] {path}: {JsErrorText.Describe(exception)}", exception);
                }
            }
            return registration;
        }
        catch
        {
            Unregister(name);
            throw;
        }
        finally { _engine.SetValue("__vjs_plugin", JsValue.Undefined); }
    }

    /// <summary>Invokes one hook for one plugin; returns JSON when the handler produced a value.</summary>
    public string? Invoke(string plugin, string hook, string? payloadJson)
    {
        if (!_plugins.TryGetValue(plugin, out var registration))
        {
            return null;
        }

        if (!registration.Hooks.ContainsKey(hook))
        {
            return null;
        }

        var payload = payloadJson == null
            ? JsValue.Undefined
            : Json.Parse(_engine, payloadJson);

        string? answer = null;
        foreach (var component in registration.Components)
        {
            if (!component.Hooks.TryGetValue(hook, out var handler)) continue;
            var result = Run(plugin, () => component.IsClass
                ? handler.Call(component.Instance, new[] { payload })
                : handler.Call(JsValue.Undefined, new[] { registration.Context, payload }));
            if (result.IsNull() || result.IsUndefined() || result is ObjectInstance obj && obj.Get("then") is Function) continue;
            if (answer != null) throw new InvalidOperationException($"{plugin}: conflicting results for hook '{hook}'.");
            answer = Json.Stringify(_engine, result);
        }
        return answer;
    }

    /// <summary>Runs <c>teardown</c>/<c>onUnload</c>, then drops the plugin's registrations.</summary>
    public void Unregister(string plugin)
    {
        if (!_plugins.TryGetValue(plugin, out var registration))
        {
            return;
        }

        for (var index = registration.Components.Count - 1; index >= 0; index--)
        {
            var component = registration.Components[index];
            try { Run(plugin, () => InvokeLifecycle(registration, component, "teardown", "onUnload")); }
            catch (Exception exception) { _log.Write("warn", plugin, $"{component.Entry}: unload failed: " + JsErrorText.Describe(exception)); }
        }

        foreach (var id in new List<int>(_timers.Keys))
        {
            if (_timers[id].Plugin == plugin)
            {
                _timers.Remove(id);
            }
        }

        _plugins.Remove(plugin);
        _contextsByPlugin.Remove(plugin);
        _servicesByPlugin.Remove(plugin);
        _bindings.ReleasePlugin(plugin);
    }

    /// <summary>Fires due timers and flushes promise continuations. Call once per host tick.</summary>
    public void PumpTimers()
    {
        if (_timers.Count > 0)
        {
            var now = Now();
            foreach (var id in new List<int>(_timers.Keys))
            {
                if (!_timers.TryGetValue(id, out var timer) || now < timer.Due)
                {
                    continue;
                }

                if (timer.Repeat)
                {
                    timer.Due = now + timer.Period;
                }
                else
                {
                    _timers.Remove(id);
                }

                try
                {
                    Run(timer.Plugin, () => timer.Callback.Call(JsValue.Undefined, Array.Empty<JsValue>()));
                }
                catch (Exception exception)
                {
                    // A failing interval would repeat forever; stop it, like the old runtime did.
                    if (timer.Repeat)
                    {
                        _timers.Remove(id);
                    }

                    _log.Write("error", timer.Plugin, "timer failed: " + JsErrorText.Describe(exception) + " — timer stopped");
                }
            }
        }

        // Continuations from promises settled by host work only progress when the engine runs.
        _engine.Evaluate("void 0");
    }

    public void Dispose()
    {
        _timers.Clear();
        _plugins.Clear();
        _contextsByPlugin.Clear();
        _servicesByPlugin.Clear();
        SourceMapRegistry.Clear();
        _bindings.Dispose();
        _engine.Dispose();
    }

    /// <summary>Loads a linked Source Map next to the evaluated bundle, when the builder emitted one.</summary>
    private void LoadSourceMap(string bundlePath)
    {
        var mapPath = bundlePath + ".map";
        if (!File.Exists(mapPath))
        {
            return;
        }

        try
        {
            var root = Json.Parse(_engine, File.ReadAllText(mapPath)).AsObject();
            var sourceArray = root.Get("sources").AsArray();
            var sources = new string[sourceArray.Length];
            for (var index = 0; index < sourceArray.Length; index++)
            {
                sources[index] = sourceArray.Get(index).AsString();
            }

            var directory = Path.GetDirectoryName(mapPath) ?? ".";
            SourceMapRegistry.Register(bundlePath, new SourceMap(sources, root.Get("mappings").AsString(), directory));
        }
        catch (Exception exception)
        {
            _log.Write("warn", "host", $"source map '{mapPath}' ignored: {JsErrorText.Describe(exception)}");
        }
    }

    private void InstallTimers()
    {
        _engine.SetValue("setTimeout", TimerFunction("setTimeout", repeat: false));
        _engine.SetValue("setInterval", TimerFunction("setInterval", repeat: true));
        _engine.SetValue("clearTimeout", ClearTimerFunction("clearTimeout"));
        _engine.SetValue("clearInterval", ClearTimerFunction("clearInterval"));
    }

    private ClrFunction Native(string name, Func<JsValue, JsValue[], JsValue> body, int length) =>
        new ClrFunction(_engine, name, body, length, PropertyFlag.Configurable);

    private Function ClearTimerFunction(string name) => Native(name,
        (_, arguments) =>
        {
            if (arguments.Length > 0 && arguments[0].IsNumber())
            {
                _timers.Remove((int)arguments[0].AsNumber());
            }

            return JsValue.Undefined;
        }, 1);

    private Function TimerFunction(string name, bool repeat) => Native(name,
        (_, arguments) =>
        {
            if (arguments.Length == 0 || arguments[0] is not Function callback)
            {
                throw new ArgumentException($"{name} needs a function as its first argument.");
            }

            var seconds = arguments.Length > 1 && arguments[1].IsNumber() ? arguments[1].AsNumber() : 0;
            var delay = Math.Max(1, seconds);
            var id = _nextTimer++;
            _timers[id] = new Timer
            {
                Plugin = CurrentPlugin,
                Callback = callback,
                Due = Now() + delay,
                Period = delay,
                Repeat = repeat
            };

            return id;
        }, 2);

    private double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private string CurrentPlugin
    {
        get
        {
            // Binding callbacks restore context without entering Run; promise reactions also
            // carry their own context. Timers created there must belong to that plugin.
            var context = _engine.GetValue("__vjs_context");
            if (context is ObjectInstance obj && obj.Get("name").IsString()) return obj.Get("name").AsString();
            return _active.Count > 0 ? _active[_active.Count - 1] : "host";
        }
    }

    private sealed class PluginJobCallbacks : Jint.Runtime.JobCallbackHooks
    {
        protected override object? Capture(Engine engine)
        {
            var context = engine.GetValue("__vjs_context");
            return context is ObjectInstance ? context : null;
        }
        protected override object? Enter(Engine engine, object hostDefined)
            => JsContextScope.Enter(engine, (JsValue)hostDefined);
        protected override void Exit(Engine engine, object? token)
            => JsContextScope.Exit(engine, token as JsValue ?? JsValue.Undefined);
    }

    /// <summary>Marks the executing plugin so timers, context lookups and errors are attributed correctly.</summary>
    private T Run<T>(string plugin, Func<T> action)
    {
        _active.Add(plugin);
        JsValue previous;
        if (_contextsByPlugin.TryGetValue(plugin, out var context))
        {
            previous = JsContextScope.Enter(_engine, context);
        }
        else
        {
            previous = _engine.GetValue("__vjs_context");
        }

        try
        {
            return action();
        }
        finally
        {
            JsContextScope.Exit(_engine, previous);
            _active.RemoveAt(_active.Count - 1);
        }
    }

    private void Run(string plugin, Action action) => Run(plugin, () =>
    {
        action();
        return true;
    });

    private JsValue Construct(Function constructor) =>
        _engine.Construct(constructor, Array.Empty<JsValue>());

    private JsValue InvokeLifecycle(PluginRegistration registration, PluginComponent component, string primary, string fallback)
    {
        var target = component.Instance.AsObject();
        var hook = target.Get(primary);
        if (hook is not Function)
        {
            hook = target.Get(fallback);
        }

        if (hook is not Function function)
        {
            return JsValue.Undefined;
        }

        var result = component.IsClass
            ? function.Call(component.Instance, new[] { registration.Context })
            : function.Call(JsValue.Undefined, new[] { registration.Context });
        if (result is ObjectInstance obj && obj.Get("then") is Function)
            throw new InvalidOperationException($"{component.Entry}: {primary}/{fallback} must be synchronous; promises are unsupported.");
        return result;
    }

    private Dictionary<string, Function> DiscoverHooks(ObjectInstance instance)
    {
        var hooks = new Dictionary<string, Function>(StringComparer.Ordinal);
        var current = instance;
        var depth = 0;

        while (current != null && depth++ < 8)
        {
            foreach (var key in current.GetOwnPropertyKeys(Jint.Runtime.Types.String))
            {
                var name = key.ToString();
                if (ReservedMembers.Contains(name) || hooks.ContainsKey(name))
                {
                    continue;
                }

                var value = current.Get(name);
                if (value is Function function)
                {
                    hooks[name] = function;
                }
            }

            current = current.Prototype;
        }

        return hooks;
    }

    private JsObject BuildContext(string name, string directory, PluginServices services, JsValue api, JsObject exports)
    {
        var paths = PathsToJs(services.Paths);
        var storage = new JsObject(_engine);
        storage.Set("get", Native("get",
            (_, arguments) => ToJs(services.StorageGet(arguments.Length > 0 ? arguments[0].ToString() : "")), 1), false);
        storage.Set("set", Native("set", (_, arguments) =>
        {
            services.StorageSet(arguments[0].ToString(), arguments[1].ToString());
            return JsValue.Undefined;
        }, 2), false);

        var host = new JsObject(_engine);
        host.Set("name", name, false);
        host.Set("dir", directory, false);
        host.Set("paths", paths, false);
        host.Set("log", Native("log", (_, arguments) =>
        {
            var level = arguments.Length > 0 ? arguments[0].ToString() : "info";
            var message = arguments.Length > 1 ? arguments[1].ToString() : "";
            WriteLog(name, level, message);
            return JsValue.Undefined;
        }, 2), false);
        host.Set("now", Native("now", (_, _) => Now(), 0), false);
        host.Set("config", Native("config",
            (_, arguments) => ToJs(services.Config(arguments.Length > 0 ? arguments[0].ToString() : "")), 1), false);
        host.Set("storage", storage, false);
        host.Set("info", Native("info", (_, _) =>
        {
            var info = new JsObject(_engine);
            info.Set("name", name, false);
            info.Set("version", services.Version, false);
            info.Set("api", services.ApiVersion, false);
            info.Set("dir", directory, false);
            return info;
        }, 0), false);
        host.Set("api", api, false);

        var state = new JsObject(_engine);

        var context = new JsObject(_engine);
        context.Set("name", name, false);
        context.Set("dir", directory, false);
        context.Set("paths", paths, false);
        context.Set("host", host, false);
        context.Set("state", state, false);
        context.Set("exports", exports, false);
        context.Set("dep", Native("dep", (_, arguments) =>
        {
            var dependency = arguments.Length > 0 ? arguments[0].ToString() : "";
            if (_plugins.TryGetValue(dependency, out var registration))
            {
                return registration.Exports;
            }

            throw new InvalidOperationException(
                $"unknown dependency '{dependency}'; it is not loaded (check manifest depends=)");
        }, 1), false);

        return context;
    }

    private JsObject PathsToJs(PluginPaths paths)
    {
        var result = new JsObject(_engine);
        result.Set("root", paths.Root, false);
        result.Set("plugins", paths.Plugins, false);
        result.Set("plugin", paths.Plugin, false);
        result.Set("data", paths.Data, false);
        result.Set("config", paths.Config, false);
        result.Set("logs", paths.Logs, false);
        result.Set("logFile", paths.LogFile, false);
        return result;
    }

    private JsValue ToJs(string? value) => value == null ? JsValue.Null : JsValue.FromObject(_engine, value);

    private void WriteLog(string plugin, string level, string message)
    {
        if (_servicesByPlugin.TryGetValue(plugin, out var services) && services.Log != null)
        {
            services.Log(level, message);
            return;
        }

        _log.Write(level, plugin, message);
    }

    private sealed class Timer
    {
        public string Plugin = "host";

        public Function Callback = null!;

        public double Due;

        public double Period;

        public bool Repeat;
    }
}
