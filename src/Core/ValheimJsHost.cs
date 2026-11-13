using UnityEngine;

namespace ValheimJS.Core;

/// <summary>
/// One surviving root for host-owned objects. BepInEx owns the plugin component; everything
/// ValheimJS creates at runtime hangs below this object so teardown and scene changes have one
/// owner. UI, world rendering, debug overlays and prefab containers are children rather than
/// separate DontDestroyOnLoad roots.
/// </summary>
internal static class ValheimJsHost
{
    private static GameObject? _root;
    private static Transform? _ui;
    private static Transform? _world;
    private static Transform? _prefabs;
    private static Transform? _services;

    internal static GameObject Root
    {
        get
        {
            Ensure();
            return _root!;
        }
    }

    internal static Transform UiRoot => Child(ref _ui, "UI");

    internal static Transform WorldRoot => Child(ref _world, "World");

    internal static Transform PrefabRoot => Child(ref _prefabs, "Prefabs");

    internal static Transform ServicesRoot => Child(ref _services, "Services");

    internal static void Ensure()
    {
        if (_root != null)
        {
            return;
        }

        _root = new GameObject("ValheimJS");
        Object.DontDestroyOnLoad(_root);
    }

    internal static void Dispose()
    {
        if (_root != null)
        {
            Object.Destroy(_root);
        }

        _root = null;
        _ui = null;
        _world = null;
        _prefabs = null;
        _services = null;
    }

    private static Transform Child(ref Transform? field, string name)
    {
        Ensure();
        if (field != null)
        {
            return field;
        }

        var existing = _root!.transform.Find(name);
        if (existing != null)
        {
            field = existing;
            return existing;
        }

        var child = new GameObject(name);
        child.transform.SetParent(_root!.transform, false);
        field = child.transform;
        return child.transform;
    }
}
