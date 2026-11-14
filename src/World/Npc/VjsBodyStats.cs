using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using ValheimJS.Entities;

namespace ValheimJS.World;

/// <summary>
/// Server-owned body stats for a native NPC body. Health is native (<see cref="Character"/>);
/// stamina and food slots are ValheimJS-owned because a plain Humanoid has no Player stamina,
/// food or skill systems. Values persist in the ZDO so a culled body resumes where it left off.
/// </summary>
internal sealed class VjsBodyStats : MonoBehaviour
{
    private const int MaxFoodSlots = 3;
    private const float BaseStamina = 100f;
    private const float StaminaRegen = 8f;
    private const float StaminaRegenDelay = 2f;
    private const float RunStaminaDrain = 8f;
    private const float SwimStaminaDrain = 4f;
    private const float SaveSeconds = 5f;
    private const float FoodRegenSeconds = 10f;

    private struct FoodSlot
    {
        internal int PrefabHash;
        internal string Name;
        internal float Remaining;
        internal float Total;
        internal float Health;
        internal float Stamina;
        internal float Eitr;
        internal float Regen;

        internal bool Active => Remaining > 0f && PrefabHash != 0;
    }

    private ZNetView? _nview;
    private ZDO? _zdo;
    private Humanoid? _humanoid;
    private Character? _character;
    private SEMan? _seman;
    private ZSyncAnimation? _zanim;
    private readonly FoodSlot[] _food = new FoodSlot[MaxFoodSlots];
    private float _baseMaxHealth = 10f;
    private float _stamina = BaseStamina;
    private float _regenTimer;
    private float _regenDelay;
    private float _saveAt;
    private float _healthRegenTimer;
    private bool _dirty;
    private bool _loaded;

    internal bool Ready { get; private set; }

    internal float Stamina => _stamina;

    internal float MaxStamina => BaseStamina + FoodSum(slot => slot.Stamina);

    internal int FoodCount
    {
        get
        {
            var count = 0;
            foreach (var slot in _food)
            {
                if (slot.Active) count++;
            }

            return count;
        }
    }

    private void Awake()
    {
        _nview = GetComponent<ZNetView>();
        _zdo = _nview?.GetZDO();
        _humanoid = GetComponent<Humanoid>();
        _character = GetComponent<Character>();
        _seman = _character?.GetSEMan();
        _zanim = GetComponent<ZSyncAnimation>();
        if (_character != null)
        {
            _baseMaxHealth = Mathf.Max(1f, _character.GetMaxHealthBase());
        }
    }

    private void Start()
    {
        Load();
        RecalculateMaxima();
        if (_nview != null && _nview.IsOwner() && _humanoid != null && _zdo != null)
        {
            var initialized = _zdo.GetBool(WorldKeys.InventoryInitialized, false);
            var hasSaved = _zdo.GetString(WorldKeys.Inventory, out var saved) && !string.IsNullOrEmpty(saved);
            VjsInventory.Restore(_humanoid, _zdo);
            if (!initialized)
            {
                if (!hasSaved)
                {
                    var inventory = _humanoid.GetInventory();
                    foreach (var item in NpcPrefabs.StartingItemsFor(gameObject.name))
                    {
                        if (inventory != null && VjsInventory.Add(inventory, item, 1, 1) == 0)
                        {
                            PluginLogger.Warn($"npc body: starting item '{item}' was not found");
                        }
                    }
                }

                _zdo.Set(WorldKeys.InventoryInitialized, true);
                VjsInventory.Save(_humanoid, _zdo);
            }

            if (!HasEquippedWeapon(_humanoid))
            {
                VjsInventory.EquipFirstWeapon(_humanoid);
                VjsInventory.Save(_humanoid, _zdo);
            }
        }

        Ready = true;
    }

    private static bool HasEquippedWeapon(Humanoid humanoid) =>
        (humanoid.RightItem != null && humanoid.RightItem.IsWeapon()) ||
        (humanoid.LeftItem != null && humanoid.LeftItem.IsWeapon());

    private void Update()
    {
        if (!_loaded || _nview == null || !_nview.IsOwner() || _character == null || _humanoid == null)
        {
            return;
        }

        var dt = Time.deltaTime;
        UpdateFood(dt);
        UpdateStamina(dt);
        if (Time.time >= _saveAt)
        {
            _saveAt = Time.time + SaveSeconds;
            if (_dirty)
            {
                Save();
            }
        }
    }

    private void OnDestroy()
    {
        Ready = false;
        if (_nview == null || _zdo == null || !_nview.IsOwner())
        {
            return;
        }

        Save();
        if (_humanoid != null)
        {
            VjsInventory.Save(_humanoid, _zdo);
        }
    }

    /// <summary>Plain snapshot for scripts; no CLR types leave the host.</summary>
    internal Dictionary<string, object?> GetView()
    {
        var health = _character != null ? _character.GetHealth() : 0f;
        var maxHealth = _character != null ? _character.GetMaxHealth() : 0f;
        var maxStamina = MaxStamina;
        var food = new List<object?>();
        foreach (var slot in _food)
        {
            if (!slot.Active) continue;
            food.Add(new Dictionary<string, object?>
            {
                ["prefab"] = slot.PrefabHash,
                ["name"] = slot.Name,
                ["remaining"] = slot.Remaining,
                ["total"] = slot.Total,
                ["health"] = slot.Health,
                ["stamina"] = slot.Stamina,
                ["eitr"] = slot.Eitr,
                ["regen"] = slot.Regen
            });
        }

        var effects = new List<object?>();
        if (_seman != null)
        {
            foreach (var effect in _seman.GetStatusEffects())
            {
                if (effect == null) continue;
                effects.Add(new Dictionary<string, object?>
                {
                    ["name"] = effect.m_name,
                    ["remaining"] = effect.GetRemaningTime(),
                    ["duration"] = effect.m_ttl
                });
            }
        }

        return new Dictionary<string, object?>
        {
            ["health"] = health,
            ["maxHealth"] = maxHealth,
            ["healthRatio"] = maxHealth > 0f ? health / maxHealth : 0f,
            ["stamina"] = _stamina,
            ["maxStamina"] = maxStamina,
            ["staminaRatio"] = maxStamina > 0f ? _stamina / maxStamina : 0f,
            ["eitr"] = 0f,
            ["maxEitr"] = 0f,
            ["eitrRatio"] = 0f,
            ["food"] = food,
            ["statusEffects"] = effects,
            ["hungry"] = FoodCount == 0
        };
    }

    internal bool CanConsume(ItemDrop.ItemData? item)
    {
        if (item == null || !IsFood(item.m_shared)) return false;
        var hash = PrefabHash(item);
        var free = false;
        for (var index = 0; index < MaxFoodSlots; index++)
        {
            var slot = _food[index];
            if (!slot.Active)
            {
                free = true;
                continue;
            }

            if (slot.PrefabHash == hash && slot.Remaining < slot.Total * 0.5f)
            {
                return true;
            }
        }

        return free;
    }

    internal bool TryConsume(ItemDrop.ItemData item, out string? reason)
    {
        reason = null;
        if (_nview == null || !_nview.IsOwner() || _humanoid == null)
        {
            reason = "not-owner";
            return false;
        }

        var inventory = _humanoid.GetInventory();
        if (inventory == null || !inventory.ContainsItem(item))
        {
            reason = "no-item";
            return false;
        }

        if (!IsFood(item.m_shared))
        {
            reason = "not-food";
            return false;
        }

        if (!CanConsume(item))
        {
            reason = FoodCount >= MaxFoodSlots ? "slots-full" : "cannot-eat";
            return false;
        }

        if (!inventory.RemoveItem(item, 1))
        {
            reason = "no-item";
            return false;
        }

        var slotIndex = FindFoodSlot(item);
        var total = Mathf.Max(1f, item.m_shared.m_foodBurnTime);
        _food[slotIndex] = new FoodSlot
        {
            PrefabHash = PrefabHash(item),
            Name = item.m_shared.m_name,
            Remaining = total,
            Total = total,
            Health = item.m_shared.m_food,
            Stamina = item.m_shared.m_foodStamina,
            Eitr = item.m_shared.m_foodEitr,
            Regen = item.m_shared.m_foodRegen
        };

        RecalculateMaxima();
        _humanoid.m_consumeItemEffects.Create(transform.position, Quaternion.identity, null, 1f, -1, _character?.GetZDOID() ?? ZDOID.None);
        _zanim?.SetTrigger("eat");
        if (item.m_shared.m_consumeStatusEffect != null)
        {
            _seman?.AddStatusEffect(item.m_shared.m_consumeStatusEffect, resetTime: true, item.m_quality, 0f, -1);
        }

        _dirty = true;
        Save();
        return true;
    }

    private int FindFoodSlot(ItemDrop.ItemData item)
    {
        var hash = PrefabHash(item);
        var lowest = 0;
        for (var index = 0; index < MaxFoodSlots; index++)
        {
            var slot = _food[index];
            if (!slot.Active) return index;
            if (slot.PrefabHash == hash) return index;
            if (slot.Remaining < _food[lowest].Remaining) lowest = index;
        }

        return lowest;
    }

    private void UpdateFood(float dt)
    {
        for (var index = 0; index < MaxFoodSlots; index++)
        {
            var slot = _food[index];
            if (!slot.Active) continue;
            slot.Remaining -= dt;
            if (slot.Remaining <= 0f)
            {
                slot = default;
                _dirty = true;
            }

            _food[index] = slot;
        }

        _healthRegenTimer += dt;
        if (_healthRegenTimer < FoodRegenSeconds) return;
        _healthRegenTimer = 0f;
        var regen = FoodSum(slot => slot.Regen);
        if (regen > 0f && _character != null && _character.GetHealth() < _character.GetMaxHealth())
        {
            _character.Heal(regen);
        }
    }

    private void UpdateStamina(float dt)
    {
        if (_character == null || _humanoid == null) return;
        var velocity = _character.GetVelocity();
        velocity.y = 0f;
        var speed = velocity.magnitude;
        var draining = false;
        if (_character.IsSwimming() && speed > 0.25f)
        {
            _stamina -= SwimStaminaDrain * dt;
            draining = true;
        }
        else if (speed > Mathf.Max(0.1f, _humanoid.m_walkSpeed * 1.05f))
        {
            _stamina -= RunStaminaDrain * dt;
            draining = true;
        }

        if (draining)
        {
            _regenDelay = StaminaRegenDelay;
        }
        else
        {
            _regenDelay = Mathf.Max(0f, _regenDelay - dt);
            if (_regenDelay <= 0f)
            {
                _stamina += StaminaRegen * dt;
            }
        }

        var max = MaxStamina;
        var clamped = Mathf.Clamp(_stamina, 0f, max);
        if (Mathf.Abs(clamped - _stamina) > 0.0001f)
        {
            _stamina = clamped;
            _dirty = true;
        }
    }

    private void RecalculateMaxima()
    {
        if (_character == null) return;
        var maxHealth = _baseMaxHealth + FoodSum(slot => slot.Health);
        _character.SetMaxHealth(Mathf.Max(1f, maxHealth));
        _stamina = Mathf.Clamp(_stamina, 0f, MaxStamina);
    }

    private float FoodSum(Func<FoodSlot, float> selector)
    {
        var total = 0f;
        foreach (var slot in _food)
        {
            if (slot.Active) total += selector(slot);
        }

        return total;
    }

    private void Save()
    {
        if (_zdo == null) return;
        _dirty = false;
        _zdo.Set(WorldKeys.Stats, _stamina.ToString("R", CultureInfo.InvariantCulture));
        var builder = new StringBuilder();
        foreach (var slot in _food)
        {
            if (!slot.Active) continue;
            if (builder.Length > 0) builder.Append('|');
            builder.Append(slot.PrefabHash.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(slot.Remaining.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(slot.Total.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(slot.Health.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(slot.Stamina.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(slot.Eitr.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(slot.Regen.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append(slot.Name);
        }

        _zdo.Set(WorldKeys.Food, builder.ToString());
    }

    private void Load()
    {
        _loaded = true;
        if (_zdo == null) return;
        if (_zdo.GetString(WorldKeys.Stats, out var stats) &&
            float.TryParse(stats, NumberStyles.Float, CultureInfo.InvariantCulture, out var stamina))
        {
            _stamina = Mathf.Clamp(stamina, 0f, MaxStamina);
        }

        if (!_zdo.GetString(WorldKeys.Food, out var food) || string.IsNullOrEmpty(food)) return;
        var entries = food.Split('|');
        for (var index = 0; index < entries.Length && index < MaxFoodSlots; index++)
        {
            var parts = entries[index].Split(':');
            if (parts.Length < 7) continue;
            if (!int.TryParse(parts[0], out var hash) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var remaining) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var total) ||
                !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var health) ||
                !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var staminaBonus) ||
                !float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var eitr) ||
                !float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var regen))
            {
                continue;
            }

            _food[index] = new FoodSlot
            {
                PrefabHash = hash,
                Name = parts.Length > 7 ? parts[7] : string.Empty,
                Remaining = Mathf.Max(0f, remaining),
                Total = Mathf.Max(1f, total),
                Health = health,
                Stamina = staminaBonus,
                Eitr = eitr,
                Regen = regen
            };
        }
    }

    private static bool IsFood(ItemDrop.ItemData.SharedData shared) =>
        shared != null && (shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f);

    private static int PrefabHash(ItemDrop.ItemData item) =>
        item.m_dropPrefab != null ? item.m_dropPrefab.name.GetStableHashCode() : item.m_shared.m_name.GetStableHashCode();
}
