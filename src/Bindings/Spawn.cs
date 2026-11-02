using System;
using ValheimJS.Scripting.Binding;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimJS.Bindings;

/// <summary>
/// Object spawning over <b>existing</b> prefabs. Scripts see <c>host.api.spawn.*</c>.
/// <para>
/// Server-side only in practice: instantiating a prefab that carries a <c>ZNetView</c> makes the
/// game create and replicate a ZDO, so clients see it without any extra work from us. Handles are
/// session-scoped — persist your own ids if you need them across restarts.
/// </para>
/// </summary>
[JsBinding("Spawn", JsBindPhase.Gameplay, order: 170)]
internal static class SpawnBindings
{
    private static readonly Dictionary<string, ZDOID> Handles = new(StringComparer.Ordinal);
    private static int _next;

    public static void Bind(JsBindingContext context)
    {
        context.Add("spawn.create", new Func<string, float, float, float, string?>(Create));
        context.Add("spawn.destroy", new Action<string>(Destroy));
        context.Add("spawn.prefabs", new Func<string, int, string[]>(Prefabs));
        context.Add("spawn.exists", new Func<string, bool>(name => ZNetScene.instance != null && ZNetScene.instance.GetPrefab(name) != null));

        // Data that belongs to an object: stored on its ZDO, so the game replicates it with the
        // object's own dirty bits and saves it with the world — no custom packets, no polling.
        context.Add("spawn.get", new Func<string, string, string?>(GetData));
        context.Add("spawn.set", new Action<string, string, string>(SetData));
        context.Add("spawn.alive", new Func<string, bool>(handle => ResolveZdo(handle) != null));
    }

    private static string? Create(string prefabName, float x, float y, float z)
    {
        var scene = ZNetScene.instance;
        if (scene == null || string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        var prefab = scene.GetPrefab(prefabName);
        if (prefab == null)
        {
            Plugin.Logger.LogWarning($"[Spawn] unknown prefab '{prefabName}'");
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
            Plugin.Logger.LogWarning($"[Spawn] '{prefabName}' has no ZNetView; refusing to spawn a local-only object");
            return null;
        }

        var handle = "spawn-" + (++_next);
        Handles[handle] = view.GetZDO().m_uid;

        if (Handles.Count == 1024)
        {
            Plugin.Logger.LogWarning("[Spawn] 1024 live spawn handles; plugins should destroy what they create");
        }

        return handle;
    }

    private static void Destroy(string handle)
    {
        if (!Handles.TryGetValue(handle, out var id))
        {
            return;
        }

        Handles.Remove(handle);
        var manager = ZDOMan.instance;
        var zdo = manager?.GetZDO(id);
        if (manager != null && zdo != null)
        {
            manager.DestroyZDO(zdo);
        }
    }

    private static ZDO? ResolveZdo(string handle)
    {
        if (!Handles.TryGetValue(handle, out var id))
        {
            return null;
        }

        return ZDOMan.instance?.GetZDO(id);
    }

    private static string? GetData(string handle, string key)
    {
        var zdo = ResolveZdo(handle);
        return zdo == null ? null : zdo.GetString(key, null);
    }

    private static void SetData(string handle, string key, string value)
    {
        var zdo = ResolveZdo(handle);
        zdo?.Set(key, value);
    }

    private static string[] Prefabs(string query, int limit)
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
}
