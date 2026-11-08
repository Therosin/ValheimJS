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
    internal const string IntentKind = "vjs.intent.kind";
    internal const string IntentX = "vjs.intent.x";
    internal const string IntentY = "vjs.intent.y";
    internal const string IntentZ = "vjs.intent.z";
    internal const string IntentRun = "vjs.intent.run";
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
    internal string Prefab = "";
    internal double TickMs = 500;
    internal float RelevancyRadius = 64f;
    internal Func<WorldObject, IWorldBehavior> Create = _ => throw new InvalidOperationException();
    internal Action? Prepare;
}

/// <summary>Per-object simulation owned by a kind. Only invoked on the world authority.</summary>
internal interface IWorldBehavior
{
    void Tick(WorldObject obj, float dt);
    void Write(WorldObject obj, ZDO zdo);
    object? Snapshot(WorldObject obj);
    bool Command(WorldObject obj, string command, object?[] args);
}
