using System;
using System.Globalization;
using UnityEngine;
using ValheimJS.Scripting.Types;

namespace ValheimJS.Entities;

/// <summary>
/// A ZDO-backed scripted NPC, exposed to scripts as <c>ValheimBaseNPC</c>. Game objects, ZDOs,
/// the AI component and the intent queue stay internal; the public surface is identity, pose,
/// health, intents and durable string state.
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

    public string Name => NpcWorld.Zdo(_id)?.GetString("vjs.name", _name) ?? _name;

    public double X => NpcWorld.Zdo(_id)?.GetPosition().x ?? 0d;

    public double Y => NpcWorld.Zdo(_id)?.GetPosition().y ?? 0d;

    public double Z => NpcWorld.Zdo(_id)?.GetPosition().z ?? 0d;

    public float Health => NpcWorld.HealthOf(_id, MaxHealth);

    public float MaxHealth => NpcWorld.MaxHealthOf(_id);

    public bool IsAlive => NpcWorld.IsAlive(_id, Health);

    /// <summary>Sets the current intent. False when refused (no authority/ownership).</summary>
    public bool MoveTo(double x, double y, double z, bool run) =>
        NpcWorld.Instance?.ApplyIntent(_plugin, _id, NpcIntent.MoveTo(x, y, z, run)) == true;

    /// <summary>Clears movement; the AI wanders near the spawn point.</summary>
    public bool Idle() => NpcWorld.Instance?.ApplyIntent(_plugin, _id, NpcIntent.Idle) == true;

    [JsNullable]
    public string? State(string key) => NpcWorld.Instance?.StateGet(_plugin, _id, key);

    public bool SetState(string key, string value) =>
        NpcWorld.Instance?.StateSet(_plugin, _id, key, value) == true;

    public bool RemoveState(string key) =>
        NpcWorld.Instance?.StateRemove(_plugin, _id, key) == true;

    public string[] StateKeys() => NpcWorld.Instance?.StateKeys(_plugin, _id) ?? Array.Empty<string>();

    [JsIgnore] internal NpcPerception Perception() => NpcWorld.Instance?.Perception(_plugin, _id.ToString()) ?? EmptyPerception();

    private NpcPerception EmptyPerception() => new()
    {
        Id = Id,
        Kind = _kind,
        X = X,
        Y = Y,
        Z = Z,
        Health = Health,
        MaxHealth = MaxHealth
    };
}
