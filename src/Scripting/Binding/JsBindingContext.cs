using System;
using System.Collections.Generic;
using System.IO;
using Network = ValheimJS.Core.Networking;

namespace ValheimJS.Scripting.Binding;

/// <summary>
/// Host services a binding may use while installing itself. Everything here is owned by the
/// host: paths, the plugin dispatch seam, and a small main-thread queue so bindings never have
/// to guess which thread they are on.
/// </summary>
public sealed class JsBindingContext : IDisposable
{
    private readonly Action<string, string> _log;
    private readonly List<JsBindings> _binders = new();
    private readonly Queue<Action> _posted = new();
    private readonly object _gate = new();
    private readonly Network? _networking;
    private readonly Core.ChatCommands? _commands;
    private readonly Services.AccountService? _accounts;
    private readonly Services.PermissionService? _permissions;
    private readonly Func<bool>? _worldReady;
    private Scripting.Types.JsTypeRegistry? _types;
    private Jint.Engine? _engine;

    public JsBindingContext(
        ProcessSide side,
        string dataRoot,
        string configRoot,
        string pluginRoot,
        string logsRoot,
        Func<string, string> dataDirectoryFor,
        Func<string, string> configDirectoryFor,
        Action<string, string> log,
        Network? networking = null,
        Core.ChatCommands? commands = null,
        Services.AccountService? accounts = null,
        Services.PermissionService? permissions = null,
        Func<bool>? worldReady = null)
    {
        if (!Enum.IsDefined(typeof(ProcessSide), side)) throw new ArgumentOutOfRangeException(nameof(side));
        ProcessSide = side;
        DataRoot = dataRoot;
        ConfigRoot = configRoot;
        PluginRoot = pluginRoot;
        LogsRoot = logsRoot;
        DataDirectoryFor = dataDirectoryFor;
        ConfigDirectoryFor = configDirectoryFor;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _networking = networking;
        _commands = commands;
        _accounts = accounts;
        _permissions = permissions;
        _worldReady = worldReady;
    }

    /// <summary><c>server</c> on a dedicated process, <c>client</c> otherwise.</summary>
    public ProcessSide ProcessSide { get; }
    public string Side => ProcessSide == ProcessSide.Server ? "server" : "client";

    /// <summary>True for the dedicated-server process branch.</summary>
    public bool Dedicated => ProcessSide == ProcessSide.Server;
    /// <summary>Process-owned services; engine disposal releases consumer scopes, not the service.</summary>
    public Network Networking => _networking ?? throw new InvalidOperationException("Networking service was not supplied by the host.");
    public Core.ChatCommands ChatCommands => _commands ?? throw new InvalidOperationException("Command registry was not supplied by the host.");
    public Services.AccountService Accounts => _accounts ?? throw new InvalidOperationException("AccountService was not supplied by the host.");
    public Services.PermissionService Permissions => _permissions ?? throw new InvalidOperationException("PermissionService was not supplied by the host.");

    /// <summary>Host-supplied world readiness; probes without a game default to true.</summary>
    public bool WorldReady => _worldReady?.Invoke() ?? true;

    /// <summary>
    /// Native facade classes exposed to scripts. Attached by the runtime once the engine exists;
    /// binders register their types in <see cref="JsBindings.Install"/>.
    /// </summary>
    public Scripting.Types.JsTypeRegistry Types =>
        _types ?? throw new InvalidOperationException("Type registry was not supplied by the runtime.");

    /// <summary>Type registry when the runtime attached one; probes may run without it.</summary>
    internal Scripting.Types.JsTypeRegistry? TypesOrNull => _types;

    /// <summary>The live Jint engine, for bindings that build native payloads/classes.</summary>
    public Jint.Engine Engine => _engine ?? throw new InvalidOperationException("Engine was not supplied by the runtime.");

    internal void AttachEngine(Jint.Engine engine, Scripting.Types.JsTypeRegistry types)
    {
        _engine = engine;
        _types = types;
    }

    /// <summary>Root of the script data area: <c>&lt;plugin root&gt;/data</c>.</summary>
    public string DataRoot { get; }

    /// <summary>Root of per-plugin config folders.</summary>
    public string ConfigRoot { get; }

    /// <summary>Folder holding the JS plugins.</summary>
    public string PluginRoot { get; }

    /// <summary>Folder receiving per-plugin log files.</summary>
    public string LogsRoot { get; }

    /// <summary>Resolves (and creates) a plugin's sandboxed data directory.</summary>
    public Func<string, string> DataDirectoryFor { get; }

    /// <summary>Resolves (and creates) a plugin's config directory.</summary>
    public Func<string, string> ConfigDirectoryFor { get; }


    /// <summary>Per-plugin folder (bundle + manifest).</summary>
    public string PluginDirectoryFor(string plugin) => Path.Combine(PluginRoot, plugin);

    public void Log(string level, string message) => _log(level, message);

    /// <summary>
    /// Queues work for the game thread. Safe to call from RPC or file-watcher threads; the host
    /// drains the queue every tick.
    /// </summary>
    public void Post(Action action)
    {
        if (action == null)
        {
            return;
        }

        lock (_gate)
        {
            _posted.Enqueue(action);
        }
    }

    /// <summary>Drains posted work and samples binding-owned state. Must run on the game thread.</summary>
    public void Pump()
    {
        while (true)
        {
            Action next;
            lock (_gate)
            {
                if (_posted.Count == 0)
                {
                    break;
                }

                next = _posted.Dequeue();
            }

            try
            {
                next();
            }
            catch (Exception exception)
            {
                Log("error", "posted work failed: " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        foreach (var binder in _binders)
        {
            try
            {
                binder.Pump();
            }
            catch (Exception exception)
            {
                Log("warn", $"pumping {binder.GetType().Name} failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    internal void Own(JsBindings binder) => _binders.Add(binder);

    /// <summary>Tells every binder that a plugin's registrations are no longer valid.</summary>
    internal void ReleasePlugin(string plugin)
    {
        foreach (var binder in _binders)
        {
            try
            {
                binder.Release(plugin);
            }
            catch (Exception exception)
            {
                Log("warn", $"releasing {binder.GetType().Name} for '{plugin}' failed: {exception.Message}");
            }
        }
    }

    public void Dispose()
    {
        for (var index = _binders.Count - 1; index >= 0; index--)
        {
            try
            {
                _binders[index].Dispose();
            }
            catch (Exception exception)
            {
                Log("warn", $"disposing {_binders[index].GetType().Name} failed: {exception.Message}");
            }
        }

        _binders.Clear();
        lock (_gate)
        {
            _posted.Clear();
        }
    }
}
