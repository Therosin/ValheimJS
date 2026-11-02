using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ValheimJS.Scripting;

public enum JsLogLevel
{
    Info,
    Warn,
    Error
}

/// <summary>Logging seam so the host runs outside Unity (tests, harness).</summary>
public interface IJsLog
{
    void Write(string level, string plugin, string message);
}

/// <summary>
/// Plugin discovery and lifecycle. Two passes like a package manager: read every manifest
/// first, order by declared dependencies, then load code — so a plugin can rely on another's
/// exports being registered before its own <c>onLoad</c> runs.
/// </summary>
public sealed class JsHost
{
    private readonly string _root;
    private readonly string _pluginsDir;
    private readonly string _dataDir;
    private readonly string _configDir;
    private readonly string _logsDir;
    private readonly IJsLog _log;
    private readonly JsRuntimeOptions _options;
    private readonly string _side;
    private readonly Dictionary<string, JsPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _errors = new();
    private int _pumpErrors;
    private JsRuntime? _runtime;

    /// <param name="root">The <c>server_plugins</c> folder: <c>plugins/</c>, <c>data/</c>, <c>config/</c>, <c>logs/</c> live inside.</param>
    /// <param name="side"><c>server</c> or <c>client</c>: picks the side-specific entry when present.</param>
    public JsHost(string root, string side, IJsLog log, JsRuntimeOptions? options = null)
    {
        _side = side;
        _options = options ?? new JsRuntimeOptions();
        _root = root;
        _pluginsDir = Path.Combine(root, "plugins");
        _dataDir = Path.Combine(root, "data");
        Storage.DocumentStore.Configure(_dataDir);
        _configDir = Path.Combine(root, "config");
        _logsDir = Path.Combine(root, "logs");
        _log = log;
    }

    public string Root => _root;


    public IReadOnlyList<string> Errors => _errors;

    public bool HasErrors => _errors.Count > 0;

    public IReadOnlyList<string> LoadedNames => _plugins.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<JsPlugin> Plugins => _plugins.Values.ToList();

    public void Load() => Reload();

    /// <summary>Drops every plugin and rebuilds the engine. Safe to call repeatedly.</summary>
    public void Reload()
    {
        Dispose();

        _errors.Clear();
        _plugins.Clear();

        foreach (var directory in new[] { _root, _pluginsDir, _dataDir, _configDir, _logsDir })
        {
            Directory.CreateDirectory(directory);
        }

        var manifests = new Dictionary<string, (PluginManifest Manifest, string Directory)>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.GetDirectories(_pluginsDir).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            // dot-folders (.git, .data, editor junk) are not plugins
            if (Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            var manifest = PluginManifest.Load(directory);
            manifest = manifest.WithEntry(ResolveEntry(directory, manifest));
            if (!manifest.Enabled)
            {
                _log.Write("info", manifest.Name, "disabled in manifest.ini");
                continue;
            }

            if (!File.Exists(Path.Combine(directory, manifest.Entry)))
            {
                _errors.Add($"{manifest.Name}: entry '{manifest.Entry}' not found in {directory}");
                continue;
            }

            if (manifest.ApiVersion != HostApiVersion.Current)
            {
                _errors.Add($"{manifest.Name}: apiVersion {manifest.ApiVersion} != host {HostApiVersion.Current}");
                _log.Write("error", manifest.Name, $"needs apiVersion {manifest.ApiVersion}, host provides {HostApiVersion.Current}");
                continue;
            }

            if (manifests.ContainsKey(manifest.Name))
            {
                _errors.Add($"{manifest.Name}: duplicate plugin name (second copy ignored)");
                continue;
            }

            manifests[manifest.Name] = (manifest, directory);
        }

        List<string> order;
        try
        {
            order = TopologicalOrder(manifests);
        }
        catch (Exception exception)
        {
            _errors.Add(exception.Message);
            _log.Write("error", "host", exception.Message);
            return;
        }

        var runtime = new JsRuntime(_log, _options);
        WireHostApi(runtime, manifests);
        InstallBindings(runtime);
        _runtime = runtime;

        foreach (var name in order)
        {
            var (manifest, directory) = manifests[name];
            try
            {
                var index = runtime.Register(manifest.Name, Path.Combine(directory, manifest.Entry), directory);
                var plugin = new JsPlugin(runtime, manifest, directory, index, _log);
                _plugins[name] = plugin;
                plugin.Dispatch("onLoad", null);
            }
            catch (Exception exception)
            {
                _errors.Add($"{name}: load failed: {Describe(exception)}");
                _log.Write("error", name, "load failed: " + Describe(exception));
            }
        }

        foreach (var error in _errors)
        {
            _log.Write("warn", "host", error);
        }

        _log.Write("info", "host", $"loaded {_plugins.Count} plugin(s) from {_pluginsDir}");
    }

    public void Tick()
    {
        try
        {
            _runtime?.PumpTimers();
            _pumpErrors = 0;
        }
        catch (Exception exception)
        {
            // A pump failure repeats every tick, so report the first occurrence in full and keep
            // the count; the stack is the only way to tell a host-delegate NRE from a Jint bug.
            var key = exception.GetType().Name + ": " + exception.Message;
            if (_pumpErrors == 0)
            {
                _log.Write("error", "host", "timer pump threw: " + exception);
            }
            else if (_pumpErrors == 4)
            {
                _log.Write("error", "host", $"timer pump still failing ({key}); suppressing further repeats");
            }

            _pumpErrors++;
        }
    }

    /// <summary>Dispatches a hook to every loaded plugin; returns non-null JSON results.</summary>
    public IReadOnlyList<string> DispatchAll(string hook, Func<string, string?> payloadFactory)
    {
        var results = new List<string>();
        foreach (var plugin in _plugins.Values)
        {
            var result = plugin.Dispatch(hook, payloadFactory(plugin.Name));
            if (result != null)
            {
                results.Add(result);
            }
        }
        return results;
    }

    public void Dispose()
    {
        foreach (var plugin in _plugins.Values)
        {
            plugin.Dispose();
        }
        _plugins.Clear();
        _runtime?.Dispose();
        _runtime = null;
    }

    /// <summary>
    /// Side-specific entries win over the shared one: <c>server/plugin.js</c> on a dedicated
    /// process, <c>client/plugin.js</c> otherwise, falling back to the manifest entry.
    /// </summary>
    private string ResolveEntry(string directory, PluginManifest manifest)
    {
        var sideEntry = Path.Combine(directory, _side, manifest.Entry);
        if (File.Exists(sideEntry))
        {
            return Path.Combine(_side, manifest.Entry).Replace('\\', '/');
        }

        return manifest.Entry;
    }

    /// <summary>Depth-first dependency order with cycle detection.</summary>
    private List<string> TopologicalOrder(IReadOnlyDictionary<string, (PluginManifest Manifest, string Directory)> manifests)
    {
        var order = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string name)
        {
            if (visited.Contains(name))
            {
                return;
            }

            if (!visiting.Add(name))
            {
                throw new InvalidOperationException($"cyclic plugin dependency at '{name}'");
            }

            var manifest = manifests[name].Manifest;
            foreach (var (dependency, requirement) in manifest.Depends)
            {
                if (!manifests.TryGetValue(dependency, out var resolved))
                {
                    throw new InvalidOperationException($"'{name}' depends on missing plugin '{dependency}'");
                }

                if (!PluginManifest.Satisfies(resolved.Manifest.Version, requirement))
                {
                    throw new InvalidOperationException(
                        $"'{name}' requires {dependency}@{requirement}, found {resolved.Manifest.Version}");
                }

                Visit(dependency);
            }

            visiting.Remove(name);
            visited.Add(name);
            order.Add(name);
        }

        foreach (var name in manifests.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            Visit(name);
        }

        return order;
    }

    /// <summary>Discovers <see cref="Bindings.JsBindingAttribute"/> modules and installs their API.</summary>
    private void InstallBindings(JsRuntime runtime)
    {
        var context = new ValheimJS.Scripting.Binding.JsBindingContext(
            (level, message) => _log.Write(level, "bindings", message),
            DataDirectoryFor,
            _dataDir,
            _pluginsDir);

        var functions = ValheimJS.Scripting.Binding.JsBindingLoader.BindAll(
            typeof(JsHost).Assembly,
            context,
            (level, message) => _log.Write(level, "bindings", message));

        try
        {
            runtime.InstallBindings(functions);
            _log.Write("info", "bindings", $"installed {functions.Count} binding(s)");
        }
        catch (Exception exception)
        {
            _log.Write("error", "bindings", "install failed: " + Describe(exception));
        }
    }

    /// <summary>Per-plugin data directory (created on demand), used as the fs sandbox.</summary>
    private string DataDirectoryFor(string plugin)
    {
        var directory = Path.Combine(_dataDir, plugin);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Per-plugin config directory (created on demand).</summary>
    private string ConfigDirectoryFor(string plugin)
    {
        var directory = Path.Combine(_configDir, plugin);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>JSON description of a plugin's paths, exposed as <c>ctx.host.paths</c>.</summary>
    private string PathsJson(string plugin)
    {
        var pluginDir = Path.Combine(_pluginsDir, plugin);
        var dataDir = DataDirectoryFor(plugin);
        var configDir = ConfigDirectoryFor(plugin);
        return "{" +
               $"\"root\":{Quote(_root)}," +
               $"\"plugins\":{Quote(_pluginsDir)}," +
               $"\"plugin\":{Quote(pluginDir)}," +
               $"\"data\":{Quote(dataDir)}," +
               $"\"config\":{Quote(configDir)}," +
               $"\"logs\":{Quote(_logsDir)}," +
               $"\"logFile\":{Quote(Path.Combine(_logsDir, plugin + ".log"))}" +
               "}";
    }

    private void WireHostApi(JsRuntime runtime, IReadOnlyDictionary<string, (PluginManifest Manifest, string Directory)> manifests)
    {
        runtime.SetHost("__vjs_log", new Action<string, string, string>(WriteLog));
        runtime.SetHost("__vjs_paths", new Func<string, string>(PathsJson));
        runtime.SetHost("__vjs_now", new Func<double>(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        runtime.SetHost("__vjs_config", new Func<string, string, string?>((plugin, key) => ReadConfig(plugin, key, manifests)));
        runtime.SetHost("__vjs_storage_get", new Func<string, string, string?>((plugin, key) => ReadStorage(plugin, key)));
        runtime.SetHost("__vjs_storage_set", new Action<string, string, string>((plugin, key, value) => WriteStorage(plugin, key, value)));
        runtime.SetHost("__vjs_info", new Func<string, string, string>((plugin, dir) =>
        {
            var version = manifests.TryGetValue(plugin, out var entry) ? entry.Manifest.Version : "0.0.0";
            var api = manifests.TryGetValue(plugin, out entry) ? entry.Manifest.ApiVersion : "1";
            return $"{{\"name\":{Quote(plugin)},\"version\":{Quote(version)},\"api\":{Quote(api)},\"dir\":{Quote(dir)}}}";
        }));
    }

    /// <summary>Writes to the shared log and to <c>server_plugins/logs/&lt;plugin&gt;.log</c>.</summary>
    private void WriteLog(string plugin, string level, string message)
    {
        _log.Write(level, plugin, message);

        try
        {
            Directory.CreateDirectory(_logsDir);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(_logsDir, plugin + ".log"), line);
        }
        catch
        {
            // per-plugin log files are best effort; the shared log already has the line
        }
    }

    /// <summary>Config precedence: <c>config/&lt;plugin&gt;/config.ini</c>, then the manifest.</summary>
    private string? ReadConfig(
        string plugin,
        string key,
        IReadOnlyDictionary<string, (PluginManifest Manifest, string Directory)> manifests)
    {
        var file = Path.Combine(ConfigDirectoryFor(plugin), "config.ini");
        if (File.Exists(file))
        {
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator > 0 && line.Substring(0, separator).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(separator + 1).Trim();
                }
            }
        }

        return manifests.TryGetValue(plugin, out var entry) ? entry.Manifest.Get(key) : null;
    }

    private string StoragePath(string plugin) => Path.Combine(DataDirectoryFor(plugin), "storage.json");

    private string? ReadStorage(string plugin, string key)
    {
        var file = StoragePath(plugin);
        if (!File.Exists(file))
        {
            return null;
        }

        foreach (var line in File.ReadAllLines(file))
        {
            var separator = line.IndexOf('\t');
            if (separator > 0 && line.Substring(0, separator) == key)
            {
                return line.Substring(separator + 1);
            }
        }

        return null;
    }

    private void WriteStorage(string plugin, string key, string value)
    {
        var file = StoragePath(plugin);
        var lines = new List<string>();
        var replaced = false;
        if (File.Exists(file))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var separator = line.IndexOf('\t');
                if (separator > 0 && line.Substring(0, separator) == key)
                {
                    lines.Add(key + "\t" + value);
                    replaced = true;
                }
                else
                {
                    lines.Add(line);
                }
            }
        }

        if (!replaced)
        {
            lines.Add(key + "\t" + value);
        }

        File.WriteAllLines(file, lines);
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Describe(Exception exception)
    {
        var inner = exception;
        while (inner.InnerException != null)
        {
            inner = inner.InnerException;
        }
        return inner.GetType().Name + ": " + inner.Message;
    }
}
