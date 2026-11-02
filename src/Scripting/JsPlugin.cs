using System;

namespace ValheimJS.Scripting;

/// <summary>
/// A loaded plugin handle. The script state lives in the shared <see cref="JsRuntime"/>;
/// this object carries identity, dependency bookkeeping and failure isolation.
/// </summary>
public sealed class JsPlugin
{
    private readonly JsRuntime _runtime;
    private readonly IJsLog _log;

    internal JsPlugin(JsRuntime runtime, PluginManifest manifest, string directory, int index, IJsLog log)
    {
        _runtime = runtime;
        _log = log;
        Manifest = manifest;
        Directory = directory;
        Index = index;
    }

    public PluginManifest Manifest { get; }

    public string Directory { get; }

    internal int Index { get; }

    public string Name => Manifest.Name;

    public bool Failed { get; private set; }

    public string? Dispatch(string hook, string? payloadJson)
    {
        if (Failed)
        {
            return null;
        }

        try
        {
            return _runtime.Invoke(Index, hook, payloadJson);
        }
        catch (Exception exception)
        {
            Failed = true;
            _log.Write("error", Name, $"hook '{hook}' threw: {Describe(exception)} — plugin disabled");
            return null;
        }
    }

    public void Dispose()
    {
        if (Failed)
        {
            return;
        }

        try
        {
            _runtime.Invoke(Index, "onUnload", null);
        }
        catch (Exception exception)
        {
            _log.Write("warn", Name, "onUnload threw: " + Describe(exception));
        }
    }

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
