using System;
using System.Threading;
using System.Threading.Tasks;
using ValheimJS.Utils;

namespace ValheimJS.Networking;

/// <summary>Adapts the common RpcBus to ValheimJS's event and sync systems.</summary>
internal sealed class RoutedRpcTransport : IRpcTransport
{
    public bool Available => RpcBus.Available;
    public long Everybody => ZRoutedRpc.Everybody;
    public long ServerPeer
    {
        get
        {
            var peers = ZNet.instance?.GetPeers();
            if (peers != null)
                foreach (var peer in peers)
                    if (peer != null && peer.m_server) return peer.m_uid;
            return 0;
        }
    }
    public IDisposable Subscribe(string channel, string eventName, bool responds, Func<NetworkMessage, byte[], byte[]> handler) =>
        RpcBus.Add(channel, eventName, responds, (message, payload) => handler(new NetworkMessage(message.Sender, message.Channel, message.Event), payload));
    public void Notify(long peer, string channel, string eventName, byte[] payload) => RpcBus.NotifyRaw(peer, channel, eventName, payload);
    public Task<byte[]> Request(long peer, string channel, string eventName, byte[] payload, TimeSpan timeout, CancellationToken cancellationToken) =>
        RpcBus.SendRaw(peer, channel, eventName, payload, timeout, cancellationToken);
}
