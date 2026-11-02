using System.Diagnostics;
using ValheimJS.Scripting;

// Headless smoke test for the JS host: loads bundles from a directory, pumps timers,
// dispatches hooks, reloads. No Unity, no BepInEx — same code the plugin runs.

var root = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripting", "dist"));

// Accept a server_plugins root (plugins/, data/, config/, logs/) or a folder of plugin dirs.
// The latter is copied into a throwaway root so the harness never writes into the build output.
if (!Directory.Exists(Path.Combine(root, "plugins")) &&
    Directory.GetDirectories(root).Any(dir => File.Exists(Path.Combine(dir, "manifest.ini"))))
{
    var staging = Path.Combine(Path.GetTempPath(), "valheimjs-harness-root");
    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
    Directory.CreateDirectory(Path.Combine(staging, "plugins"));

    foreach (var dir in Directory.GetDirectories(root))
    {
        if (!File.Exists(Path.Combine(dir, "manifest.ini"))) continue;
        CopyTree(dir, Path.Combine(staging, "plugins", Path.GetFileName(dir)));
    }

    root = staging;
}

var log = new ConsoleLog();
var host = new JsHost(root, "server", log);

Console.WriteLine($"root: {root}");
host.Load();

if (host.LoadedNames.Count == 0)
{
    Console.WriteLine("FAIL: no plugins loaded");
    return 1;
}

Console.WriteLine("loaded: " + string.Join(", ", host.LoadedNames));

// ~4 s of host ticks at 200 ms: enough for the sample plugin's 1 s interval.
var elapsed = Stopwatch.StartNew();
while (elapsed.Elapsed < TimeSpan.FromSeconds(4))
{
    Thread.Sleep(200);
    host.Tick();
}

// Optional: exercise the net dispatch path without a running game.
if (args.Contains("--net-probe"))
{
    const string envelopeJson =
        "{\"v\":1,\"kind\":\"request\",\"channel\":\"server-time\",\"from\":\"server\",\"ts\":1,\"id\":7,\"data\":{\"day\":1}}";
    var payload = "{\"channel\":\"server-time\",\"sender\":7,\"data\":" + JsonQuote(envelopeJson) + "}";
    var replies = host.DispatchAll("onNet", _ => payload);
    Console.WriteLine($"net probe dispatched, {replies.Count} reply/result(s)");
}

var results = host.DispatchAll("onTick", _ => "{}");
Console.WriteLine($"dispatch results: {results.Count}");

host.Reload();
Console.WriteLine("after reload: " + string.Join(", ", host.LoadedNames));
host.Dispose();

// Plugins that call game bindings fail outside the game by design; only host-level failures
// (bindings install, engine, load) decide pass/fail.
Console.WriteLine(log.HostErrors == 0 ? "PASS" : $"FAIL: {log.HostErrors} host error(s)");
return log.HostErrors == 0 ? 0 : 1;

static void CopyTree(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.GetFiles(source))
    {
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }
    foreach (var dir in Directory.GetDirectories(source))
    {
        CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}

static string JsonQuote(string value) =>
    "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

internal sealed class ConsoleLog : IJsLog
{
    public int Errors { get; private set; }

    /// <summary>Errors from the host itself (engine, bindings, load), not from a plugin body.</summary>
    public int HostErrors { get; private set; }

    public void Write(string level, string plugin, string message)
    {
        if (level == "error")
        {
            Errors++;
            if (plugin is "host" or "bindings")
            {
                HostErrors++;
            }
        }
        Console.WriteLine($"[{level,-5}] {plugin,-10} {message}");
    }
}
