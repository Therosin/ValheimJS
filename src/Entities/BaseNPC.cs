using System;
using Jint;
using Jint.Native;
using UnityEngine;
using ValheimJS.Scripting.Types;
using ValheimJS.World;

namespace ValheimJS.Entities;

/// <summary>
/// A ZDO-backed scripted world object, exposed to scripts as <c>ValheimBaseNPC</c>. Game objects,
/// ZDOs, simulation and durable state stay native; this facade is identity, pose, health, actions
/// and string state.
/// </summary>
[JsType("ValheimBaseNPC", Description = "A ZDO-backed scripted NPC.")]
public sealed class BaseNPC
{
    [JsIgnore] private readonly string _plugin;
    [JsIgnore] private readonly ZDOID _id;
    [JsIgnore] private readonly string _kind;
    [JsIgnore] private readonly string _name;
    [JsIgnore] private readonly Func<CommandResult, JsValue> _result;
    [JsIgnore] private readonly Func<string, JsValue, JsValue> _interact;
    [JsIgnore] private readonly Func<string, JsValue> _affordances;

    internal BaseNPC(string plugin, ZDOID id, string kind, string name, Func<CommandResult, JsValue> result,
        Func<string, JsValue, JsValue> interact, Func<string, JsValue> affordances)
    {
        _plugin = plugin;
        _id = id;
        _kind = kind;
        _name = name;
        _result = result;
        _interact = interact;
        _affordances = affordances;
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

    /// <summary>Requests a native move action; returns action acceptance and reason.</summary>
    [JsReturns("ActionResult")]
    public JsValue MoveTo(double x, double y, double z, bool run) => Run("moveTo", x, y, z, run);

    /// <summary>Clears movement; the script decides what happens next.</summary>
    [JsReturns("ActionResult")]
    public JsValue Idle() => Run("idle");

    /// <summary>Stops the current action.</summary>
    [JsReturns("ActionResult")]
    public JsValue Stop() => Run("stop");

    /// <summary>Waits in place; the action reports arrived when the timer elapses.</summary>
    [JsReturns("ActionResult")]
    public JsValue Wait(double seconds) => Run("wait", seconds);

    /// <summary>Turns toward a world point and reports the action as arrived.</summary>
    [JsReturns("ActionResult")]
    public JsValue LookAt(double x, double y, double z) => Run("lookAt", x, y, z);

    /// <summary>Turns toward another ZDO-backed entity; fails when it no longer exists.</summary>
    [JsReturns("ActionResult")]
    public JsValue Face(string id) => Run("face", id);

    /// <summary>Picks a pickable object into the inventory when in range.</summary>
    [JsReturns("ActionResult")]
    public JsValue Pick(string id) => Run("pick", id);

    /// <summary>Inspects what a target object supports for this body.</summary>
    [JsReturns("NpcAffordances")]
    public JsValue Affordances(string id) => _affordances(id);

    /// <summary>Performs one supported interaction against a target object.</summary>
    [JsReturns("NpcInteractionResult")]
    public JsValue Interact(string id, JsValue options) => _interact(id, options);

    [JsIgnore]
    private JsValue Run(string command, params object?[] args) =>
        _result(WorldManager.Instance?.Command(_plugin, Id, command, args) ?? CommandResult.Fail("no-world"));

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
