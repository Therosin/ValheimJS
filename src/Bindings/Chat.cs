using System;
using ValheimJS.Scripting.Binding;
using Splatform;
using UnityEngine;

namespace ValheimJS.Bindings;

/// <summary>
/// Chat output from the server. Scripts see <c>host.api.chat.*</c>.
/// <para>
/// Server-authored messages travel as the game's own <c>ChatMessage</c> routed call, addressed
/// to every peer (or one peer for <c>sendTo</c>). Clients run the same permission check as for
/// player chat, so a message may be filtered by a client's relation settings — treat delivery
/// as best effort, not guaranteed.
/// </para>
/// </summary>
[JsBinding("Chat", JsBindPhase.Gameplay, order: 130)]
internal static class ChatBindings
{

    public static void Bind(JsBindingContext context)
    {
        context.Add("chat.send", new Action<string>(Send));
        context.Add("chat.sendTo", new Action<long, string>(SendTo));
        context.Add("chat.available", new Func<bool>(() => ZRoutedRpc.instance != null && ZNet.instance != null));
    }

    private static void Send(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Broadcast(ZRoutedRpc.Everybody, text);
    }

    private static void SendTo(long peerUid, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || peerUid == 0L)
        {
            return;
        }

        Broadcast(peerUid, text);
    }

    private static void Broadcast(long target, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // A process with a local player can display directly; a dedicated server asks its clients.
        if (!EnvBindings.Dedicated())
        {
            Core.ChatAgent.Show(text);
            return;
        }

        if (target == ZRoutedRpc.Everybody)
        {
            var peers = ZNet.instance?.GetPeers();
            if (peers == null || peers.Count == 0)
            {
                Networking.RpcChannel.Send(Core.ChatAgent.Channel, text);
                return;
            }

            foreach (var peer in peers)
            {
                if (peer != null && !peer.m_server)
                {
                    Core.PlayerAccess.QueueOrSend(peer.m_uid, Core.ChatAgent.Channel, text);
                }
            }

            return;
        }

        Core.PlayerAccess.QueueOrSend(target, Core.ChatAgent.Channel, text);
    }
}
