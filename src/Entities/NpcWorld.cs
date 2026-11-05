using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimJS.Core;

namespace ValheimJS.Entities;

/// <summary>
/// Native owner of NPC definitions and entities. Handles prefab registration, spawning,
/// persistence keys, intent delivery, perception and durable string state. `BaseNPC` is the
/// script-facing facade; nothing here touches Jint.
/// </summary>
internal sealed class NpcWorld : IDisposable
{
    private const string KeyPlugin = "vjs.plugin";
    private const string KeyKind = "vjs.kind";
    private const string KeyName = "vjs.name";
    private const string StatePrefix = "vjs.s.";
    private const string StateIndex = "vjs.sk";

    internal static NpcWorld? Instance { get; private set; }

    private readonly Dictionary<string, NpcDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entity> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BaseNPC> _facades = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _lastScan = new(StringComparer.Ordinal);

    internal NpcWorld()
    {
        Instance = this;
    }

    internal bool Available => ZDOMan.instance != null && ZNetScene.instance != null;

    internal sealed class Entity
    {
        internal ZDOID Id;
        internal string Plugin = "";
        internal string Kind = "";
        internal NpcIntent Intent = NpcIntent.Idle;
        internal int Generation;
        internal (bool Ok, string? Reason)? Result;
    }

    internal bool Register(string plugin, string kind, string prefab, double tickMs)
    {
        if (!NpcDefinition.TryCreate(kind, prefab, tickMs, out var definition, out var error))
        {
            PluginLogger.Warn(error);
            return false;
        }

        _definitions[Key(plugin, kind)] = definition;
        NpcPrefabs.TryApply(definition);
        return true;
    }

    internal string? Spawn(string plugin, string kind, float x, float y, float z, string? name)
    {
        if (!Available || !GameEnvironment.IsServer())
        {
            PluginLogger.Warn("npc spawn refused: no world authority");
            return null;
        }

        if (!_definitions.TryGetValue(Key(plugin, kind), out var definition))
        {
            PluginLogger.Warn($"npc spawn refused: unknown kind '{kind}'");
            return null;
        }

        var prefabName = NpcPrefabs.PrefabName(kind);
        var prefab = ZNetScene.instance.GetPrefab(prefabName);
        if (prefab == null)
        {
            PluginLogger.Warn($"npc spawn refused: prefab '{prefabName}' is not registered");
            return null;
        }

        var instance = UnityEngine.Object.Instantiate(prefab, new Vector3(x, y, z), Quaternion.identity);
        var view = instance != null ? instance.GetComponent<ZNetView>() : null;
        var zdo = view != null ? view.GetZDO() : null;
        if (instance == null || view == null || zdo == null || !view.IsOwner())
        {
            if (instance != null)
            {
                UnityEngine.Object.Destroy(instance);
            }

            PluginLogger.Warn($"npc spawn refused: '{prefabName}' has no owned ZNetView");
            return null;
        }

        zdo.Persistent = true;
        zdo.Set(KeyPlugin, plugin);
        zdo.Set(KeyKind, kind);
        zdo.Set(KeyName, name ?? kind);

        var id = zdo.m_uid.ToString();
        _entities[id] = new Entity
        {
            Id = zdo.m_uid,
            Plugin = plugin,
            Kind = kind
        };

        return id;
    }

    internal bool Despawn(string plugin, string handle)
    {
        var entity = Find(handle);
        if (entity == null)
        {
            return false;
        }

        if (!string.Equals(entity.Plugin, plugin, StringComparison.Ordinal))
        {
            PluginLogger.Warn("npc despawn ignored: handle belongs to another plugin");
            return false;
        }

        var scene = ZNetScene.instance;
        var instance = scene != null ? scene.FindInstance(entity.Id) : null;
        if (instance != null)
        {
            scene!.Destroy(instance);
        }
        else if (Zdo(entity.Id) is { } zdo)
        {
            ZDOMan.instance?.DestroyZDO(zdo);
        }

        Remove(handle);
        return true;
    }

    internal BaseNPC? Resolve(string plugin, string handle)
    {
        var entity = Find(handle);
        if (entity == null || !string.Equals(entity.Plugin, plugin, StringComparison.Ordinal))
        {
            return null;
        }

        if (!_facades.TryGetValue(handle, out var facade))
        {
            facade = new BaseNPC(plugin, entity.Id, entity.Kind, Zdo(entity.Id)?.GetString(KeyName, entity.Kind) ?? entity.Kind);
            _facades[handle] = facade;
        }

        return facade;
    }

    internal IReadOnlyList<string> Handles(string plugin, string kind)
    {
        var key = Key(plugin, kind);
        var now = Time.realtimeSinceStartup;
        if (!_lastScan.TryGetValue(key, out var last) || now - last > 10f)
        {
            Scan(plugin, kind);
            _lastScan[key] = now;
        }

        return _entities.Values
            .Where(entity => string.Equals(entity.Plugin, plugin, StringComparison.Ordinal) &&
                             string.Equals(entity.Kind, kind, StringComparison.Ordinal))
            .Select(entity => entity.Id.ToString())
            .ToList();
    }

    internal NpcPerception? Perception(string plugin, string handle)
    {
        var entity = Find(handle);
        if (entity == null || !string.Equals(entity.Plugin, plugin, StringComparison.Ordinal))
        {
            return null;
        }

        var scene = ZNetScene.instance;
        var instance = scene?.FindInstance(entity.Id);
        if (instance == null)
        {
            return null;
        }

        var character = instance.GetComponent<Character>();
        if (character == null)
        {
            return null;
        }

        var position = character.transform.position;
        var nearby = new List<NpcNearbyEntry>();
        var characters = Character.GetAllCharacters();
        if (characters != null)
        {
            foreach (var other in characters)
            {
                if (other == null || other == character)
                {
                    continue;
                }

                var distance = Vector3.Distance(position, other.transform.position);
                if (distance > 20f)
                {
                    continue;
                }

                nearby.Add(new NpcNearbyEntry
                {
                    Id = other.GetZDOID().ToString(),
                    Prefab = PrefabName(other),
                    Name = PrefabName(other) ?? other.m_name,
                    X = other.transform.position.x,
                    Y = other.transform.position.y,
                    Z = other.transform.position.z,
                    Distance = distance,
                    Health = other.GetHealth(),
                    IsPlayer = other.IsPlayer()
                });
            }
        }

        nearby.Sort((left, right) => left.Distance.CompareTo(right.Distance));

        return new NpcPerception
        {
            Id = handle,
            Kind = entity.Kind,
            X = position.x,
            Y = position.y,
            Z = position.z,
            Health = character.GetHealth(),
            MaxHealth = character.GetMaxHealth(),
            LastIntentKind = entity.Result?.Ok == true ? null : entity.Intent.Kind,
            LastIntentOk = entity.Result?.Ok,
            LastIntentReason = entity.Result?.Reason,
            Nearby = nearby.Take(8).ToList()
        };
    }

    internal bool ApplyIntent(string plugin, ZDOID id, NpcIntent intent)
    {
        if (!GameEnvironment.IsServer())
        {
            PluginLogger.Warn("npc intent refused: no world authority");
            return false;
        }

        var entity = Find(id.ToString());
        if (entity == null || !string.Equals(entity.Plugin, plugin, StringComparison.Ordinal))
        {
            return false;
        }

        entity.Intent = intent;
        entity.Generation++;
        entity.Result = null;
        return true;
    }

    internal bool GetIntent(ZDOID id, out NpcIntent intent, out int generation)
    {
        var entity = Find(id.ToString());
        if (entity == null)
        {
            intent = NpcIntent.Idle;
            generation = 0;
            return false;
        }

        intent = entity.Intent;
        generation = entity.Generation;
        return true;
    }

    internal void Report(ZDOID id, bool ok, string? reason)
    {
        var entity = Find(id.ToString());
        if (entity != null)
        {
            entity.Result = (ok, reason);
        }
    }

    internal string? StateGet(string plugin, ZDOID id, string key)
    {
        if (!ValidStateKey(key) || !Owner(plugin, id))
        {
            return null;
        }

        var zdo = Zdo(id);
        return zdo != null && zdo.GetString(StatePrefix + key, out var value) ? value : null;
    }

    internal bool StateSet(string plugin, ZDOID id, string key, string value)
    {
        if (!ValidStateKey(key))
        {
            throw new ArgumentException("npc state: invalid key");
        }

        if (!GameEnvironment.IsServer())
        {
            PluginLogger.Warn("npc state write refused: no world authority");
            return false;
        }

        var zdo = Zdo(id);
        if (!Owner(plugin, id) || zdo == null || !zdo.IsOwner())
        {
            return false;
        }

        zdo.Set(StatePrefix + key, value ?? string.Empty);
        AddIndexKey(zdo, key);
        return true;
    }

    internal bool StateRemove(string plugin, ZDOID id, string key)
    {
        if (!ValidStateKey(key))
        {
            throw new ArgumentException("npc state: invalid key");
        }

        var zdo = Zdo(id);
        if (!Owner(plugin, id) || zdo == null || !zdo.IsOwner())
        {
            return false;
        }

        var removed = zdo.RemoveString((StatePrefix + key).GetStableHashCode());
        RemoveIndexKey(zdo, key);
        return removed;
    }

    internal string[] StateKeys(string plugin, ZDOID id)
    {
        if (!Owner(plugin, id))
        {
            return Array.Empty<string>();
        }

        var zdo = Zdo(id);
        if (zdo == null || !zdo.GetString(StateIndex, out var index) || string.IsNullOrEmpty(index))
        {
            return Array.Empty<string>();
        }

        return index.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    }

    internal void ApplyPendingPrefabs()
    {
        if (!Available)
        {
            return;
        }

        foreach (var definition in _definitions.Values)
        {
            NpcPrefabs.TryApply(definition);
        }
    }

    public void Dispose()
    {
        _definitions.Clear();
        _entities.Clear();
        _facades.Clear();
        _lastScan.Clear();
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    internal static ZDO? Zdo(ZDOID id) => ZDOMan.instance?.GetZDO(id);

    internal static float HealthOf(ZDOID id, float fallback)
    {
        var zdo = Zdo(id);
        return zdo != null ? zdo.GetFloat(ZDOVars.s_health, fallback) : 0f;
    }

    internal static float MaxHealthOf(ZDOID id)
    {
        var zdo = Zdo(id);
        if (zdo == null)
        {
            return 0f;
        }

        var scene = ZNetScene.instance;
        var prefab = scene != null ? scene.GetPrefab(zdo.GetPrefab()) : null;
        var character = prefab != null ? prefab.GetComponent<Character>() : null;
        return zdo.GetFloat(ZDOVars.s_maxHealth, character != null ? character.m_health : 0f);
    }

    internal static bool IsAlive(ZDOID id, float health)
    {
        var scene = ZNetScene.instance;
        var instance = scene != null ? scene.FindInstance(id) : null;
        if (instance != null)
        {
            var character = instance.GetComponent<Character>();
            if (character != null)
            {
                return !character.IsDead();
            }
        }

        return health > 0f;
    }

    private void Scan(string plugin, string kind)
    {
        var zdo = ZDOMan.instance;
        var scene = ZNetScene.instance;
        if (zdo == null || scene == null)
        {
            return;
        }

        var prefab = NpcPrefabs.PrefabName(kind);
        var found = new List<ZDO>();
        var index = 0;
        while (!zdo.GetAllZDOsWithPrefabIterative(prefab, found, ref index))
        {
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in found)
        {
            if (!string.Equals(candidate.GetString(KeyPlugin, ""), plugin, StringComparison.Ordinal) ||
                !string.Equals(candidate.GetString(KeyKind, ""), kind, StringComparison.Ordinal))
            {
                continue;
            }

            var handle = candidate.m_uid.ToString();
            seen.Add(handle);
            if (!_entities.ContainsKey(handle))
            {
                _entities[handle] = new Entity { Id = candidate.m_uid, Plugin = plugin, Kind = kind };
            }
        }

        var stale = _entities
            .Where(pair => string.Equals(pair.Value.Plugin, plugin, StringComparison.Ordinal) &&
                           string.Equals(pair.Value.Kind, kind, StringComparison.Ordinal) &&
                           !seen.Contains(pair.Key) &&
                           Zdo(pair.Value.Id) == null)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var handle in stale)
        {
            Remove(handle);
        }
    }

    private Entity? Find(string handle) => _entities.TryGetValue(handle, out var entity) ? entity : null;

    private bool Owner(string plugin, ZDOID id)
    {
        var entity = Find(id.ToString());
        return entity != null && string.Equals(entity.Plugin, plugin, StringComparison.Ordinal);
    }

    private void Remove(string handle)
    {
        _entities.Remove(handle);
        _facades.Remove(handle);
    }

    private static bool ValidStateKey(string key) =>
        !string.IsNullOrEmpty(key) && System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9_.-]{1,48}$");

    private static void AddIndexKey(ZDO zdo, string key)
    {
        var keys = new List<string>();
        if (zdo.GetString(StateIndex, out var index) && !string.IsNullOrEmpty(index))
        {
            keys.AddRange(index.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        }

        if (!keys.Contains(key, StringComparer.Ordinal))
        {
            keys.Add(key);
            zdo.Set(StateIndex, string.Join(",", keys));
        }
    }

    private static void RemoveIndexKey(ZDO zdo, string key)
    {
        if (!zdo.GetString(StateIndex, out var index) || string.IsNullOrEmpty(index))
        {
            return;
        }

        var keys = index.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(value => !string.Equals(value, key, StringComparison.Ordinal))
            .ToList();
        zdo.Set(StateIndex, string.Join(",", keys));
    }

    private static string Key(string plugin, string kind) => plugin + "\u001f" + kind;

    private static string? PrefabName(Character character)
    {
        var view = character.GetComponent<ZNetView>();
        var zdo = view != null ? view.GetZDO() : null;
        var prefab = zdo != null && ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
        return prefab != null ? prefab.name : null;
    }
}

/// <summary>Small logging seam so the NPC world keeps using the plugin logger.</summary>
internal static class PluginLogger
{
    internal static void Warn(string message) => Plugin.Logger.LogWarning(message);
}
