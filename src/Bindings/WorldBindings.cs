using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimJS.Core;
using ValheimJS.Scripting.Binding;
using JsonText = ValheimJS.Utils.Json;
using Object = UnityEngine.Object;

namespace ValheimJS.Bindings;

/// <summary>
/// World objects: counters over the ZDO table, spawning over <b>existing</b> prefabs, and data
/// attached to a spawned object.
/// <para>
/// Spawning is server-side in practice: instantiating a prefab that carries a <c>ZNetView</c>
/// makes the game create and replicate a ZDO, so clients see it without extra work. Handles are
/// session-scoped and owned by the plugin that spawned them — persist your own ids if you need
/// them across restarts.
/// </para>
/// </summary>
[JsBinder("world", Phase = JsBindPhase.Gameplay, Order = 120,
    Description = "Live world objects: counts, spawning and per-object data.")]
internal sealed class WorldBindings : JsBindings
{
    /// <summary>The live object table is private; reflection happens once, not per call.</summary>
    private static readonly FieldInfo? ObjectsField = typeof(ZDOMan).GetField(
        "m_objectsByID",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly SpawnHandleTable<ZDOID> _handles = new();

    public WorldBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding(Description = "Number of live world objects (props, creatures, structures).")]
    public int ZdoCount()
    {
        var manager = ZDOMan.instance;
        if (manager == null || ObjectsField == null)
        {
            return 0;
        }

        return ObjectsField.GetValue(manager) is System.Collections.ICollection objects ? objects.Count : 0;
    }

    [JsBinding(Description = "False if this server build hides the object table.")]
    public bool ZdoAvailable() => ObjectsField != null;

    [JsBinding(Scoped = true, Returns = "string | null",
        Description = "Instantiates an existing networked prefab on the world authority; returns a session handle or null.")]
    public string? Spawn(string plugin, string prefabName, float x, float y, float z)
    {
        if (!RequireAuthority(plugin, "spawn"))
        {
            return null;
        }

        var scene = ZNetScene.instance;
        if (scene == null || string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        var prefab = scene.GetPrefab(prefabName);
        if (prefab == null)
        {
            LogWarn($"unknown prefab '{prefabName}'");
            return null;
        }

        var instance = Object.Instantiate(prefab, new Vector3(x, y, z), Quaternion.identity);
        if (instance == null)
        {
            return null;
        }

        var view = instance.GetComponent<ZNetView>();
        if (view == null || view.GetZDO() == null)
        {
            // Not a networked prefab: nothing clients could see, so drop it instead of leaking.
            Object.Destroy(instance);
            LogWarn($"'{prefabName}' has no ZNetView; refusing to spawn a local-only object");
            return null;
        }

        if (!view.IsOwner())
        {
            // ZNetView.Awake creates an owned ZDO for an unclaimed instance; anything else means
            // this process would hold a replica it cannot write, so do not hand out a handle.
            Object.Destroy(instance);
            LogWarn($"[{plugin}] spawned '{prefabName}' without ZDO ownership; discarded so writes cannot silently drop");
            return null;
        }

        var handle = _handles.Add(plugin, view.GetZDO().m_uid);

        if (_handles.Count == 1024)
        {
            LogWarn("1024 live spawn handles; plugins should destroy what they create");
        }

        return handle;
    }

    [JsBinding(Scoped = true, Description = "Destroys an object this plugin spawned; no-op for an unknown or foreign handle.")]
    public void Destroy(string plugin, string handle)
    {
        var lookup = _handles.Resolve(plugin, handle, out var id);
        if (lookup != HandleLookup.Ok)
        {
            WarnLookup(plugin, "destroy", handle, lookup);
            return;
        }

        if (!RequireAuthority(plugin, "destroy"))
        {
            return;
        }

        var manager = ZDOMan.instance;
        var zdo = manager?.GetZDO(id);
        if (manager == null || zdo == null)
        {
            // The object is already gone; drop the stale handle.
            _handles.Take(plugin, handle, out _);
            return;
        }

        if (!zdo.IsOwner())
        {
            // ZDOMan.DestroyZDO only queues owner-side destruction; claiming would be a separate
            // authority transfer, so refuse instead of appearing to succeed and keep the handle.
            LogWarn($"[{plugin}] destroy('{handle}') skipped: this process is not the ZDO owner");
            return;
        }

        _handles.Take(plugin, handle, out _);
        manager.DestroyZDO(zdo);
    }

    [JsBinding(Description = "Prefab names matching an optional query; limit 0 returns everything.")]
    public string[] Prefabs(string query, int limit)
    {
        var scene = ZNetScene.instance;
        if (scene?.m_prefabs == null)
        {
            return Array.Empty<string>();
        }

        var names = scene.m_prefabs
            .Where(prefab => prefab != null)
            .Select(prefab => prefab.name)
            .Where(name => string.IsNullOrEmpty(query) || name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        return (limit > 0 ? names.Take(limit) : names).ToArray();
    }

    [JsBinding(Description = "True when the prefab exists in the world scene.")]
    public bool Exists(string prefabName) => ZNetScene.instance != null && ZNetScene.instance.GetPrefab(prefabName) != null;

    [JsBinding(Scoped = true, Returns = "string | null",
        Description = "Reads a custom string from an object this plugin spawned, or null.")]
    public string? GetData(string plugin, string handle, string key)
    {
        var lookup = _handles.Resolve(plugin, handle, out var id);
        if (lookup != HandleLookup.Ok)
        {
            WarnLookup(plugin, "getData", handle, lookup);
            return null;
        }

        return ResolveZdo(id)?.GetString(key, null);
    }

    [JsBinding(Scoped = true, Description = "Writes a custom string to an object this plugin spawned; replicated and saved.")]
    public void SetData(string plugin, string handle, string key, string value)
    {
        var lookup = _handles.Resolve(plugin, handle, out var id);
        if (lookup != HandleLookup.Ok)
        {
            WarnLookup(plugin, "setData", handle, lookup);
            return;
        }

        if (!RequireAuthority(plugin, "setData"))
        {
            return;
        }

        var zdo = ResolveZdo(id);
        if (zdo == null)
        {
            return;
        }

        if (!zdo.IsOwner())
        {
            // ZDO.Set on a replica updates only local state; refuse rather than pretend it synced.
            LogWarn($"[{plugin}] setData('{handle}') ignored: this process is not the ZDO owner");
            return;
        }

        zdo.Set(key, value);
    }

    [JsBinding(Scoped = true, Description = "True while an object this plugin spawned still exists.")]
    public bool Alive(string plugin, string handle)
    {
        var lookup = _handles.Resolve(plugin, handle, out var id);
        if (lookup != HandleLookup.Ok)
        {
            WarnLookup(plugin, "alive", handle, lookup);
            return false;
        }

        return ResolveZdo(id) != null;
    }

    [JsBinding(Scoped = true, Returns = "string | null",
        Description = "Stable ZDO id for a handle this plugin spawned, or null for an unknown or foreign handle.")]
    public string? IdOf(string plugin, string handle)
    {
        var lookup = _handles.Resolve(plugin, handle, out var id);
        if (lookup != HandleLookup.Ok)
        {
            WarnLookup(plugin, "id", handle, lookup);
            return null;
        }

        return id.ToString();
    }

    [JsBinding(ReturnKind = JsReturnKind.Json, Returns = "WorldCharacter[]",
        Description = "Live characters within a radius, nearest first; limit 0 returns everything.")]
    public string CharactersNear(float x, float y, float z, float radius, int limit)
    {
        var origin = new Vector3(x, y, z);
        var found = new List<(Character Character, float Distance)>();
        var characters = Character.GetAllCharacters();
        if (characters != null)
        {
            foreach (var character in characters)
            {
                if (character == null)
                {
                    continue;
                }

                var distance = Vector3.Distance(origin, character.transform.position);
                if (distance <= radius)
                {
                    found.Add((character, distance));
                }
            }
        }

        found.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        var text = new StringBuilder("[");
        var written = 0;
        foreach (var pair in found)
        {
            if (limit > 0 && written >= limit)
            {
                break;
            }

            var character = pair.Character;
            var view = character.GetComponent<ZNetView>();
            var zdo = view != null ? view.GetZDO() : null;
            var prefab = PrefabName(zdo, character.gameObject);
            if (written > 0)
            {
                text.Append(',');
            }

            text.Append("{\"id\":").Append(JsonText.Quote(zdo != null ? zdo.m_uid.ToString() : null))
                .Append(",\"prefab\":").Append(JsonText.Quote(prefab))
                .Append(",\"name\":").Append(JsonText.Quote(character.m_name))
                .Append(",\"x\":").Append(JsonText.Number(character.transform.position.x))
                .Append(",\"y\":").Append(JsonText.Number(character.transform.position.y))
                .Append(",\"z\":").Append(JsonText.Number(character.transform.position.z))
                .Append(",\"distance\":").Append(JsonText.Number(pair.Distance))
                .Append(",\"health\":").Append(JsonText.Number(character.GetHealth()))
                .Append(",\"maxHealth\":").Append(JsonText.Number(character.GetMaxHealth()))
                .Append(",\"isPlayer\":").Append(character.IsPlayer() ? "true" : "false")
                .Append(",\"isDead\":").Append(character.IsDead() ? "true" : "false")
                .Append(",\"isTamed\":").Append(character.IsTamed() ? "true" : "false")
                .Append('}');
            written++;
        }

        return text.Append(']').ToString();
    }

    [JsBinding(ReturnKind = JsReturnKind.Json, Returns = "WorldContainer[]",
        Description = "Containers within a radius, nearest first; limit 0 returns everything.")]
    public string ContainersNear(float x, float y, float z, float radius, int limit)
    {
        var origin = new Vector3(x, y, z);
        var found = new List<(Container Container, float Distance)>();
        foreach (var container in Object.FindObjectsOfType<Container>())
        {
            if (container == null)
            {
                continue;
            }

            var distance = Vector3.Distance(origin, container.transform.position);
            if (distance <= radius)
            {
                found.Add((container, distance));
            }
        }

        found.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        var text = new StringBuilder("[");
        var written = 0;
        foreach (var pair in found)
        {
            if (limit > 0 && written >= limit)
            {
                break;
            }

            var container = pair.Container;
            var view = container.GetComponent<ZNetView>();
            var zdo = view != null ? view.GetZDO() : null;
            var inventory = container.GetInventory();
            if (written > 0)
            {
                text.Append(',');
            }

            text.Append("{\"id\":").Append(JsonText.Quote(zdo != null ? zdo.m_uid.ToString() : null))
                .Append(",\"prefab\":").Append(JsonText.Quote(PrefabName(zdo, container.gameObject)))
                .Append(",\"x\":").Append(JsonText.Number(container.transform.position.x))
                .Append(",\"y\":").Append(JsonText.Number(container.transform.position.y))
                .Append(",\"z\":").Append(JsonText.Number(container.transform.position.z))
                .Append(",\"distance\":").Append(JsonText.Number(pair.Distance))
                .Append(",\"slots\":").Append(container.m_width * container.m_height)
                .Append(",\"items\":").Append(inventory != null ? inventory.NrOfItems() : 0)
                .Append('}');
            written++;
        }

        return text.Append(']').ToString();
    }

    private static string? PrefabName(ZDO? zdo, GameObject instance)
    {
        if (zdo != null)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            if (prefab != null)
            {
                return prefab.name;
            }
        }

        return instance != null ? instance.name : null;
    }

    /// <summary>Handles are in-memory, so a plugin unload forgets them; the world objects remain.</summary>
    public override void Release(string plugin) => _handles.Release(plugin);

    private bool RequireAuthority(string plugin, string operation)
    {
        if (GameEnvironment.IsServer())
        {
            return true;
        }

        LogWarn($"[{plugin}] {operation} refused: this process does not have live world authority");
        return false;
    }

    /// <summary>Unknown handles are a stale no-op; a foreign handle is worth a warning.</summary>
    private void WarnLookup(string plugin, string operation, string handle, HandleLookup lookup)
    {
        if (lookup == HandleLookup.Foreign)
        {
            LogWarn($"[{plugin}] {operation}('{handle}') ignored: handle belongs to another plugin");
        }
    }

    private static ZDO? ResolveZdo(ZDOID id) => ZDOMan.instance?.GetZDO(id);
}
