using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using ValheimJS.Startup;
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
    private readonly List<IStartupModule> _modules = new();
    private Core.Networking? _network;
    private World.WorldManager? _world;
    internal Core.Networking Network => _network ?? throw new InvalidOperationException("Networking has not started.");
    private Core.ChatCommands? _commands;
    private Commands.ManagementCommands? _managementCommands;
    private Core.ChatCommandScope? _helpCommands;
    internal static Services.AccountService? Accounts { get; private set; }
    internal static Services.PermissionService? Permissions { get; private set; }
    private static Services.AccountSessions? _accountSessions;
    internal static void RefreshAccounts() => _accountSessions?.Refresh();
    internal static global::ValheimJS.Networking.CommandService? CommandTransport { get; private set; }

    private void Awake()
    {
        Logger = base.Logger;

        _enabled = Config.Bind("General", "Enabled", true, "Load JS plugins on startup.");
        var processSide = Config.Bind("General", "ProcessSide", "auto", "auto (valheim/valheim_server executable), server, or client. Fixed until process restart.");
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
        ValheimJsHost.Ensure();
        try
        {
            using var process = Process.GetCurrentProcess();
            GameEnvironment.Initialize(ProcessPolicy.Resolve(process.ProcessName, processSide.Value));
            Logger.LogInfo($"startup side: {GameEnvironment.Side} ({processSide.Value}; executable {process.ProcessName})");
            var types = typeof(Plugin).Assembly.GetTypes();
            var modules = ProcessPolicy.Select(types.Where(type => !type.IsAbstract && typeof(IStartupModule).IsAssignableFrom(type)), GameEnvironment.Side);
            var patches = ProcessPolicy.Select(types.Where(type => type.IsDefined(typeof(HarmonyPatch), false)), GameEnvironment.Side);
            foreach (var type in modules)
            {
                var module = (IStartupModule)Activator.CreateInstance(type, true)!;
                _modules.Add(module);
                module.Start(this);
                Logger.LogInfo($"startup module: {type.Name}");
            }
            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            foreach (var patch in patches)
            {
                _harmony.CreateClassProcessor(patch).Patch();
                Logger.LogInfo($"hook registered: {patch.Name}");
            }
            _host!.Load();
            _world?.PluginsLoaded();
            if (_watch.Value) StartWatcher();
            Logger.LogInfo($"ValheimJS ready — {_host.LoadedNames.Count} plugin(s) from {_host.Root}");
        }
        catch (Exception exception)
        {
            Logger.LogError("ValheimJS startup failed: " + exception);
            OnDestroy();
            enabled = false;
        }
    }

    internal void StartShared()
    {
        Utils.RpcBus.Install();
        _network = new Core.Networking(new global::ValheimJS.Networking.RoutedRpcTransport(), message => Logger.LogError(message));
        _commands = new Core.ChatCommands(_network.Post, message => Logger.LogError(message));
        Accounts = new Services.AccountService(Path.Combine(_pluginRoot.Value, "services", "accounts"),
            () => ZNet.instance != null && ZNet.instance.IsServer());
        _accountSessions = new Services.AccountSessions(Accounts);
        Permissions = new Services.PermissionService(Path.Combine(_pluginRoot.Value, "services", "permissions"), Accounts);
        _managementCommands = new Commands.ManagementCommands(_commands, Accounts, Permissions,
            user => ZNet.instance != null && ZNet.instance.IsServer() && user.PlatformId != null && ZNet.instance.IsAdmin(user.PlatformId),
            message => Logger.LogInfo(message));
        _helpCommands = _commands.OpenScope();
        _helpCommands.Register("host", new Commands.HelpCommand(_commands));
        CommandTransport = new global::ValheimJS.Networking.CommandService(_network, _commands, Accounts, Permissions);
        _world = new World.WorldManager(_network);
        _host = new JsHost(
            _pluginRoot.Value,
            GameEnvironment.Side,
            new BepInExJsLog(),
            new JsRuntimeOptions
            {
                MaxStatements = _maxStatements.Value,
                TimeoutMs = _timeoutMs.Value,
                LimitRecursion = _limitRecursion.Value
            }, networking: _network, commands: _commands, accounts: Accounts, permissions: Permissions,
            worldReady: () => GameEnvironment.WorldReady &&
                         (GameEnvironment.Dedicated || GameEnvironment.PlayerReady));
        ValheimBridge.Emit = _host.Emit;
        ValheimBridge.WorldSnapshot = BuildWorldSnapshot;
    }

    internal void StopShared()
    {
        ValheimBridge.Emit = null;
        ValheimBridge.WorldSnapshot = null;
        Hooks.PlayerWatcher.Reset();
        CommandTransport?.Dispose();
        CommandTransport = null;
        _managementCommands?.Dispose();
        _managementCommands = null;
        _helpCommands?.Dispose();
        _helpCommands = null;
        _commands?.Dispose();
        _commands = null;
        _accountSessions = null;
        Accounts = null;
        Permissions = null;
        _world?.Dispose();
        _world = null;
        _network?.Dispose();
        _network = null;
        Utils.RpcBus.Shutdown();
        global::Core.Container.Container.ClearAll();
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
#if USE_GUI
        UI.UiHost.Tick();
#endif
        Utils.RpcBus.Refresh();
        _network?.Pump();
        _world?.Tick(Time.unscaledDeltaTime);
        if (_host == null)
        {
            return;
        }

        if (_reloadRequested)
        {
            _reloadRequested = false;
            Logger.LogInfo("Reloading JS plugins…");
            _host.Reload();
            _world?.PluginsLoaded();
        }

        _tickAccumulator += Time.unscaledDeltaTime;
        var interval = _tickMs.Value / 1000f;
        if (_tickAccumulator < interval)
        {
            return;
        }

        _tickAccumulator = 0f;
        RefreshAccounts();
        _host.Tick();
    }

    private void StartWatcher()
    {
        // Watch plugin bundles only: data/, config/ and logs/ are written at runtime, so watching
        // the whole root would reload the host every time a plugin logged a line.
        var plugins = Path.Combine(_pluginRoot.Value, "plugins");
        Directory.CreateDirectory(plugins);
        _watcher = new FileSystemWatcher(plugins)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, _) => _reloadRequested = true;
        _watcher.Created += (_, _) => _reloadRequested = true;
        _watcher.Deleted += (_, _) => _reloadRequested = true;
        _watcher.Renamed += (_, _) => _reloadRequested = true;
        Logger.LogInfo($"Watching {plugins} for changes.");
    }

    private void OnDestroy()
    {
        _watcher?.Dispose();
        _watcher = null;
        _host?.Dispose();
        _host = null;
        _harmony?.UnpatchSelf();
        _harmony = null;
        for (var index = _modules.Count - 1; index >= 0; index--)
        {
            try { _modules[index].Stop(this); }
            catch (Exception exception) { Logger.LogWarning("Module cleanup failed: " + exception); }
        }
        _modules.Clear();
        ValheimJsHost.Dispose();
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
