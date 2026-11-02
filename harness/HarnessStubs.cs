using System;
using ValheimJS.Scripting.Binding;

// Test doubles for the game bindings a plugin needs to load outside the game. Only the pieces the
// sample plugins touch; anything else stays unavailable, which is itself a useful signal.
[JsBinding("TestNet", JsBindPhase.Engine, order: 1)]
internal static class NetStubs
{
    public static void Bind(JsBindingContext context)
    {
        context.Add("net.listen", new Action<string>(_ => { }));
        context.Add("net.send", new Action<string, string>((_, _) => { }));
        context.Add("net.sendTo", new Action<long, string, string>((_, _, _) => { }));
        context.Add("net.channels", new Func<string[]>(() => Array.Empty<string>()));
        context.Add("net.available", new Func<bool>(() => true));
        context.Add("net.side", new Func<string>(() => "server"));
    }
}
