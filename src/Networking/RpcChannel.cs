using System;
using System.Collections.Generic;
using System.Linq;
using ValheimJS.Core;
using ValheimJS.Utils;

namespace ValheimJS.Networking;

/// <summary>
/// The mod's RPC layer: named channels on top of the game's routed RPC, with deferred
/// registration (plugins load before the world starts) and a hook for built-in agents.
/// <para>
/// Payloads are strings. Callers decide whether that string is JSON, a binary frame, or a
/// <c>action|arg</c> command — the transport stays dumb, which is what keeps it cheap.
/// </para>
/// </summary>
internal static class RpcChannel
{
    private const string Prefix = "ValheimJS:";
    private static readonly HashSet<string> Listening = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
    private const string BinarySuffix = ":bin";
    private static readonly HashSet<string> ListeningBinary = new(StringComparer.OrdinalIgnoreCase);
    private static bool _subscribed;

    /// <summary>Built-in agents (not plugins) that need raw inbound messages.</summary>
    internal static event Action<string, long, string>? Received;

    internal static void Install()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        ZNet.s_onZNetStart += FlushPending;
    }

    internal static string[] Channels() => Listening.OrderBy(channel => channel, StringComparer.Ordinal).ToArray();

    internal static bool Available() => ZRoutedRpc.instance != null;

    /// <summary>True while requested channels are still waiting for the routing layer.</summary>
    internal static bool HasPending() => Pending.Count > 0;

    /// <summary>
    /// Peer id of the server, or 0 when this process is the server / not connected.
    /// (The game's <c>GetServerPeerID</c> is private in this build; the peer list carries the flag.)
    /// </summary>
    internal static long ServerPeer()
    {
        var peers = ZNet.instance?.GetPeers();
        if (peers == null)
        {
            return 0L;
        }

        foreach (var peer in peers)
        {
            if (peer != null && peer.m_server)
            {
                return peer.m_uid;
            }
        }

        return 0L;
    }

    internal static void SendFrame(string channel, string base64)
    {
        if (!TryDecode(base64, out var bytes))
        {
            return;
        }

        DispatchBytes(ZRoutedRpc.Everybody, channel, bytes);
    }

    internal static void SendFrameTo(long peerUid, string channel, string base64)
    {
        if (peerUid == 0L || !TryDecode(base64, out var bytes))
        {
            return;
        }

        DispatchBytes(peerUid, channel, bytes);
    }

    internal static void ListenFrame(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || ListeningBinary.Contains(channel))
        {
            return;
        }

        var rpc = ZRoutedRpc.instance;
        if (rpc == null)
        {
            Pending.Add(channel + BinarySuffix);
            return;
        }

        var rpcName = Prefix + channel + BinarySuffix;
        try
        {
            rpc.Register<ZPackage>(rpcName, (sender, package) => DeliverFrame(channel, sender, package));
            ListeningBinary.Add(channel);
            Plugin.Logger.LogInfo($"[Net] listening for frames on '{channel}' as {rpcName}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"[Net] could not listen for frames on '{channel}': {exception.Message}");
        }
    }

    private static void DispatchBytes(long target, string channel, byte[] bytes)
    {
        var rpc = ZRoutedRpc.instance;
        if (rpc == null || string.IsNullOrWhiteSpace(channel))
        {
            return;
        }

        var package = new ZPackage();
        package.Write(bytes);
        rpc.InvokeRoutedRPC(target, Prefix + channel + BinarySuffix, package);
    }

    private static void DeliverFrame(string channel, long sender, ZPackage package)
    {
        try
        {
            var bytes = package.ReadByteArray();
            var base64 = Convert.ToBase64String(bytes);
            Received?.Invoke(channel + BinarySuffix, sender, base64);

            ValheimBridge.Emit?.Invoke(
                "onNetFrame",
                "{" +
                $"\"channel\":{Json.Quote(channel)}," +
                $"\"sender\":{sender}," +
                $"\"data\":{Json.Quote(base64)}" +
                "}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("[Net] frame delivery failed: " + exception.Message);
        }
    }

    private static bool TryDecode(string base64, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(base64);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    internal static void Send(string channel, string payload) => Dispatch(ZRoutedRpc.Everybody, channel, payload);

    internal static void SendTo(long peerUid, string channel, string payload)
    {
        if (peerUid == 0L)
        {
            return;
        }

        Dispatch(peerUid, channel, payload);
    }

    private static void Dispatch(long target, string channel, string payload)
    {
        var rpc = ZRoutedRpc.instance;
        if (rpc == null || string.IsNullOrWhiteSpace(channel))
        {
            return;
        }

        rpc.InvokeRoutedRPC(target, Prefix + channel, payload ?? "null");
    }

    /// <summary>
    /// Requests a channel. Registration is deferred until the game's routing layer exists —
    /// plugins load before the world starts, so an immediate registration would be lost.
    /// </summary>
    internal static void Listen(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || Listening.Contains(channel))
        {
            return;
        }

        Pending.Add(channel);
        FlushPending();
    }

    internal static void FlushPending()
    {
        if (Pending.Count == 0)
        {
            return;
        }

        var rpc = ZRoutedRpc.instance;
        if (rpc == null)
        {
            return;
        }

        foreach (var channel in new List<string>(Pending))
        {
            if (channel.EndsWith(BinarySuffix, StringComparison.Ordinal))
            {
                var binaryChannel = channel.Substring(0, channel.Length - BinarySuffix.Length);
                Pending.Remove(channel);
                ListeningBinary.Remove(binaryChannel);
                ListenFrame(binaryChannel);
                continue;
            }

            var rpcName = Prefix + channel;
            try
            {
                rpc.Register<string>(rpcName, (sender, payload) => Deliver(channel, sender, payload));
                Listening.Add(channel);
                Pending.Remove(channel);
                Plugin.Logger.LogInfo($"[Net] listening on '{channel}' as {rpcName}");
            }
            catch (Exception exception)
            {
                Pending.Remove(channel);
                Plugin.Logger.LogWarning($"[Net] could not listen on '{channel}': {exception.Message}");
            }
        }
    }

    private static void Deliver(string channel, long sender, string payload)
    {
        Plugin.Logger.LogInfo($"[Net] received '{channel}' from {sender} ({payload.Length} chars)");
        Received?.Invoke(channel, sender, payload);

        try
        {
            ValheimBridge.Emit?.Invoke(
                "onNet",
                "{" +
                $"\"channel\":{Json.Quote(channel)}," +
                $"\"sender\":{sender}," +
                $"\"data\":{Json.Quote(payload)}" +
                "}");
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("[Net] delivery failed: " + exception.Message);
        }
    }

    /// <summary>Channel registrations live in the game's RPC table, which survives a reload.</summary>
    internal static void Reset() => Pending.Clear();
}
