using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ValheimJS.Entities;

namespace ValheimJS.World;

/// <summary>
/// Clones a vanilla prefab into a scripted NPC prefab and registers it in the world scene.
/// Recipe mirrors the proven companion-mod approach: hidden container, base AI/talk/drop removed,
/// client presenter added, then `m_prefabs` + `m_namedPrefabs` updated.
/// </summary>
internal static class NpcPrefabs
{
    private static readonly FieldInfo? NamedPrefabsField = AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs");

    private static GameObject? _container;
    private static ZNetScene? _scene;
    private static readonly HashSet<string> Applied = new(System.StringComparer.Ordinal);

    internal static string PrefabName(string kind) => "vjs_npc_" + kind;

    internal static void TryApply(NpcDefinition definition)
    {
        var scene = ZNetScene.instance;
        if (scene == null)
        {
            return;
        }

        if (!ReferenceEquals(_scene, scene))
        {
            _scene = scene;
            Applied.Clear();
            if (_container != null)
            {
                Object.Destroy(_container);
                _container = null;
            }
        }

        var name = PrefabName(definition.Kind);
        if (scene.GetPrefab(name) != null)
        {
            return;
        }

        var source = scene.GetPrefab(definition.Prefab);
        if (source == null)
        {
            PluginLogger.Warn($"npc prefab: source '{definition.Prefab}' not found for kind '{definition.Kind}'");
            return;
        }

        if (_container == null)
        {
            _container = new GameObject("ValheimJS_NPC_Prefabs");
            _container.hideFlags = HideFlags.HideAndDontSave;
            _container.SetActive(false);
            Object.DontDestroyOnLoad(_container);
        }

        var clone = Object.Instantiate(source, _container.transform, false);
        clone.name = name;

        if (definition.Faction != null)
        {
            if (System.Enum.TryParse<Character.Faction>(definition.Faction, true, out var faction))
            {
                foreach (var character in clone.GetComponents<Character>())
                {
                    character.m_faction = faction;
                }
            }
            else
            {
                PluginLogger.Warn($"npc prefab: unknown faction '{definition.Faction}' for kind '{definition.Kind}'");
            }
        }

        foreach (var ai in clone.GetComponents<BaseAI>())
        {
            Object.DestroyImmediate(ai);
        }

        foreach (var drop in clone.GetComponents<CharacterDrop>())
        {
            Object.DestroyImmediate(drop);
        }

        foreach (var talk in clone.GetComponents<NpcTalk>())
        {
            Object.DestroyImmediate(talk);
        }

        if (clone.GetComponent<ZNetView>() == null)
        {
            Object.Destroy(clone);
            PluginLogger.Warn($"npc prefab: '{definition.Prefab}' has no ZNetView");
            return;
        }

        clone.AddComponent<NpcVisualAI>();
        clone.AddComponent<NpcPresenter>();

        scene.m_prefabs.Add(clone);
        if (NamedPrefabsField?.GetValue(scene) is Dictionary<int, GameObject> named)
        {
            named[clone.name.GetStableHashCode()] = clone;
        }
        else
        {
            PluginLogger.Warn("npc prefab: m_namedPrefabs field not found");
            scene.m_prefabs.Remove(clone);
            Object.Destroy(clone);
            return;
        }

        Applied.Add(definition.Kind);
    }
}
