using System;
using ValheimJS.Core;
using Network = ValheimJS.Core.Networking;

namespace ValheimJS.Networking;

/// <summary>Receives server announcements through the shared channel/event system.</summary>
internal sealed class ChatService : IDisposable
{
    internal const string Channel = "valheimjs:chat";
    private readonly Network _network;
    private readonly NetworkScope _scope;
    internal ChatService(Network network)
    {
        _network = network;
        _scope = network.OpenScope();
        _scope.Channel(Channel).On<string>("message", Handle);
        Plugin.Logger.LogInfo($"Chat service listening on '{Channel}'");
    }
    private void Handle(NetworkMessage message, string text)
    {
        var server = _network.ServerPeer;
        if (server == 0 || message.Sender != server) return;
        try { Show(text); }
        catch (Exception exception) { Plugin.Logger.LogWarning("[ChatService] " + exception.Message); }
    }
    internal static void Show(string text)
    {
        var chat = Chat.instance;
        if (chat == null || string.IsNullOrEmpty(text)) return;
        chat.AddString("Server", text, Talker.Type.Shout);
        Plugin.Logger.LogInfo($"[ChatService] {text}");
    }
    public void Dispose() => _scope.Dispose();
}
