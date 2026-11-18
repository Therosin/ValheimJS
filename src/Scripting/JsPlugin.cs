using System;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Scripting;

/// <summary>
/// A loaded plugin handle. The script state lives in the shared <see cref="JsRuntime"/>;
/// this object carries identity, failure isolation and the unload call.
/// </summary>
public sealed class JsPlugin
{
    private readonly JsRuntime _runtime;
    private readonly IJsLog _log;
    private readonly PluginRegistration _registration;

    internal JsPlugin(JsRuntime runtime, PluginManifest manifest, string directory, PluginRegistration registration, IJsLog log)
    {
        _runtime = runtime;
        _log = log;
        _registration = registration;
        Manifest = manifest;
        Directory = directory;
    }

    public PluginManifest Manifest { get; }

    public string Directory { get; }

    public string Name => Manifest.Name;

    /// <summary>Hooks the host discovered on the plugin object or class.</summary>
    public System.Collections.Generic.IReadOnlyCollection<string> Hooks => _registration.Hooks.Keys;

    public bool Failed { get; private set; }

    public string? Dispatch(string hook, string? payloadJson)
    {
        if (Failed)
        {
            return null;
        }

        try
        {
            return _runtime.Invoke(Name, hook, payloadJson);
        }
        catch (Exception exception)
        {
            Failed = true;
            _log.Write("error", Name, $"hook '{hook}' threw: {JsErrorText.Describe(exception)} — plugin disabled");
            _runtime.Unregister(Name);
            return null;
        }
    }

    public void Dispose()
    {
        if (Failed)
        {
            // Still drop registrations (timers, RPC handlers) for a failed plugin.
            _runtime.Unregister(Name);
            return;
        }

        try
        {
            _runtime.Unregister(Name);
        }
        catch (Exception exception)
        {
            _log.Write("warn", Name, "unload threw: " + JsErrorText.Describe(exception));
        }
    }
}
