using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ValheimJS.Core;
using UnityEngine;
using ValheimJS.Entities;

namespace ValheimJS.World;

/// <summary>
/// Builds the native body prefab for an NPC kind. The default human body is a Player clone with
/// the Player component replaced by a plain Humanoid; other Character prefabs are cloned as-is
/// (minus their native decision AI). Every body gets a <see cref="VjsNpcAI"/> controller.
/// </summary>
internal static class NpcPrefabs
{
    private static readonly FieldInfo? NamedPrefabsField = AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs");
    private static readonly FieldInfo? AnimCharacterField = AccessTools.Field(typeof(CharacterAnimEvent), "m_character");
    private static readonly FieldInfo? VisPlayerField = AccessTools.Field(typeof(VisEquipment), "m_playerComponent");
    private static readonly FieldInfo? VisIsPlayerField = AccessTools.Field(typeof(VisEquipment), "m_isPlayer");

    private static GameObject? _container;
    private static ZNetScene? _scene;
    private static readonly Dictionary<string, IReadOnlyList<string>> StartingItems = new(StringComparer.Ordinal);

    internal static string PrefabName(string kind) => "vjs_npc_" + kind;

    internal static IReadOnlyList<string> StartingItemsFor(string objectName)
    {
        var clone = objectName.IndexOf("(Clone)", StringComparison.Ordinal);
        var name = clone >= 0 ? objectName[..clone] : objectName;
        return StartingItems.TryGetValue(name, out var items) ? items : Array.Empty<string>();
    }

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
            StartingItems.Clear();
            if (_container != null)
            {
                UnityEngine.Object.Destroy(_container);
                _container = null;
            }
        }

        var name = PrefabName(definition.Kind);
        StartingItems[name] = definition.Items;
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
            _container = new GameObject("NPC_Prefabs");
            _container.transform.SetParent(ValheimJsHost.PrefabRoot, false);
            _container.hideFlags = HideFlags.HideAndDontSave;
            _container.SetActive(false);
        }

        var clone = UnityEngine.Object.Instantiate(source, _container.transform, false);
        clone.name = name;

        var character = clone.GetComponent<Character>();
        if (character == null)
        {
            UnityEngine.Object.Destroy(clone);
            PluginLogger.Warn($"npc prefab: source '{definition.Prefab}' has no Character");
            return;
        }

        if (definition.Human || clone.GetComponent<Player>() != null)
        {
            ConvertHuman(clone);
        }

        RemoveNativeDecisions(clone);
        foreach (var characterPart in clone.GetComponents<Character>())
        {
            if (definition.Faction != null && !string.IsNullOrWhiteSpace(definition.Faction) &&
                Enum.TryParse<Character.Faction>(definition.Faction, true, out var faction))
            {
                characterPart.m_faction = faction;
            }

            characterPart.m_name = definition.Kind;
        }

        if (clone.GetComponent<Humanoid>() != null)
        {
            ConfigureHumanoid(clone.GetComponent<Humanoid>(), definition);
        }

        ConfigureNetworking(clone);
        var ai = clone.AddComponent<VjsNpcAI>();
        clone.AddComponent<VjsBodyStats>();
        ai.m_pathAgentType = Pathfinding.AgentType.Humanoid;
        ai.m_viewRange = definition.Sensors.ViewRange;
        ai.m_viewAngle = definition.Sensors.ViewAngle;
        ai.m_hearRange = definition.Sensors.Hearing > 0f
            ? definition.Sensors.Hearing
            : definition.Sensors.Radius;
        ai.m_avoidFire = true;
        ai.m_avoidWater = true;
        ai.m_avoidLava = true;
        ai.AutoEngage = definition.AutoEngage;
        ai.AutoOpenDoors = definition.AutoOpenDoors;

        clone.SetActive(true);
        if (NamedPrefabsField?.GetValue(scene) is not Dictionary<int, GameObject> named)
        {
            UnityEngine.Object.Destroy(clone);
            PluginLogger.Warn("npc prefab: m_namedPrefabs field not found");
            return;
        }

        scene.m_prefabs.Add(clone);
        named[clone.name.GetStableHashCode()] = clone;
    }

    private static void ConvertHuman(GameObject clone)
    {
        var player = clone.GetComponent<Player>();
        if (player == null)
        {
            return;
        }

        var values = CaptureFields(player);
        var humanoid = clone.AddComponent<Humanoid>();
        foreach (var pair in values)
        {
            pair.Key.SetValue(humanoid, pair.Value);
        }

        DestroyComponent<PlayerController>(clone);
        DestroyComponent<Talker>(clone);
        DestroyComponent<Skills>(clone);
        UnityEngine.Object.DestroyImmediate(player);

        var anim = clone.GetComponentInChildren<CharacterAnimEvent>();
        if (anim != null && AnimCharacterField != null)
        {
            AnimCharacterField.SetValue(anim, humanoid);
        }

        var vis = clone.GetComponent<VisEquipment>();
        if (vis != null)
        {
            VisPlayerField?.SetValue(vis, null);
            VisIsPlayerField?.SetValue(vis, false);
        }
    }

    private static List<KeyValuePair<FieldInfo, object?>> CaptureFields(Player player)
    {
        var values = new List<KeyValuePair<FieldInfo, object?>>();
        foreach (var type in new[] { typeof(Character), typeof(Humanoid) })
        {
            foreach (var field in type.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance |
                                                 BindingFlags.Public | BindingFlags.NonPublic))
            {
                values.Add(new KeyValuePair<FieldInfo, object?>(field, field.GetValue(player)));
            }
        }

        return values;
    }

    private static void RemoveNativeDecisions(GameObject clone)
    {
        foreach (var ai in clone.GetComponents<BaseAI>())
        {
            UnityEngine.Object.DestroyImmediate(ai);
        }

        foreach (var drop in clone.GetComponents<CharacterDrop>())
        {
            UnityEngine.Object.DestroyImmediate(drop);
        }

        foreach (var talk in clone.GetComponents<NpcTalk>())
        {
            UnityEngine.Object.DestroyImmediate(talk);
        }
    }

    private static void ConfigureHumanoid(Humanoid humanoid, NpcDefinition definition)
    {
        humanoid.m_name = definition.Kind;
        humanoid.m_defaultItems = Array.Empty<GameObject>();
        humanoid.m_randomWeapon = Array.Empty<GameObject>();
        humanoid.m_randomArmor = Array.Empty<GameObject>();
        humanoid.m_randomShield = Array.Empty<GameObject>();
        humanoid.m_randomSets = Array.Empty<Humanoid.ItemSet>();
        humanoid.m_randomItems = Array.Empty<Humanoid.RandomItem>();
        humanoid.m_health = Math.Max(1f, humanoid.m_health);
        humanoid.m_walkSpeed = 2f;
        humanoid.m_runSpeed = 5f;
        humanoid.m_turnSpeed = 100f;
        humanoid.m_runTurnSpeed = 160f;
        humanoid.m_canSwim = true;
    }

    private static void ConfigureNetworking(GameObject clone)
    {
        var view = clone.GetComponent<ZNetView>();
        if (view == null)
        {
            PluginLogger.Warn($"npc prefab: '{clone.name}' has no ZNetView");
            return;
        }

        view.m_persistent = true;
        view.m_distant = false;
        view.m_type = ZDO.ObjectType.Default;
        view.m_syncInitialScale = false;

        var sync = clone.GetComponent<ZSyncTransform>();
        if (sync != null)
        {
            sync.m_syncPosition = true;
            sync.m_syncRotation = true;
            sync.m_syncScale = false;
            sync.m_syncBodyVelocity = false;
            sync.m_characterParentSync = false;
        }

        var animation = clone.GetComponent<ZSyncAnimation>();
        if (animation != null)
        {
            animation.m_smoothCharacterSpeeds = true;
        }
    }

    private static void DestroyComponent<T>(GameObject clone) where T : Component
    {
        var component = clone.GetComponent<T>();
        if (component != null)
        {
            UnityEngine.Object.DestroyImmediate(component);
        }
    }
}
