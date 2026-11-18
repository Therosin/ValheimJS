using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ValheimJS.Scripting.Binding;
using ValheimJS.Storage;
using Network = ValheimJS.Core.Networking;

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
/// exports being registered before its own setup runs.
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
    private readonly ProcessSide _processSide;
    private readonly Func<Type, bool>? _bindingFilter;
    private readonly Network? _networking;
    private readonly Core.ChatCommands? _commands;
    private readonly Services.AccountService? _accounts;
    private readonly Services.PermissionService? _permissions;
    private readonly Func<bool>? _worldReady;
    private readonly Dictionary<string, JsPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _errors = new();
    private int _pumpErrors;
    private JsRuntime? _runtime;
    private JsBindingContext? _bindings;

    /// <param name="root">The <c>server_plugins</c> folder: <c>plugins/</c>, <c>data/</c>, <c>config/</c>, <c>logs/</c> live inside.</param>
    /// <param name="side"><c>server</c> or <c>client</c>: picks the side-specific entry when present.</param>
    public JsHost(
        string root,
        ProcessSide side,
        IJsLog log,
        JsRuntimeOptions? options = null,
        Func<Type, bool>? bindingFilter = null,
        Network? networking = null,
        Core.ChatCommands? commands = null,
        Services.AccountService? accounts = null,
        Services.PermissionService? permissions = null,
        Func<bool>? worldReady = null)
    {
        if (!Enum.IsDefined(typeof(ProcessSide), side)) throw new ArgumentOutOfRangeException(nameof(side));
        _processSide = side;
        _side = side == ProcessSide.Server ? "server" : "client";
        _bindingFilter = bindingFilter;
        _networking = networking;
        _commands = commands;
        _accounts = accounts;
        _permissions = permissions;
        _worldReady = worldReady;
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

    /// <summary>Namespaces installed on this process, for diagnostics and tests.</summary>
    public IReadOnlyList<string> BindingNamespaces => _runtime?.InstalledBindings ?? Array.Empty<string>();

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
            if (!manifest.Enabled)
            {
                _log.Write("info", manifest.Name, "disabled in manifest.ini");
                continue;
            }

            // Identity and paths are both derived from the manifest name. Requiring the folder
            // to match keeps ctx.dir, ctx.paths.plugin, data/, config/ and assets in one place.
            var folderName = Path.GetFileName(directory);
            if (manifest.Name.Length == 0)
            {
                _errors.Add($"{folderName}: manifest name is empty");
                _log.Write("error", folderName, "manifest name is empty");
                continue;
            }

            if (!string.Equals(manifest.Name, folderName, StringComparison.Ordinal))
            {
                _errors.Add($"{folderName}: manifest name '{manifest.Name}' must match the plugin folder");
                _log.Write("error", folderName, $"manifest name '{manifest.Name}' must match the plugin folder");
                continue;
            }

            if (!new[] { "shared.js", "server.js", "client.js" }.Any(entry => File.Exists(Path.Combine(directory, entry))))
            {
                _errors.Add($"{manifest.Name}: no shared.js, server.js, or client.js in {directory}");
                continue;
            }

            if (manifest.ApiVersion != HostApiVersion.Current)
            {
                _errors.Add($"{manifest.Name}: apiVersion {manifest.ApiVersion} != host {HostApiVersion.Current}");
                _log.Write("error", manifest.Name, $"needs apiVersion {manifest.ApiVersion}, host provides {HostApiVersion.Current}");
                continue;
            }

            if (ResolveEntries(directory).Count == 0)
            {
                _log.Write("info", manifest.Name, $"skipped: no entries for {_side}");
                continue;
            }

            if (manifests.ContainsKey(manifest.Name))
            {
                _errors.Add($"{manifest.Name}: duplicate plugin name (second copy ignored)");
                continue;
            }

            manifests[manifest.Name] = (manifest, directory);
        }

        var order = TopologicalOrder(manifests);

        var bindings = CreateBindingContext();
        _bindings = bindings;

        JsRuntime runtime;
        try
        {
            runtime = new JsRuntime(_log, bindings, _options, _bindingFilter);
        }
        catch (Exception exception)
        {
            _errors.Add("bindings failed: " + JsErrorText.Describe(exception));
            _log.Write("error", "bindings", "install failed: " + JsErrorText.Describe(exception));
            bindings.Dispose();
            _bindings = null;
            return;
        }

        _runtime = runtime;


        _log.Write("info", "bindings", $"installed {runtime.InstalledBindingCount} binding(s) in {runtime.InstalledBindings.Count} namespace(s)");

        foreach (var name in order)
        {
            var (manifest, directory) = manifests[name];
            try
            {
                var failedDependency = manifest.Depends.Keys.FirstOrDefault(dependency => !_plugins.ContainsKey(dependency));
                if (failedDependency != null) throw new InvalidOperationException($"dependency '{failedDependency}' did not load on {_side}");
                var registration = runtime.Register(
                    manifest.Name,
                    ResolveEntries(directory),
                    directory,
                    ServicesFor(manifest, directory));
                _plugins[name] = new JsPlugin(runtime, manifest, directory, registration, _log);
            }
            catch (Exception exception)
            {
                _errors.Add($"{name}: load failed: {JsErrorText.Describe(exception)}");
                _log.Write("error", name, "load failed: " + JsErrorText.Describe(exception));
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
            _bindings?.Pump();
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

    /// <summary>Raises a host event. Private commands use CommandService, outside public chat.</summary>
    public void Emit(string hook, string payloadJson)
    {
        DispatchAll(hook, _ => payloadJson);
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

    /// <summary>Dispatches a hook to one loaded plugin, if present.</summary>
    public string? DispatchTo(string pluginName, string hook, string? payloadJson) =>
        _plugins.TryGetValue(pluginName, out var plugin) ? plugin.Dispatch(hook, payloadJson) : null;

    public void Dispose()
    {
        foreach (var plugin in _plugins.Values)
        {
            plugin.Dispose();
        }

        _plugins.Clear();
        _runtime?.Dispose();
        _runtime = null;
        _bindings = null;
    }

    private IReadOnlyList<string> ResolveEntries(string directory) => new[] { "shared.js", _side + ".js" }
        .Select(entry => Path.Combine(directory, entry)).Where(File.Exists).ToList();

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
            try { Visit(name); }
            catch (Exception exception) { _errors.Add(exception.Message); }
            finally { visiting.Clear(); }
        }

        return order;
    }

    private JsBindingContext CreateBindingContext() => new(
        _processSide,
        _dataDir,
        _configDir,
        _pluginsDir,
        _logsDir,
        DataDirectoryFor,
        ConfigDirectoryFor,
        (level, message) => _log.Write(level, "bindings", message),
        _networking,
        _commands,
        _accounts,
        _permissions,
        _worldReady);

    private PluginServices ServicesFor(PluginManifest manifest, string directory)
    {
        var pluginDir = Path.Combine(_pluginsDir, manifest.Name);
        var dataDir = DataDirectoryFor(manifest.Name);
        var configDir = ConfigDirectoryFor(manifest.Name);
        return new PluginServices
        {
            Version = manifest.Version,
            ApiVersion = manifest.ApiVersion,
            Log = (level, message) => WriteLog(manifest.Name, level, message),
            Config = key => ReadConfig(manifest.Name, key, manifest),
            StorageGet = key => ReadStorage(manifest.Name, key),
            StorageSet = (key, value) => WriteStorage(manifest.Name, key, value),
            Paths = new PluginPaths
            {
                Root = _root,
                Plugins = _pluginsDir,
                Plugin = pluginDir,
                Data = dataDir,
                Config = configDir,
                Logs = _logsDir,
                LogFile = Path.Combine(_logsDir, manifest.Name + ".log")
            }
        };
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

    /// <summary>Config precedence: <c>config/&lt;plugin&gt;/config.ini</c>, then the manifest.</summary>
    private string? ReadConfig(string plugin, string key, PluginManifest manifest)
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

        return manifest.Get(key);
    }

    /// <summary>Writes to the shared log and to <c>logs/&lt;plugin&gt;.log</c>.</summary>
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

    private string StoragePath(string plugin) => Path.Combine(DataDirectoryFor(plugin), "storage.json");

    private string? ReadStorage(string plugin, string key)
    {
        return ReadStorageMap(plugin).TryGetValue(key, out var value) ? value : null;
    }

    private void WriteStorage(string plugin, string key, string value)
    {
        var values = ReadStorageMap(plugin);
        values[key] = value;
        try
        {
            StringStore.Write(StoragePath(plugin), values);
        }
        catch (Exception exception)
        {
            _log.Write("error", plugin, "storage write failed: " + JsErrorText.Describe(exception));
        }
    }

    private Dictionary<string, string> ReadStorageMap(string plugin)
    {
        var file = StoragePath(plugin);
        try
        {
            return StringStore.Read(file);
        }
        catch (Exception exception)
        {
            _log.Write("error", plugin, "storage.json is invalid: " + JsErrorText.Describe(exception));
            BackupBrokenStorage(file);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static void BackupBrokenStorage(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Move(file, file + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            }
        }
        catch
        {
            // The error is already logged; a second failure to move the file is not actionable.
        }
    }

}
