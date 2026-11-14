using System;
using UnityEngine;

namespace ValheimJS.World;

/// <summary>Shared persistence keys and channel name for world-simulated objects.</summary>
internal static class WorldKeys
{
    internal const string Channel = "valheimjs:world";
    internal const string Sim = "vjs.sim";
    internal const string Plugin = "vjs.plugin";
    internal const string Kind = "vjs.kind";
    internal const string Name = "vjs.name";
    internal const string HomeX = "vjs.home.x";
    internal const string HomeY = "vjs.home.y";
    internal const string HomeZ = "vjs.home.z";
    internal const string Speed = "vjs.speed";
    internal const string Moving = "vjs.moving";
    internal const string StatePrefix = "vjs.s.";
    internal const string StateIndex = "vjs.sk";
    internal const string Memory = "vjs.memory";
    internal const string ActionKind = "vjs.action.kind";
    internal const string ActionX = "vjs.action.x";
    internal const string ActionY = "vjs.action.y";
    internal const string ActionZ = "vjs.action.z";
    internal const string ActionRun = "vjs.action.run";
    internal const string ActionTarget = "vjs.action.target";
    internal const string ActionSeconds = "vjs.action.seconds";
    internal const string ActionStartDistance = "vjs.action.start";
    internal const string ActionWaitUntil = "vjs.action.until";
    internal const string Stats = "vjs.stats";
    internal const string Food = "vjs.food";
    internal const string Inventory = "vjs.inv";
    internal const string InventoryInitialized = "vjs.inv.init";
    internal const string Death = "vjs.death";
    internal const string Generation = "vjs.gen";
}

/// <summary>
/// Authority-owned record for one simulated world object. Position/rotation are written to the
/// object's ZDO by <see cref="WorldManager"/>; behaviour implementations add their own keys.
/// </summary>
internal sealed class WorldObject
{
    internal ZDOID Id;
    internal string Handle = "";
    internal string Kind = "";
    internal string Plugin = "";
    internal string Name = "";
    internal Vector3 Position;
    internal float Yaw;
    internal Vector3 Home;
    internal ZNetView? View;
    internal Character? Character;
    internal VjsNpcAI? AI;
    internal VjsBodyStats? Stats;
    internal bool BodyBound;
    internal bool BodyReady => Character != null && Stats != null && Stats.Ready;
    internal float Speed;
    internal bool Moving;
    internal double TickAccumulator;
    internal float LastWrite;
    internal int Generation;
    internal WorldKind KindInfo = null!;
    internal IWorldBehavior Behavior = null!;

    internal ZDO? Zdo => ZDOMan.instance?.GetZDO(Id);
}

/// <summary>Registration for one object kind; plugins add kinds at runtime.</summary>
internal sealed class WorldKind
{
    internal string Name = "";
    internal string Plugin = "";
    internal string Prefab = "";
    internal double TickMs = 500;
    internal float RelevancyRadius = 64f;
    internal Func<WorldObject, IWorldBehavior> Create = _ => throw new InvalidOperationException();
    internal Action? Prepare;
}

/// <summary>Outcome of one native action request; reasons are stable, script-facing facts.</summary>
internal readonly struct CommandResult
{
    private CommandResult(bool ok, string? reason)
    {
        Ok = ok;
        Reason = reason;
    }

    internal bool Ok { get; }

    internal string? Reason { get; }

    internal static CommandResult Success => new(true, null);

    internal static CommandResult Fail(string reason) => new(false, reason);
}

/// <summary>Per-object simulation owned by a kind. Only invoked on the world authority.</summary>
internal interface IWorldBehavior
{
    /// <summary>
    /// Called when the object gains (or regains) its native body. Behaviours replay any action
    /// they hold so movement survives body culling, rehydrate and server restarts.
    /// </summary>
    void Bind(WorldObject obj);

    void Tick(WorldObject obj, float dt);
    void Write(WorldObject obj, ZDO zdo);
    object? Snapshot(WorldObject obj);
    CommandResult Command(WorldObject obj, string command, object?[] args);
}
