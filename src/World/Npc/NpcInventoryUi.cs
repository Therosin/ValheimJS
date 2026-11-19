using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimJS.Core;
using ValheimJS.Entities;

namespace ValheimJS.World;

/// <summary>
/// Client-side vanilla container adapter for one NPC body. The real <see cref="Container"/> on the
/// body owns the mirror inventory shown by <see cref="InventoryGui"/>; the server owns the NPC
/// inventory and applies the mirror on every change. No custom inventory grid is involved.
/// </summary>
internal sealed class NpcInventoryUi : MonoBehaviour
{
    private const string OpenRpc = "VJS_NpcInventoryOpen";
    private const string SnapshotRpc = "VJS_NpcInventorySnapshot";
    private const string SyncRpc = "VJS_NpcInventorySync";
    private const string CloseRpc = "VJS_NpcInventoryClose";
    private const string RejectRpc = "VJS_NpcInventoryReject";

    private const float InteractionRange = 6f;
    private const float SyncInterval = 0.12f;
    private const float CloseDistance = 7f;

    private static readonly FieldInfo? InventoryNameField =
        typeof(Inventory).GetField("m_name", BindingFlags.Instance | BindingFlags.NonPublic);

#if USE_GUI
    private static readonly FieldInfo? CurrentContainerField =
        typeof(InventoryGui).GetField("m_currentContainer", BindingFlags.Instance | BindingFlags.NonPublic);
#endif

    private ZNetView? _nview;
    private Container? _container;
    private Humanoid? _humanoid;
    private VjsNpcAI? _ai;
    private Inventory? _mirror;
    private string _displayName = "Inventory";
    private bool _registered;
    private bool _open;
    private bool _opening;
    private bool _loading;
    private bool _dirty;
    private long _lockedPeer;
    private float _nextSync;

    /// <summary>True while the authority has handed this body's inventory to a client editor.</summary>
    internal bool IsLocked => _lockedPeer != 0L;

    internal string DisplayName => _displayName;

    private void Awake()
    {
        Ensure();
    }

    private void Start()
    {
        Ensure();
        Register();
    }

    private void OnDestroy()
    {
        if (_mirror != null)
        {
            _mirror.m_onChanged -= OnMirrorChanged;
        }

#if USE_GUI
        if (_open && !GameEnvironment.Dedicated)
        {
            CloseLocal(send: false);
        }
#endif
    }

    private void Ensure()
    {
        if (_nview == null) _nview = GetComponent<ZNetView>();
        if (_container == null) _container = GetComponent<Container>();
        if (_humanoid == null) _humanoid = GetComponent<Humanoid>();
        if (_ai == null) _ai = GetComponent<VjsNpcAI>();
        if (_container == null || _humanoid == null) return;

        var zdo = _nview?.GetZDO();
        _displayName = zdo != null ? zdo.GetString(WorldKeys.Name, _humanoid.m_name) : _humanoid.m_name;
        if (string.IsNullOrWhiteSpace(_displayName)) _displayName = "Inventory";
        _container.m_name = _displayName;

        var mirror = _container.GetInventory();
        if (mirror == null || ReferenceEquals(mirror, _mirror)) return;
        if (_mirror != null) _mirror.m_onChanged -= OnMirrorChanged;
        _mirror = mirror;
        _mirror.m_onChanged += OnMirrorChanged;
        InventoryNameField?.SetValue(_mirror, _displayName);
    }

    private void Register()
    {
        if (_registered || _nview == null || _nview.GetZDO() == null) return;
        _registered = true;
        _nview.Register(OpenRpc, OnOpenRequest);
        _nview.Register<string>(SnapshotRpc, OnSnapshot);
        _nview.Register<string>(SyncRpc, OnSync);
        _nview.Register(CloseRpc, OnCloseRequest);
        _nview.Register<string>(RejectRpc, OnReject);
    }

    private void Update()
    {
        if (!_registered) Register();

        if (GameEnvironment.IsServer())
        {
            if (_lockedPeer != 0L && GameEnvironment.Dedicated && ZNet.instance?.GetPeer(_lockedPeer) == null)
            {
                ReleaseLock();
            }
        }

#if USE_GUI
        if (!_open || GameEnvironment.Dedicated) return;
        if (_dirty && Time.time >= _nextSync)
        {
            _dirty = false;
            _nextSync = Time.time + SyncInterval;
            SendSync();
        }

        if (!IsCurrentContainer() || DistanceToLocalPlayer() > CloseDistance)
        {
            CloseLocal(send: true);
        }
#endif
    }

#if USE_GUI
    private bool IsCurrentContainer()
    {
        if (InventoryGui.instance == null || CurrentContainerField == null) return false;
        return ReferenceEquals(CurrentContainerField.GetValue(InventoryGui.instance), _container);
    }

    private float DistanceToLocalPlayer()
    {
        var player = Player.m_localPlayer;
        return player == null ? float.MaxValue : Vector3.Distance(player.transform.position, transform.position);
    }

    /// <summary>Called from the marked Container.Interact prefix on the UI process.</summary>
    internal bool TryInteract(Humanoid user, bool hold)
    {
        if (hold || GameEnvironment.Dedicated || _nview == null || _container == null || _humanoid == null)
        {
            return false;
        }

        if (Player.m_localPlayer == null || !ReferenceEquals(user, Player.m_localPlayer)) return false;
        if (_open)
        {
            InventoryGui.instance?.Show(_container, 1);
            return true;
        }

        if (_opening) return true;
        _opening = true;
        _nview.InvokeRPC(OpenRpc);
        return true;
    }

    internal string HoverText()
    {
        if (GameEnvironment.Dedicated) return string.Empty;
        var text = _displayName;
        if (_mirror != null && _mirror.NrOfItems() == 0)
        {
            text += " ($piece_container_empty)";
        }

        return Localization.instance.Localize(text + "\n[<color=yellow><b>$KEY_Use</b></color>] $piece_container_open");
    }

    internal string HoverName() => _displayName;

    private void OpenLocal(string payload)
    {
        if (_container == null || _mirror == null)
        {
            PluginLogger.Warn($"npc inventory: could not open UI for '{name}': no mirror");
            _opening = false;
            return;
        }

        if (!VjsInventory.TryDecode(_mirror, payload, out var error))
        {
            PluginLogger.Warn($"npc inventory: could not open UI for '{name}': {error}");
            _opening = false;
            return;
        }

        _loading = true;
        try
        {
            foreach (var item in _mirror.GetAllItems())
            {
                item.m_equipped = false;
            }
        }
        finally
        {
            _loading = false;
        }

        _open = true;
        _opening = false;
        _dirty = false;
        _nextSync = Time.time + SyncInterval;
        InventoryGui.instance?.Show(_container, 1);
    }

    private void CloseLocal(bool send)
    {
        if (!_open && !_opening) return;
        if (send)
        {
            SendSync();
            _nview?.InvokeRPC(CloseRpc);
        }

        _open = false;
        _opening = false;
        _dirty = false;

        if (InventoryGui.instance != null && IsCurrentContainer())
        {
            InventoryGui.instance.Hide();
        }
    }

    private void SendSync()
    {
        if (_mirror == null || _nview == null) return;
        var payload = VjsInventory.Encode(_mirror);
        if (payload == null) return;
        _nview.InvokeRPC(SyncRpc, payload);
    }
#else
    internal bool TryInteract(Humanoid user, bool hold) => false;
    internal string HoverText() => string.Empty;
    internal string HoverName() => _displayName;
#endif

    private void OnMirrorChanged()
    {
#if USE_GUI
        if (_loading || !_open || GameEnvironment.Dedicated) return;
        _dirty = true;
#endif
    }

    private void OnOpenRequest(long sender)
    {
        if (!GameEnvironment.IsServer() || _humanoid == null) return;

        if (_lockedPeer != 0L && _lockedPeer != sender)
        {
            _nview?.InvokeRPC(sender, RejectRpc, "in-use");
            return;
        }

        if (!CanOpen(sender))
        {
            _nview?.InvokeRPC(sender, RejectRpc, "out-of-range");
            return;
        }

        var inventory = _humanoid.GetInventory();
        var payload = VjsInventory.Encode(inventory);
        if (payload == null)
        {
            _nview?.InvokeRPC(sender, RejectRpc, "no-inventory");
            return;
        }

        _lockedPeer = sender;
        _nview?.InvokeRPC(sender, SnapshotRpc, payload);
    }

    private void OnSnapshot(long sender, string payload)
    {
        if (GameEnvironment.Dedicated || sender == 0L) return;
#if USE_GUI
        OpenLocal(payload);
#endif
    }

    private void OnReject(long sender, string reason)
    {
        if (GameEnvironment.Dedicated) return;
        _opening = false;
#if USE_GUI
        _open = false;
#endif
        Player.m_localPlayer?.Message(MessageHud.MessageType.Center, reason == "in-use"
            ? "$msg_inuse"
            : "$msg_cantopen");
    }

    private void OnSync(long sender, string payload)
    {
        if (!GameEnvironment.IsServer() || _humanoid == null || sender != _lockedPeer) return;
        Apply(payload);
    }

    private void OnCloseRequest(long sender)
    {
        if (!GameEnvironment.IsServer() || _humanoid == null || sender != _lockedPeer) return;
        ReleaseLock();
    }

    private void Apply(string payload)
    {
        if (_humanoid == null) return;
        var inventory = _humanoid.GetInventory();
        if (!VjsInventory.TryDecode(inventory, payload, out var error))
        {
            PluginLogger.Warn($"npc inventory: rejected sync for '{_displayName}': {error}");
            return;
        }

        _humanoid.UnequipAllItems();
        VjsInventory.EquipBestFor(_humanoid, null);
        var zdo = _nview?.GetZDO();
        if (zdo != null) VjsInventory.Save(_humanoid, zdo);
    }

    private void ReleaseLock()
    {
        _lockedPeer = 0L;
        if (_humanoid != null)
        {
            _humanoid.UnequipAllItems();
            VjsInventory.EquipBestFor(_humanoid, null);
            var zdo = _nview?.GetZDO();
            if (zdo != null) VjsInventory.Save(_humanoid, zdo);
        }
    }

    private bool CanOpen(long sender)
    {
        var distance = DistanceToPeer(sender);
        return distance <= InteractionRange;
    }

    private float DistanceToPeer(long sender)
    {
        var peer = ZNet.instance?.GetPeer(sender);
        if (peer != null && !peer.m_characterID.IsNone())
        {
            var zdo = ZDOMan.instance?.GetZDO(peer.m_characterID);
            if (zdo != null)
            {
                return Vector3.Distance(zdo.GetPosition(), transform.position);
            }
        }

        var player = PlayerLookup.ByUid(sender);
        if (player != null)
        {
            return Vector3.Distance(player.transform.position, transform.position);
        }

        // A host does not always expose its local player as a routed peer.
        var local = Player.m_localPlayer;
        return local != null ? Vector3.Distance(local.transform.position, transform.position) : float.MaxValue;
    }
}
