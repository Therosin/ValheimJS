using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using ValheimJS.Scripting.Binding;
using Object = UnityEngine.Object;

namespace ValheimJS.Bindings;

/// <summary>
/// World objects: counters over the ZDO table, spawning over <b>existing</b> prefabs, and data
/// attached to a spawned object.
/// <para>
/// Spawning is server-side in practice: instantiating a prefab that carries a <c>ZNetView</c>
/// makes the game create and replicate a ZDO, so clients see it without extra work. Handles are
/// session-scoped — persist your own ids if you need them across restarts.
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

    private readonly Dictionary<string, ZDOID> _handles = new(StringComparer.Ordinal);
    private int _next;

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

    [JsBinding(Returns = "string | null", Description = "Instantiates an existing networked prefab; returns a session handle or null.")]
    public string? Spawn(string prefabName, float x, float y, float z)
    {
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

        var handle = "spawn-" + (++_next);
        _handles[handle] = view.GetZDO().m_uid;

        if (_handles.Count == 1024)
        {
            LogWarn("1024 live spawn handles; plugins should destroy what they create");
        }

        return handle;
    }

    [JsBinding(Description = "Destroys an object created with spawn(); no-op for an unknown handle.")]
    public void Destroy(string handle)
    {
        if (!_handles.TryGetValue(handle, out var id))
        {
            return;
        }

        _handles.Remove(handle);
        var manager = ZDOMan.instance;
        var zdo = manager?.GetZDO(id);
        if (manager != null && zdo != null)
        {
            manager.DestroyZDO(zdo);
        }
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

    [JsBinding(Returns = "string | null", Description = "Reads a custom string from a spawned object's ZDO, or null.")]
    public string? GetData(string handle, string key) => ResolveZdo(handle)?.GetString(key, null);

    [JsBinding(Description = "Writes a custom string to a spawned object's ZDO; replicated and saved.")]
    public void SetData(string handle, string key, string value) => ResolveZdo(handle)?.Set(key, value);

    [JsBinding(Description = "True while the object behind a handle still exists.")]
    public bool Alive(string handle) => ResolveZdo(handle) != null;

    private ZDO? ResolveZdo(string handle) =>
        _handles.TryGetValue(handle, out var id) ? ZDOMan.instance?.GetZDO(id) : null;
}
