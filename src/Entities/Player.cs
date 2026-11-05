using System.Globalization;
using UnityEngine;
using ValheimJS.Scripting.Types;

namespace ValheimJS.Entities;

/// <summary>
/// One connected or local player, exposed to scripts as <c>ValheimPlayer</c>. All game objects
/// stay internal; identity, position and health are read from the replicated ZDO so the shape is
/// the same on a client, a player host and the dedicated server.
/// </summary>
[JsType("ValheimPlayer", Description = "A connected or local player.")]
public sealed class Player
{
    [JsIgnore] private long _uid;
    [JsIgnore] private long _platformId;
    [JsIgnore] private string _name;
    [JsIgnore] private ZDOID _characterId;
    [JsIgnore] private bool _isLocal;

    internal Player(long uid, long platformId, string name, ZDOID characterId, bool isLocal)
    {
        _uid = uid;
        _platformId = platformId;
        _name = name;
        _characterId = characterId;
        _isLocal = isLocal;
    }

    /// <summary>Routed peer id, as text; session-scoped addressing key.</summary>
    public string Uid => _uid.ToString(CultureInfo.InvariantCulture);

    /// <summary>Stable platform id (Steam/console) when known; null for unknown/server peers.</summary>
    [JsNullable]
    public string? PlatformId
    {
        get
        {
            var id = _platformId;
            var zdo = Zdo;
            if (id == 0L && zdo != null)
            {
                id = zdo.GetLong(ZDOVars.s_playerID, 0L);
            }

            return id == 0L ? null : id.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Display name; never an identity key.</summary>
    public string Name
    {
        get
        {
            var zdo = Zdo;
            return zdo != null ? zdo.GetString(ZDOVars.s_playerName, _name) : _name;
        }
    }

    /// <summary>True only on the process that owns this player's local player object.</summary>
    public bool IsLocal => _isLocal;

    public double X => Zdo?.GetPosition().x ?? 0d;

    public double Y => Zdo?.GetPosition().y ?? 0d;

    public double Z => Zdo?.GetPosition().z ?? 0d;

    public float Health
    {
        get
        {
            var zdo = Zdo;
            return zdo != null ? zdo.GetFloat(ZDOVars.s_health, MaxHealth) : 0f;
        }
    }

    public float MaxHealth
    {
        get
        {
            var zdo = Zdo;
            return zdo != null ? zdo.GetFloat(ZDOVars.s_maxHealth, FallbackMaxHealth(zdo)) : 0f;
        }
    }

    public bool IsAlive
    {
        get
        {
            var zdo = Zdo;
            if (zdo == null)
            {
                return false;
            }

            var scene = ZNetScene.instance;
            var instance = scene != null ? scene.FindInstance(_characterId) : null;
            if (instance != null)
            {
                var character = instance.GetComponent<Character>();
                if (character != null)
                {
                    return !character.IsDead();
                }
            }

            return Health > 0f;
        }
    }

    internal long UidValue => _uid;

    internal void Refresh(long platformId, string name, ZDOID characterId, bool isLocal)
    {
        _platformId = platformId;
        _name = name;
        _characterId = characterId;
        _isLocal = isLocal;
    }

    private ZDO? Zdo => ZDOMan.instance?.GetZDO(_characterId);

    private static float FallbackMaxHealth(ZDO zdo)
    {
        var scene = ZNetScene.instance;
        if (scene == null)
        {
            return 0f;
        }

        var prefab = scene.GetPrefab(zdo.GetPrefab());
        var character = prefab != null ? prefab.GetComponent<Character>() : null;
        return character != null ? character.m_health : 0f;
    }
}
