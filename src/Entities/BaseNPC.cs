using System;
using UnityEngine;
using ValheimJS.Scripting.Types;
using ValheimJS.World;

namespace ValheimJS.Entities;

/// <summary>
/// A ZDO-backed scripted world object, exposed to scripts as <c>ValheimBaseNPC</c>. Game objects,
/// ZDOs, simulation and durable state stay native; this facade is identity, pose, health,
/// commands and string state.
/// </summary>
[JsType("ValheimBaseNPC", Description = "A ZDO-backed scripted NPC.")]
public sealed class BaseNPC
{
    [JsIgnore] private readonly string _plugin;
    [JsIgnore] private readonly ZDOID _id;
    [JsIgnore] private readonly string _kind;
    [JsIgnore] private readonly string _name;

    internal BaseNPC(string plugin, ZDOID id, string kind, string name)
    {
        _plugin = plugin;
        _id = id;
        _kind = kind;
        _name = name;
    }

    [JsIgnore] internal string Owner => _plugin;

    [JsIgnore] internal ZDOID ZdoId => _id;

    public string Id => _id.ToString();

    public string Kind => _kind;

    public string Name => WorldManager.Zdo(Id)?.GetString(WorldKeys.Name, _name) ?? _name;

    public double X => WorldManager.Zdo(Id)?.GetPosition().x ?? 0d;

    public double Y => WorldManager.Zdo(Id)?.GetPosition().y ?? 0d;

    public double Z => WorldManager.Zdo(Id)?.GetPosition().z ?? 0d;

    public float Health => NpcBehavior.HealthOf(_id, MaxHealth);

    public float MaxHealth => NpcBehavior.MaxHealthOf(_id);

    public bool IsAlive => NpcBehavior.IsAlive(_id, Health);

    /// <summary>Sets the current intent. False when refused (no authority).</summary>
    public bool MoveTo(double x, double y, double z, bool run) =>
        WorldManager.Instance?.Command(_plugin, Id, "moveTo", x, y, z, run) == true;

    /// <summary>Clears movement; the NPC wanders near its home point.</summary>
    public bool Idle() => WorldManager.Instance?.Command(_plugin, Id, "idle") == true;

    /// <summary>Stops movement and wandering until the next intent.</summary>
    public bool Stop() => WorldManager.Instance?.Command(_plugin, Id, "stop") == true;

    /// <summary>Waits in place; the intent reports arrived when the timer elapses.</summary>
    public bool Wait(double seconds) => WorldManager.Instance?.Command(_plugin, Id, "wait", seconds) == true;

    /// <summary>Turns toward a world point and reports the intent as arrived.</summary>
    public bool LookAt(double x, double y, double z) =>
        WorldManager.Instance?.Command(_plugin, Id, "lookAt", x, y, z) == true;

    /// <summary>Turns toward another ZDO-backed entity; fails when it no longer exists.</summary>
    public bool Face(string id) => WorldManager.Instance?.Command(_plugin, Id, "face", id) == true;

    [JsNullable]
    public string? State(string key) => WorldManager.Instance?.StateGet(_plugin, Id, key);

    public bool SetState(string key, string value) =>
        WorldManager.Instance?.StateSet(_plugin, Id, key, value) == true;

    public bool RemoveState(string key) =>
        WorldManager.Instance?.StateRemove(_plugin, Id, key) == true;

    public string[] StateKeys() => WorldManager.Instance?.StateKeys(_plugin, Id) ?? Array.Empty<string>();

    [JsIgnore]
    internal NpcPerception Perception() =>
        WorldManager.Instance?.Snapshot(_plugin, Id) as NpcPerception ?? EmptyPerception();

    private NpcPerception EmptyPerception() => new()
    {
        Id = Id,
        Kind = _kind,
        X = X,
        Y = Y,
        Z = Z,
        Health = Health,
        MaxHealth = MaxHealth,
        IsAlive = IsAlive,
        Faction = string.Empty,
        Level = 1,
        HomeX = X,
        HomeY = Y,
        HomeZ = Z
    };
}
