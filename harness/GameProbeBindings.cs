using ValheimJS.Scripting.Binding;

/// <summary>Harness-only process/session seam. No Unity calls are simulated by this binder.</summary>
[JsBinder("game")]
internal sealed class GameProbeBindings : JsBindings
{
    public GameProbeBindings(JsBindingContext context) : base(context) { }
    internal static bool Authority;
    internal static bool Session;
    [JsBinding] public string Side() => Context.Side;
    [JsBinding] public bool Dedicated() => Context.Dedicated;
    [JsBinding] public bool IsClient() => Context.Side == "client";
    [JsBinding] public bool IsServer() => Session && Authority;
    [JsBinding] public bool SessionReady() => Session;
}
