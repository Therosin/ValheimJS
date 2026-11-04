using System;

namespace ValheimJS.Core;

/// <summary>
/// Displays server announcements on a client.
/// <para>
/// Community practice (Valheim-Modding wiki, "RPC Introduction") is: the server RPCs a client
/// handler and the client calls <c>Chat.instance.AddString</c>. Faking a chat <c>ChatMessage</c>
/// routed call with a synthetic UserInfo is not the supported path.
/// </para>
/// </summary>
internal static class ChatAgent
{
    internal const string Channel = "valheimjs:chat";
    private static bool _installed;
    private static IDisposable? _subscription;

    internal static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        _subscription = Utils.RpcBus.Channel(Channel).On<string>("message", (ctx, payload) => Handle(Channel, ctx.Sender, payload));

        Plugin.Logger.LogInfo($"chat agent listening on '{Channel}'");
    }

    internal static void Shutdown()
    {
        _subscription?.Dispose();
        _subscription = null;
        _installed = false;
    }

    private static void Handle(string channel, long sender, string payload)
    {
        if (channel != Channel)
        {
            return;
        }

        var server = Bindings.NetBindings.ServerPeerId();
        if (server == 0 || sender != server) return;

        try
        {
            Show(payload);
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("[ChatAgent] " + exception.Message);
        }
    }

    internal static void Show(string text)
    {
        var chat = Chat.instance;
        if (chat == null || string.IsNullOrEmpty(text))
        {
            return;
        }

        chat.AddString("Server", text, Talker.Type.Shout);
        Plugin.Logger.LogInfo($"[ChatAgent] {text}");
    }
}
