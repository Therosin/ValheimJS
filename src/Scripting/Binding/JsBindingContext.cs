using System;
using System.Collections.Generic;

namespace ValheimJS.Scripting.Binding;

/// <summary>How a binding's return value crosses into JS.</summary>
public enum JsReturnKind
{
    /// <summary>Primitive / already a JS value (string, number, bool, null).</summary>
    Raw,

    /// <summary>JSON text; the host parses it so scripts receive a real object.</summary>
    Json
}

public readonly struct JsBindingFunction
{
    public JsBindingFunction(Delegate callback, JsReturnKind kind, bool scoped)
    {
        Callback = callback;
        Kind = kind;
        Scoped = scoped;
    }

    public Delegate Callback { get; }

    public JsReturnKind Kind { get; }

    /// <summary>Scoped bindings receive the calling plugin's name as their first argument.</summary>
    public bool Scoped { get; }
}

/// <summary>
/// What a binding module receives while installing itself. Paths are dotted and become nested
/// JS objects: <c>Add("fs.read", ...)</c> is <c>host.api.fs.read(...)</c> in scripts.
/// </summary>
public sealed class JsBindingContext
{
    private readonly Dictionary<string, JsBindingFunction> _functions = new(StringComparer.Ordinal);
    private readonly Action<string, string> _log;

    internal JsBindingContext(
        Action<string, string> log,
        Func<string, string> dataDirectoryFor,
        string dataRoot,
        string pluginRoot)
    {
        _log = log;
        DataDirectoryFor = dataDirectoryFor;
        DataRoot = dataRoot;
        PluginRoot = pluginRoot;
    }

    /// <summary>Resolves (and creates) the per-plugin data directory used as an fs sandbox.</summary>
    public Func<string, string> DataDirectoryFor { get; }

    /// <summary>Sandbox root for script file access: <c>BepInEx/config/ValheimJS/data</c>.</summary>
    public string DataRoot { get; }

    /// <summary>Folder holding the JS plugins (read-only for script data).</summary>
    public string PluginRoot { get; }

    internal IReadOnlyDictionary<string, JsBindingFunction> Functions => _functions;

    public void Add(string path, Delegate callback, JsReturnKind kind = JsReturnKind.Raw, bool scoped = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("binding path cannot be empty", nameof(path));
        }

        if (callback == null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        if (_functions.ContainsKey(path))
        {
            throw new InvalidOperationException($"binding '{path}' is already registered");
        }

        _functions[path] = new JsBindingFunction(callback, kind, scoped);
    }

    public void Log(string message) => _log("info", message);
}
