using System;
using ValheimJS.Scripting.Binding;
using ValheimJS.Networking;

namespace ValheimJS.Bindings;

/// <summary>
/// Script-facing networking. Scripts see <c>host.api.net.*</c>; the transport itself lives in
/// <see cref="RpcChannel"/>, so built-in agents and plugins share one channel registry.
/// </summary>
[JsBinding("Net", JsBindPhase.Gameplay, order: 140)]
internal static class NetBindings
{
    public static void Bind(JsBindingContext context)
    {
        context.Add("net.send", new Action<string, string>(RpcChannel.Send));
        context.Add("net.sendTo", new Action<long, string, string>(RpcChannel.SendTo));
        context.Add("net.listen", new Action<string>(RpcChannel.Listen));
        context.Add("net.channels", new Func<string[]>(RpcChannel.Channels));
        context.Add("net.available", new Func<bool>(RpcChannel.Available));
        context.Add("net.side", new Func<string>(() => EnvBindings.Dedicated() ? "server" : "client"));
        context.Add("net.serverPeer", new Func<long>(RpcChannel.ServerPeer));

        // Frames travel as raw bytes on the wire; base64 is only the JS boundary.
        context.Add("net.listenFrame", new Action<string>(RpcChannel.ListenFrame));
        context.Add("net.sendFrame", new Action<string, string>(RpcChannel.SendFrame));
        context.Add("net.sendFrameTo", new Action<long, string, string>(RpcChannel.SendFrameTo));
    }
}
