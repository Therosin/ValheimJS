using System;
using System.Collections.Generic;
using System.IO;

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

    public JsBindingContext(
        ProcessSide side,
        string dataRoot,
        string configRoot,
        string pluginRoot,
        string logsRoot,
        Func<string, string> dataDirectoryFor,
        Func<string, string> configDirectoryFor,
        Action<string, string> log)
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
    }

    /// <summary><c>server</c> on a dedicated process, <c>client</c> otherwise.</summary>
    public ProcessSide ProcessSide { get; }
    public string Side => ProcessSide == ProcessSide.Server ? "server" : "client";

    /// <summary>True for the dedicated-server process branch.</summary>
    public bool Dedicated => ProcessSide == ProcessSide.Server;

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


    /// <summary>
    /// Set by the command binding. The host offers every chat message here first; returning
    /// true means it was a command and must not reach <c>onChat</c> handlers.
    /// </summary>
    public Func<string, bool>? ChatInterceptor { get; set; }

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

    /// <summary>Drains posted work. Must run on the game thread.</summary>
    public void Pump()
    {
        while (true)
        {
            Action next;
            lock (_gate)
            {
                if (_posted.Count == 0)
                {
                    return;
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
