using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using ValheimJS.Scripting;
using ValheimJS.Scripting.Binding;

if (args.Contains("--declarations"))
{
    return EmitDeclarations();
}

if (args.Contains("--probe-bindings"))
{
    return BindingProbe.Run();
}

if (args.Contains("--probe-process")) return ProcessProbe.Run();
if (args.Contains("--probe-networking")) return NetworkingProbe.Run();
if (args.Contains("--probe-commands")) return CommandProbe.Run();
if (args.Contains("--probe-accounts")) return AccountProbe.Run();
if (args.Contains("--probe-management")) return ManagementProbe.Run();

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
var commandQueue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
using var commands = new ValheimJS.Core.ChatCommands(commandQueue.Enqueue);
var host = new JsHost(root, ProcessSide.Server, log,
    bindingFilter: type => !BindingProbe.IsFixture(type) || type == typeof(ValheimJS.Bindings.ChatCommandsBindings),
    commands: commands);

Console.WriteLine($"root: {root}");
host.Load();

if (host.LoadedNames.Count == 0)
{
    Console.WriteLine("FAIL: no plugins loaded");
    return 1;
}

Console.WriteLine("loaded: " + string.Join(", ", host.LoadedNames));

// ~4 s of host ticks at 200 ms: enough for a sample plugin's 1 s interval.
var elapsed = Stopwatch.StartNew();
while (elapsed.Elapsed < TimeSpan.FromSeconds(4))
{
    Thread.Sleep(200);
    host.Tick();
    while (commandQueue.TryDequeue(out var action)) action();
}

var results = host.DispatchAll("onServerTick", _ => "{}");
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

int EmitDeclarations()
{
    var assemblyPath = ValueOf("--assembly")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "Release", "netstandard2.1", "ValheimJS.dll"));
    var managed = ValueOf("--managed") ?? Environment.GetEnvironmentVariable("MANAGED_PATH");
    var output = ValueOf("--out");

    if (!File.Exists(assemblyPath))
    {
        Console.Error.WriteLine($"assembly not found: {assemblyPath} (run `task build` first)");
        return 2;
    }

    if (string.IsNullOrEmpty(managed) || !Directory.Exists(managed))
    {
        Console.Error.WriteLine("MANAGED_PATH is not set or does not exist; pass --managed <valheim_Data/Managed>");
        return 2;
    }

    var references = (ValueOf("--references") ?? Environment.GetEnvironmentVariable("BEPINEX_CORE") ?? "")
        .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
    using var metadata = MetadataAssembly.Load(assemblyPath, managed, references);
    var catalog = JsBindingDiscovery.Discover(metadata.Assembly);
    if (!catalog.IsValid)
    {
        Console.Error.WriteLine("binding declarations failed:" + Environment.NewLine + catalog.DescribeErrors());
        return 1;
    }

    var text = JsBindingDeclarations.Write(catalog);
    if (output == null)
    {
        Console.Out.Write(text);
    }
    else
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, text);
        Console.WriteLine($"wrote {catalog.Binders.Count} namespace(s) to {output}");
    }

    return 0;
}

string? ValueOf(string name)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (args[index] == name)
        {
            return args[index + 1];
        }
    }

    return null;
}

/// <summary>Loads the built plugin for metadata-only inspection (Unity references never load).</summary>
internal sealed class MetadataAssembly : IDisposable
{
    private readonly MetadataLoadContext _context;

    private MetadataAssembly(MetadataLoadContext context, Assembly assembly)
    {
        _context = context;
        Assembly = assembly;
    }

    public Assembly Assembly { get; }

    public static MetadataAssembly Load(string assemblyPath, string managedDirectory, IEnumerable<string>? references = null)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void AddDirectory(string? directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(directory, "*.dll"))
            {
                paths.TryAdd(Path.GetFileName(file), file);
            }
        }

        AddDirectory(Path.GetDirectoryName(assemblyPath));
        AddDirectory(managedDirectory);
        if (references != null)
        {
            foreach (var reference in references)
            {
                AddDirectory(reference);
            }
        }

        AddDirectory(RuntimeEnvironment.GetRuntimeDirectory());

        var resolver = new PathAssemblyResolver(paths.Values);
        var context = new MetadataLoadContext(resolver);
        var assembly = context.LoadFromAssemblyPath(assemblyPath);
        return new MetadataAssembly(context, assembly);
    }

    public void Dispose() => _context.Dispose();
}

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
