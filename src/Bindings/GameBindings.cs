using System;
using System.Collections.Generic;
using ValheimJS.Core;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

/// <summary>
/// Process and world identity. Values are read live on each call — during startup <c>ZNet</c>
/// and <c>EnvMan</c> are not ready, so they read as null/zero until the world loads.
/// </summary>
[JsBinder("game", Phase = JsBindPhase.Core, Order = 10,
    Description = "Which process this is and what world it is running.")]
internal sealed class GameBindings : JsBindings
{
    private static readonly string InstanceId = Guid.NewGuid().ToString("N");

    public GameBindings(JsBindingContext context) : base(context)
    {
    }

    private bool IsDedicated => Context.Dedicated;

    [JsBinding(Returns = "\"server\" | \"client\"", Description = "Immutable process side selected at startup; player hosts remain clients.")]
    public string Side() => Context.Side;

    [JsBinding(Description = "True for the dedicated-server process branch; independent of session readiness.")]
    public bool Dedicated() => IsDedicated;

    [JsBinding(Description = "Confirmed live world authority; false before a session exists.")]
    public bool IsServer() => GameEnvironment.IsServer();

    [JsBinding(Description = "True when ZNet exists; does not imply a completed connection or loaded world.")]
    public bool SessionReady() => GameEnvironment.SessionReady;

    [JsBinding(Description = "True for a client process, including a player hosting a world; does not imply a local player exists.")]
    public bool IsClient() => !IsDedicated;

    [JsBinding(Description = "True when a local client world is playable: ZNetScene is ready, the local player exists and is not teleporting. False on a dedicated server.")]
    public bool ClientReady() => ValheimJS.World.WorldManager.ClientReady();

    [JsBinding(Description = "True when this process is a single-player session.")]
    public bool SinglePlayer() => GameEnvironment.SinglePlayer;

    [JsBinding(Description = "Connected peers, including the server peer.")]
    public int Peers() => GameEnvironment.Peers;

    [JsBinding(Description = "Stable id for this process, unique per start; useful as a cache key.")]
    public string Instance() => InstanceId;

    [JsBinding(Returns = "string | null", Description = "World name, or null before the world loads.")]
    public string? WorldName() => GameEnvironment.WorldName;

    [JsBinding(Description = "Current world day, or 0 before the world loads.")]
    public int Day() => GameEnvironment.Day;

    [JsBinding(Description = "Fraction of the current day, 0..1.")]
    public double DayFraction() => GameEnvironment.DayFraction;

    [JsBinding(Description = "\"day\", \"night\", or \"unknown\" before the world loads.")]
    public string TimeOfDay() => GameEnvironment.TimeOfDay;

    [JsBinding(ReturnKind = JsReturnKind.Native, Returns = "GameSnapshot",
        Description = "One object with name, peers, day, dayFraction and timeOfDay.")]
    public Jint.Native.JsValue Snapshot() =>
        JsPayload.Object(Engine)
            .Set("world", GameEnvironment.WorldName)
            .Set("peers", GameEnvironment.Peers)
            .Set("day", GameEnvironment.Day)
            .Set("dayFraction", GameEnvironment.DayFraction)
            .Set("timeOfDay", GameEnvironment.TimeOfDay)
            .Build();

    [JsBinding(ReturnKind = JsReturnKind.Native, Returns = "PeerInfo[]",
        Description = "Connected peers with routed id, player name, player id and server/ready flags.")]
    public Jint.Native.JsValue PeerList()
    {
        var peers = ZNet.instance?.GetPeers();
        var items = new List<Jint.Native.JsValue>();
        if (peers != null)
        {
            foreach (var peer in peers)
            {
                if (peer == null)
                {
                    continue;
                }

                items.Add(JsPayload.Object(Engine)
                    .Set("uid", peer.m_uid)
                    .Set("name", peer.m_playerName)
                    .Set("playerId", peer.m_playerID.ToString())
                    .Set("server", peer.m_server)
                    .Set("ready", peer.IsReady())
                    .Build());
            }
        }

        return JsPayload.Array(Engine, items);
    }
}
