using System;
using System.Collections.Generic;

namespace ValheimJS.AI;

/// <summary>
/// Optional native action resolver for behaviour trees. The Unity host installs it so YAML
/// actions such as <c>findObject</c> can run without a JavaScript callback. Headless hosts leave
/// it null and fall back to the script callback.
/// </summary>
internal readonly struct AiNativeResult
{
    internal AiNativeResult(bool handled, bool success, object? output, bool running = false, bool paused = false)
    {
        Handled = handled;
        Success = success;
        Output = output;
        Running = running;
        Paused = paused;
    }

    internal bool Handled { get; }
    internal bool Success { get; }
    internal object? Output { get; }
    internal bool Running { get; }
    internal bool Paused { get; }

    internal static AiNativeResult NotHandled => new(false, false, null);
    internal static AiNativeResult Ok(object? output = null) => new(true, true, output);
    internal static AiNativeResult Fail(object? output = null) => new(true, false, output);
    internal static AiNativeResult Pending(bool paused = false) => new(true, false, null, running: true, paused);
}

internal delegate AiNativeResult AiNativeAction(
    string plugin,
    string handle,
    string nodeId,
    string name,
    IReadOnlyDictionary<string, object?> args,
    IDictionary<string, object?> state);

internal static class AiNative
{
    /// <summary>Installed by the Unity NPC binding; null in the headless harness.</summary>
    internal static AiNativeAction? Resolve { get; set; }
}
