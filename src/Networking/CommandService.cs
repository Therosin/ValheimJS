using System;
using System.Linq;
using ValheimJS.Commands;
using ValheimJS.Core;
using ValheimJS.Services;
using Network = ValheimJS.Core.Networking;

namespace ValheimJS.Networking;

/// <summary>Private chat-command submission and replies over the main channel/event system.</summary>
internal sealed class CommandService : IDisposable
{
    private const string Channel = "valheimjs:commands";
    private readonly Network _network;
    private readonly ChatCommands _commands;
    private readonly AccountService _accounts;
    private readonly PermissionService _permissions;
    private readonly NetworkScope _scope;
    internal CommandService(Network network, ChatCommands commands, AccountService accounts, PermissionService permissions)
    {
        _network = network; _commands = commands; _accounts = accounts; _permissions = permissions; _scope = network.OpenScope();
        _scope.Channel(Channel).On<string>("submit", (message, text) => Receive(message.Sender, text));
        _scope.Channel(Channel).On<string>("reply", (message, text) =>
        {
            if (!GameEnvironment.Dedicated && _network.ServerPeer != 0 && message.Sender == _network.ServerPeer) Show(text);
        });
    }
    internal bool Submit(string text)
    {
        if (!ChatCommandParser.IsCommand(text)) return false;
        if (ZNet.instance != null && ZNet.instance.IsServer()) Receive(ZNet.GetUID(), text);
        else if (!_network.Available || _network.ServerPeer == 0) Show("The command server is not available yet.");
        else _scope.Channel(Channel).Emit(_network.ServerPeer, "submit", text);
        return true;
    }
    private void Receive(long sender, string text)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || !ChatCommandParser.IsCommand(text)) return;
        var local = !ZNet.instance.IsDedicated() && sender == ZNet.GetUID();
        var peer = ZNet.instance.GetPeers()?.FirstOrDefault(value => value != null && value.m_uid == sender);
        if (!local && peer == null) return;
        var platform = PlayerIdentity.PlatformId(sender);
        var name = local ? Player.m_localPlayer?.GetPlayerName() : peer?.m_playerName;
        var account = platform == null ? null : _accounts.Observe(sender.ToString(System.Globalization.CultureInfo.InvariantCulture), platform, name ?? "Player");
        var user = new CommandUser(sender, name ?? "Player", platform, message =>
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || PlayerIdentity.PlatformId(sender) != platform) return;
            if (local) { if (sender == ZNet.GetUID()) Show(message); }
            else if (ZNet.instance.GetPeers().Any(value => value != null && value.m_uid == sender)) _scope.Channel(Channel).Emit(sender, "reply", message);
        }, _accounts, account?.Id, _permissions);
        _commands.Submit(user, text);
    }
    internal static void Show(string text)
    {
        if (Chat.instance != null && !string.IsNullOrEmpty(text)) Chat.instance.AddString("Commands", text, Talker.Type.Normal);
    }
    public void Dispose() => _scope.Dispose();
}
