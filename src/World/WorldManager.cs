using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using ValheimJS.Core;
using ValheimJS.Entities;
using ValheimJS.Networking;
using ValheimJS.World.Navigation;

namespace ValheimJS.World;

/// <summary>
/// Authority-owned registry and simulator for world objects. The server is both brain and body:
/// it ticks behaviours and writes position/rotation/state into ZDOs; clients only mirror
/// relevance metadata and render engine-instantiated copies.
/// </summary>
internal sealed class WorldManager : IDisposable
{
    private readonly Core.Networking _network;
    private readonly NetworkScope _scope;
    private readonly NetworkChannel _channel;
    private readonly string _navRoot;
    private readonly Dictionary<string, WorldObject> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorldKind> _kinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorldObject> _subscribed = new(StringComparer.Ordinal);
    private readonly Dictionary<long, HashSet<string>> _peerSets = new();
    private NavGridNavigation? _navigation;
    private long _navWorldUid;
    private readonly List<(string Handle, float X, float Z)> _relevancyObjects = new();
    private float _nextRehydrate;
    private float _nextRelevancy;
    private float _nextSync;
    private bool _syncInFlight;
    private bool _synced;
    private bool _disposed;

    internal static WorldManager? Instance { get; private set; }

    internal WorldManager(Core.Networking networking, string navRoot)
    {
        _network = networking;
        _navRoot = navRoot;
        _scope = networking.OpenScope();
        _channel = _scope.Channel(WorldKeys.Channel, "world");
        _channel.OnJson("relevance", OnRelevance);
        _channel.OnJson("entity", OnEntity);
        _channel.HandleJson("sync", HandleSync);
        Instance = this;
    }

    internal bool Available => ZDOMan.instance != null && ZNetScene.instance != null;

    internal bool Authority => GameEnvironment.IsServer();

    internal NavGridNavigation? Navigation
    {
        get
        {
            var generator = WorldGenerator.instance;
            var zoneSystem = ZoneSystem.instance;
            if (generator == null || zoneSystem == null)
            {
                return null;
            }

            var worldUid = ZNet.instance != null ? ZNet.instance.GetWorldUID() : 0;
            if (_navigation != null && _navWorldUid == worldUid)
            {
                return _navigation;
            }

            var volume = new NavVolume(
                new WorldGeneratorTerrain(),
                new ZdoObstacleSource(),
                _navRoot,
                worldUid,
                zoneSystem.m_waterLevel);
            _navigation = new NavGridNavigation(volume);
            _navWorldUid = worldUid;
            return _navigation;
        }
    }

    internal NavVolume? NavVolume => Navigation?.Volume;

    internal void RegisterKind(WorldKind kind)
    {
        if (string.IsNullOrEmpty(kind.Name))
        {
            throw new ArgumentException("world kind: name is required");
        }

        _kinds[kind.Name] = kind;
        try
        {
            kind.Prepare?.Invoke();
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"world kind '{kind.Name}' prepare failed: {exception.Message}");
        }
    }

    internal void ApplyPendingPrefabs()
    {
        if (!Available)
        {
            return;
        }

        foreach (var kind in _kinds.Values)
        {
            try
            {
                kind.Prepare?.Invoke();
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"world kind '{kind.Name}' prepare failed: {exception.Message}");
            }
        }
    }

    internal string? Spawn(string plugin, string kind, float x, float y, float z, string? name)
    {
        if (!Authority)
        {
            Plugin.Logger.LogWarning("world spawn refused: no authority");
            return null;
        }

        if (!_kinds.TryGetValue(kind, out var kindInfo))
        {
            Plugin.Logger.LogWarning($"world spawn refused: unknown kind '{kind}'");
            return null;
        }

        kindInfo.Prepare?.Invoke();
        var prefab = ZNetScene.instance?.GetPrefab(kindInfo.Prefab);
        if (prefab == null)
        {
            Plugin.Logger.LogWarning($"world spawn refused: prefab '{kindInfo.Prefab}' is not registered");
            return null;
        }

        var zdo = ZDOMan.instance?.CreateNewZDO(new Vector3(x, y, z), prefab.name.GetStableHashCode());
        if (zdo == null)
        {
            Plugin.Logger.LogWarning($"world spawn refused: could not create a ZDO for '{prefab.name}'");
            return null;
        }

        zdo.Persistent = true;
        zdo.SetPrefab(prefab.name.GetStableHashCode());
        zdo.Set(WorldKeys.Sim, "server");
        zdo.Set(WorldKeys.Plugin, plugin);
        zdo.Set(WorldKeys.Kind, kind);
        zdo.Set(WorldKeys.Name, name ?? kind);
        zdo.Set(WorldKeys.HomeX, x);
        zdo.Set(WorldKeys.HomeY, y);
        zdo.Set(WorldKeys.HomeZ, z);
        zdo.Set(WorldKeys.Speed, 0f);
        zdo.Set(WorldKeys.Moving, false);
        zdo.Set(WorldKeys.Generation, 0);
        zdo.SetRotation(Quaternion.identity);

        var handle = zdo.m_uid.ToString();
        var obj = new WorldObject
        {
            Id = zdo.m_uid,
            Handle = handle,
            Kind = kind,
            Plugin = plugin,
            Name = name ?? kind,
            Position = new Vector3(x, y, z),
            Home = new Vector3(x, y, z),
            KindInfo = kindInfo
        };
        obj.Behavior = kindInfo.Create(obj);
        _objects[handle] = obj;
        return handle;
    }

    internal bool Despawn(string plugin, string handle)
    {
        if (!Authority)
        {
            return false;
        }

        if (!_objects.TryGetValue(handle, out var obj) ||
            !string.Equals(obj.Plugin, plugin, StringComparison.Ordinal))
        {
            return false;
        }

        var zdo = obj.Zdo;
        if (zdo != null)
        {
            if (!zdo.IsOwner())
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
            }

            ZDOMan.instance?.DestroyZDO(zdo);
        }

        _objects.Remove(handle);
        return true;
    }

    internal IReadOnlyList<string> Handles(string plugin, string kind)
    {
        var source = Authority ? _objects.Values : _subscribed.Values;
        return source
            .Where(obj => string.Equals(obj.Plugin, plugin, StringComparison.Ordinal) &&
                          string.Equals(obj.Kind, kind, StringComparison.Ordinal))
            .Select(obj => obj.Handle)
            .ToList();
    }

    internal WorldObject? Resolve(string plugin, string handle)
    {
        WorldObject? obj;
        if (!_objects.TryGetValue(handle, out obj) && !_subscribed.TryGetValue(handle, out obj))
        {
            return null;
        }

        return string.Equals(obj.Plugin, plugin, StringComparison.Ordinal) ? obj : null;
    }

    internal bool Command(string plugin, string handle, string command, params object?[] args)
    {
        if (!Authority)
        {
            return false;
        }

        var obj = Resolve(plugin, handle);
        return obj != null && obj.Behavior.Command(obj, command, args);
    }

    internal object? Snapshot(string plugin, string handle)
    {
        if (!Authority)
        {
            return null;
        }

        var obj = Resolve(plugin, handle);
        return obj?.Behavior.Snapshot(obj);
    }

    internal void Tick(float deltaSeconds)
    {
        if (_disposed)
        {
            return;
        }

        if (Authority)
        {
            ServerTick(deltaSeconds);
        }
        else
        {
            ClientTick();
        }
    }

    internal void ReclaimOwnership()
    {
        if (!Authority)
        {
            return;
        }

        var session = ZDOMan.GetSessionID();
        foreach (var obj in _objects.Values)
        {
            var zdo = obj.Zdo;
            if (zdo != null && !zdo.IsOwner())
            {
                zdo.SetOwner(session);
            }
        }
    }

    internal string? StateGet(string plugin, string handle, string key)
    {
        if (!ValidStateKey(key) || Resolve(plugin, handle) == null)
        {
            return null;
        }

        var zdo = Zdo(handle);
        return zdo != null && zdo.GetString(WorldKeys.StatePrefix + key, out var value) ? value : null;
    }

    internal bool StateSet(string plugin, string handle, string key, string value)
    {
        if (!ValidStateKey(key))
        {
            throw new ArgumentException("world state: invalid key");
        }

        var obj = Resolve(plugin, handle);
        var zdo = obj?.Zdo;
        if (!Authority || zdo == null)
        {
            return false;
        }

        EnsureOwned(zdo);
        zdo.Set(WorldKeys.StatePrefix + key, value ?? string.Empty);
        AddIndexKey(zdo, key);
        return true;
    }

    internal bool StateRemove(string plugin, string handle, string key)
    {
        if (!ValidStateKey(key))
        {
            throw new ArgumentException("world state: invalid key");
        }

        var obj = Resolve(plugin, handle);
        var zdo = obj?.Zdo;
        if (!Authority || zdo == null)
        {
            return false;
        }

        EnsureOwned(zdo);
        var removed = zdo.RemoveString((WorldKeys.StatePrefix + key).GetStableHashCode());
        RemoveIndexKey(zdo, key);
        return removed;
    }

    internal IReadOnlyList<NpcSensedEntity> Senses(string plugin, string handle, NpcSenseQuery query)
    {
        var obj = Resolve(plugin, handle);
        if (obj == null || obj.Behavior is not INpcSenses senses)
        {
            return Array.Empty<NpcSensedEntity>();
        }

        obj.Behavior.Snapshot(obj);
        return senses.Senses(query);
    }

    internal IReadOnlyList<NpcSensedEntity> TakeNewSenses(string plugin, string handle)
    {
        var obj = Resolve(plugin, handle);
        return obj?.Behavior is INpcSenses senses ? senses.TakeNewSenses() : Array.Empty<NpcSensedEntity>();
    }

    internal string? MemoryGet(string plugin, string handle)
    {
        if (Resolve(plugin, handle) == null)
        {
            return null;
        }

        var zdo = Zdo(handle);
        return zdo != null && zdo.GetString(WorldKeys.Memory, out var value) ? value : null;
    }

    internal bool MemorySet(string plugin, string handle, string json)
    {
        if (json.Length > 16384)
        {
            throw new ArgumentException("npc memory: JSON exceeds 16 KiB");
        }

        var obj = Resolve(plugin, handle);
        var zdo = obj?.Zdo;
        if (!Authority || zdo == null)
        {
            return false;
        }

        EnsureOwned(zdo);
        zdo.Set(WorldKeys.Memory, json);
        return true;
    }

    internal string[] StateKeys(string plugin, string handle)
    {
        var obj = Resolve(plugin, handle);
        var zdo = obj?.Zdo;
        if (zdo == null || !zdo.GetString(WorldKeys.StateIndex, out var index) || string.IsNullOrEmpty(index))
        {
            return Array.Empty<string>();
        }

        return index.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    }

    internal static ZDO? Zdo(string handle)
    {
        return TryParseHandle(handle, out var id) ? ZDOMan.instance?.GetZDO(id) : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _objects.Clear();
        _subscribed.Clear();
        _kinds.Clear();
        _peerSets.Clear();
        _scope.Dispose();
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    private void ServerTick(float deltaSeconds)
    {
        if (!Available)
        {
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (now >= _nextRehydrate)
        {
            _nextRehydrate = now + 10f;
            Rehydrate();
        }

        Navigation?.BuildPendingZone();

        foreach (var obj in _objects.Values.ToList())
        {
            if (obj.Zdo == null)
            {
                continue;
            }

            obj.TickAccumulator += deltaSeconds * 1000d;
            if (obj.TickAccumulator >= obj.KindInfo.TickMs)
            {
                var step = (float)(obj.TickAccumulator / 1000d);
                obj.TickAccumulator = 0d;
                obj.Behavior.Tick(obj, step);
            }

            if (now - obj.LastWrite >= 0.2f)
            {
                WriteObject(obj);
            }
        }

        if (now >= _nextRelevancy)
        {
            _nextRelevancy = now + 1f;
            TickRelevancy();
        }
    }

    private void ClientTick()
    {
        if (!_network.Available || _network.ServerPeer == 0 || !ClientWorldReady())
        {
            _synced = false;
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (_syncInFlight || now < _nextSync)
        {
            return;
        }

        _syncInFlight = true;
        _nextSync = now + (_synced ? 30f : 5f);
        _channel.RequestJson(_network.ServerPeer, "sync", "{}", OnSync, 5000);
    }

    /// <summary>
    /// A connected peer is not enough: the client can still be on the loading screen while the
    /// server is already reachable. Sync only after the local character exists and teleport/load
    /// transitions have settled.
    /// </summary>
    private static bool ClientWorldReady() =>
        ZNetScene.instance != null &&
        Player.m_localPlayer != null &&
        !Player.m_localPlayer.IsTeleporting();

    private void OnSync(string? error, string? json)
    {
        _syncInFlight = false;
        if (!string.IsNullOrEmpty(error))
        {
            _synced = false;
            Plugin.Logger.LogWarning("world sync failed: " + error);
            return;
        }

        if (string.IsNullOrEmpty(json))
        {
            _synced = false;
            return;
        }

        try
        {
            var message = NetworkJson.Read<WorldRelevanceMessage>(json);
            _network.Post(() => ApplyRelevance(message));
            _synced = true;
        }
        catch (Exception exception)
        {
            _synced = false;
            Plugin.Logger.LogWarning("world sync failed: " + exception.Message);
        }
    }

    private string? HandleSync(NetworkMessage message, string json)
    {
        if (!Authority)
        {
            return null;
        }

        var peer = ZNet.instance?.GetPeers()?.FirstOrDefault(candidate => candidate != null && candidate.m_uid == message.Sender);
        if (peer == null || peer.m_server)
        {
            return NetworkJson.Write(new WorldRelevanceMessage());
        }

        var reference = PeerPosition(peer);
        if (reference == Vector3.zero)
        {
            return NetworkJson.Write(new WorldRelevanceMessage());
        }

        var add = _objects.Values
            .Where(obj => WorldRelevancy.IsRelevant(obj.Position.x, obj.Position.z, reference.x, reference.z,
                obj.KindInfo.RelevancyRadius, false))
            .Select(ToInfo)
            .ToArray();
        return NetworkJson.Write(new WorldRelevanceMessage { Add = add });
    }

    private void OnRelevance(NetworkMessage message, string json)
    {
        if (Authority)
        {
            return;
        }

        try
        {
            var relevance = NetworkJson.Read<WorldRelevanceMessage>(json);
            _network.Post(() => ApplyRelevance(relevance));
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("world relevance ignored: " + exception.Message);
        }
    }

    private void OnEntity(NetworkMessage message, string json)
    {
        if (Authority)
        {
            return;
        }

        try
        {
            var entity = NetworkJson.Read<WorldEntityMessage>(json);
            if (entity.Entity == null)
            {
                return;
            }

            _network.Post(() =>
            {
                if (string.Equals(entity.Op, "despawn", StringComparison.Ordinal))
                {
                    _subscribed.Remove(entity.Entity.Id);
                }
                else
                {
                    UpsertSubscribed(entity.Entity);
                }
            });
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("world entity ignored: " + exception.Message);
        }
    }

    private void ApplyRelevance(WorldRelevanceMessage message)
    {
        if (message.Remove != null)
        {
            foreach (var handle in message.Remove)
            {
                _subscribed.Remove(handle);
            }
        }

        if (message.Add != null)
        {
            foreach (var info in message.Add)
            {
                UpsertSubscribed(info);
            }
        }
    }

    private void UpsertSubscribed(WorldEntityInfo info)
    {
        if (info == null || string.IsNullOrEmpty(info.Id) || !TryParseHandle(info.Id, out var id))
        {
            return;
        }

        _subscribed[info.Id] = new WorldObject
        {
            Id = id,
            Handle = info.Id,
            Kind = info.Kind,
            Plugin = info.Plugin,
            Name = info.Name
        };
    }

    private void TickRelevancy()
    {
        var net = ZNet.instance;
        if (net == null)
        {
            return;
        }

        var peers = net.GetPeers();
        if (peers == null)
        {
            return;
        }

        var active = new HashSet<long>();
        _relevancyObjects.Clear();
        foreach (var obj in _objects.Values)
        {
            _relevancyObjects.Add((obj.Handle, obj.Position.x, obj.Position.z));
        }

        foreach (var peer in peers)
        {
            if (peer == null || peer.m_server || !peer.IsReady())
            {
                continue;
            }

            active.Add(peer.m_uid);
            if (!_peerSets.TryGetValue(peer.m_uid, out var previous))
            {
                previous = new HashSet<string>(StringComparer.Ordinal);
                _peerSets[peer.m_uid] = previous;
            }

            var reference = PeerPosition(peer);
            if (reference == Vector3.zero)
            {
                continue;
            }

            var next = new HashSet<string>(StringComparer.Ordinal);
            var add = new List<WorldEntityInfo>();
            var remove = new List<string>();
            foreach (var item in _relevancyObjects)
            {
                var obj = _objects[item.Handle];
                if (WorldRelevancy.IsRelevant(item.X, item.Z, reference.x, reference.z,
                        obj.KindInfo.RelevancyRadius, previous.Contains(item.Handle)))
                {
                    next.Add(item.Handle);
                    if (previous.Add(item.Handle))
                    {
                        add.Add(ToInfo(obj));
                    }
                }
            }

            foreach (var handle in previous)
            {
                if (!next.Contains(handle))
                {
                    remove.Add(handle);
                }
            }

            previous.Clear();
            previous.UnionWith(next);
            if (add.Count == 0 && remove.Count == 0)
            {
                continue;
            }

            _channel.EmitJson(peer.m_uid, "relevance", NetworkJson.Write(new WorldRelevanceMessage
            {
                Add = add.ToArray(),
                Remove = remove.ToArray()
            }));
        }

        foreach (var stale in _peerSets.Keys.Where(uid => !active.Contains(uid)).ToList())
        {
            _peerSets.Remove(stale);
        }
    }

    private static Vector3 PeerPosition(ZNetPeer peer)
    {
        var reference = peer.GetRefPos();
        if (reference == Vector3.zero)
        {
            var character = ZDOMan.instance?.GetZDO(peer.m_characterID);
            if (character != null)
            {
                reference = character.GetPosition();
            }
        }

        return reference;
    }

    private void Rehydrate()
    {
        var zdoManager = ZDOMan.instance;
        var scene = ZNetScene.instance;
        if (zdoManager == null || scene == null)
        {
            return;
        }

        foreach (var kind in _kinds.Values)
        {
            var prefab = scene.GetPrefab(kind.Prefab);
            if (prefab == null)
            {
                continue;
            }

            var found = new List<ZDO>();
            var index = 0;
            while (!zdoManager.GetAllZDOsWithPrefabIterative(kind.Prefab, found, ref index))
            {
            }

            foreach (var zdo in found)
            {
                if (!string.Equals(zdo.GetString(WorldKeys.Sim, ""), "server", StringComparison.Ordinal) ||
                    !string.Equals(zdo.GetString(WorldKeys.Kind, ""), kind.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                var handle = zdo.m_uid.ToString();
                if (_objects.ContainsKey(handle))
                {
                    continue;
                }

                var position = zdo.GetPosition();
                var obj = new WorldObject
                {
                    Id = zdo.m_uid,
                    Handle = handle,
                    Kind = kind.Name,
                    Plugin = zdo.GetString(WorldKeys.Plugin, ""),
                    Name = zdo.GetString(WorldKeys.Name, kind.Name),
                    Position = position,
                    Home = new Vector3(
                        zdo.GetFloat(WorldKeys.HomeX, position.x),
                        zdo.GetFloat(WorldKeys.HomeY, position.y),
                        zdo.GetFloat(WorldKeys.HomeZ, position.z)),
                    Generation = zdo.GetInt(WorldKeys.Generation, 0),
                    KindInfo = kind
                };
                obj.Behavior = kind.Create(obj);
                _objects[handle] = obj;
            }
        }
    }

    private void WriteObject(WorldObject obj)
    {
        var zdo = obj.Zdo;
        if (zdo == null)
        {
            return;
        }

        if (!zdo.IsOwner())
        {
            zdo.SetOwner(ZDOMan.GetSessionID());
            return;
        }

        zdo.SetPosition(obj.Position);
        zdo.SetRotation(Quaternion.Euler(0f, obj.Yaw, 0f));
        zdo.Set(WorldKeys.Speed, obj.Speed);
        zdo.Set(WorldKeys.Moving, obj.Moving);
        obj.LastWrite = Time.realtimeSinceStartup;
        obj.Behavior.Write(obj, zdo);
    }

    private static WorldEntityInfo ToInfo(WorldObject obj) => new()
    {
        Id = obj.Handle,
        Kind = obj.Kind,
        Plugin = obj.Plugin,
        Name = obj.Name
    };

    private static void EnsureOwned(ZDO zdo)
    {
        if (!zdo.IsOwner())
        {
            zdo.SetOwner(ZDOMan.GetSessionID());
        }
    }

    private static bool ValidStateKey(string key) =>
        !string.IsNullOrEmpty(key) && System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9_.-]{1,48}$");

    private static void AddIndexKey(ZDO zdo, string key)
    {
        var keys = new List<string>();
        if (zdo.GetString(WorldKeys.StateIndex, out var index) && !string.IsNullOrEmpty(index))
        {
            keys.AddRange(index.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        }

        if (!keys.Contains(key, StringComparer.Ordinal))
        {
            keys.Add(key);
            zdo.Set(WorldKeys.StateIndex, string.Join(",", keys));
        }
    }

    private static void RemoveIndexKey(ZDO zdo, string key)
    {
        if (!zdo.GetString(WorldKeys.StateIndex, out var index) || string.IsNullOrEmpty(index))
        {
            return;
        }

        var keys = index.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(value => !string.Equals(value, key, StringComparison.Ordinal))
            .ToList();
        zdo.Set(WorldKeys.StateIndex, string.Join(",", keys));
    }

    internal static bool TryParseHandle(string handle, out ZDOID id)
    {
        id = ZDOID.None;
        var separator = handle.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        if (!long.TryParse(handle.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var user) ||
            !uint.TryParse(handle.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        id = new ZDOID(user, value);
        return true;
    }
}
