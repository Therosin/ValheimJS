using Jint;
using Jint.Native;

namespace ValheimJS.Scripting.Binding;

/// <summary>
/// The one place that swaps <c>__vjs_context</c> around host-driven script execution. Callers
/// save the previous value, run, and restore it in a <c>finally</c>; keeping this in one helper
/// means callbacks, timers, promise jobs and awaited continuations cannot drift apart.
/// </summary>
internal static class JsContextScope
{
    internal static JsValue Enter(Engine engine, JsValue context)
    {
        var previous = engine.GetValue("__vjs_context");
        engine.SetValue("__vjs_context", context);
        return previous;
    }

    internal static void Exit(Engine engine, JsValue previous) => engine.SetValue("__vjs_context", previous);
}
