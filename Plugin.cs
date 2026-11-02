using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;
using UnityEngine;
using ValheimJS.Core;
using ValheimJS.Scripting;

namespace ValheimJS;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger = null!;

    private ConfigEntry<bool> _enabled = null!;
    private ConfigEntry<string> _pluginRoot = null!;
    private ConfigEntry<int> _tickMs = null!;
    private ConfigEntry<bool> _watch = null!;
    private ConfigEntry<long> _maxStatements = null!;
    private ConfigEntry<int> _timeoutMs = null!;
    private ConfigEntry<int> _limitRecursion = null!;

    private JsHost? _host;
    private Harmony? _harmony;
    private FileSystemWatcher? _watcher;
    private volatile bool _reloadRequested;
    private float _tickAccumulator;

    private void Awake()
    {
        Logger = base.Logger;

        _enabled = Config.Bind("General", "Enabled", true, "Load JS plugins on startup.");
        _pluginRoot = Config.Bind(
            "General",
            "PluginRoot",
            Path.Combine(Paths.GameRootPath, "ValheimJS"),
            "Scripting root next to the game executable; contains plugins/, data/, config/ and logs/.");
        _tickMs = Config.Bind("Runtime", "TickMs", 200, new ConfigDescription("Timer pump interval in milliseconds.", new AcceptableValueRange<int>(50, 5000)));
        _watch = Config.Bind("Dev", "WatchForChanges", false, "Reload plugins when files under PluginRoot change (development).");
        _maxStatements = Config.Bind("Runtime", "MaxStatements", 5_000_000L, "Statement budget per plugin execution. 0 disables.");
        _timeoutMs = Config.Bind("Runtime", "TimeoutMs", 2_000, new ConfigDescription("Milliseconds per plugin execution before it is aborted. 0 disables.", new AcceptableValueRange<int>(0, 60_000)));
        _limitRecursion = Config.Bind("Runtime", "LimitRecursion", 256, new ConfigDescription("Maximum JS call depth. 0 disables.", new AcceptableValueRange<int>(0, 4096)));

        if (!_enabled.Value)
        {
            Logger.LogInfo("Disabled by config; no JS plugins loaded.");
            return;
        }

        Logger.LogInfo($"settings file: {Config.ConfigFilePath}");
        Logger.LogInfo($"startup side: {(Application.isBatchMode ? "server (headless)" : "client")}");

        var side = Application.isBatchMode ? "server" : "client";
        _host = new JsHost(
            _pluginRoot.Value,
            side,
            new BepInExJsLog(),
            new JsRuntimeOptions
            {
                MaxStatements = _maxStatements.Value,
                TimeoutMs = _timeoutMs.Value,
                LimitRecursion = _limitRecursion.Value
            });
        ValheimBridge.Emit = (hook, payload) => _host.DispatchAll(hook, _ => payload);
        ValheimBridge.WorldSnapshot = BuildWorldSnapshot;

        _host.Load();
        ApplyPatches();

        Networking.RpcChannel.Install();

        if (Application.isBatchMode)
        {
            Core.InventorySnapshot.Install();
        }
        else
        {
            // The server cannot touch a remote player's component; it commands the owning client.
            PlayerCommandAgent.Install();
            Core.ChatAgent.Install();
        }

        if (_watch.Value)
        {
            StartWatcher();
        }

        Logger.LogInfo($"ValheimJS ready — {_host.LoadedNames.Count} plugin(s) from {_host.Root}");
    }

    private void ApplyPatches()
    {
        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        try
        {
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("Game hooks applied.");
        }
        catch (Exception exception)
        {
            Logger.LogError("Hook registration failed: " + exception);
        }
    }

    private static string BuildWorldSnapshot()
    {
        var worldName = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
        var peers = ZNet.instance != null && ZNet.instance.GetPeers() != null ? ZNet.instance.GetPeers().Count : 0;
        var day = EnvMan.instance != null ? EnvMan.instance.GetDay() : 0;
        var fraction = EnvMan.instance != null ? EnvMan.instance.GetDayFraction() : 0f;
        return $"{{\"world\":{ValheimBridge.Quote(worldName)},\"peers\":{peers},\"day\":{day},\"dayFraction\":{fraction.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
    }

    private void Update()
    {
        if (_host == null)
        {
            return;
        }

        if (_reloadRequested)
        {
            _reloadRequested = false;
            Logger.LogInfo("Reloading JS plugins…");
            _host.Reload();
        }

        _tickAccumulator += Time.unscaledDeltaTime;
        var interval = _tickMs.Value / 1000f;
        if (_tickAccumulator < interval)
        {
            return;
        }

        _tickAccumulator = 0f;
        _host.Tick();
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_pluginRoot.Value)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, _) => _reloadRequested = true;
        _watcher.Created += (_, _) => _reloadRequested = true;
        _watcher.Deleted += (_, _) => _reloadRequested = true;
        _watcher.Renamed += (_, _) => _reloadRequested = true;
        Logger.LogInfo($"Watching {_pluginRoot.Value} for changes.");
    }

    private void OnDestroy()
    {
        _watcher?.Dispose();
        _watcher = null;
        _harmony?.UnpatchSelf();
        _harmony = null;
        ValheimBridge.Emit = null;
        ValheimBridge.WorldSnapshot = null;
        GameRegistry.Clear();
        Hooks.PlayerWatcher.Reset();
        Networking.RpcChannel.Reset();
        _host?.Dispose();
        _host = null;
    }

    private sealed class BepInExJsLog : IJsLog
    {
        public void Write(string level, string plugin, string message)
        {
            switch (level)
            {
                case "error":
                    Logger.LogError($"[{plugin}] {message}");
                    break;
                case "warn":
                    Logger.LogWarning($"[{plugin}] {message}");
                    break;
                default:
                    Logger.LogInfo($"[{plugin}] {message}");
                    break;
            }
        }
    }
}
