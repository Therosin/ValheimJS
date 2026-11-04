using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Serialization;

namespace ValheimJS.Networking;

/// <summary>The transport seam shared by the event system and binary sync path.</summary>
public interface IRpcTransport
{
    bool Available { get; }
    long Everybody { get; }
    long ServerPeer { get; }
    IDisposable Subscribe(string channel, string eventName, bool responds, Func<NetworkMessage, byte[], byte[]> handler);
    void Notify(long peer, string channel, string eventName, byte[] payload);
    Task<byte[]> Request(long peer, string channel, string eventName, byte[] payload, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Routed peer identity and event address; the sender is not an authorization claim.</summary>
[DataContract]
public sealed class NetworkMessage
{
    public NetworkMessage(long sender, string channel, string eventName) { Sender = sender; Channel = channel; Event = eventName; }
    [DataMember(Name = "sender")] public long Sender { get; private set; }
    [DataMember(Name = "channel")] public string Channel { get; private set; }
    [DataMember(Name = "event")] public string Event { get; private set; }
}
