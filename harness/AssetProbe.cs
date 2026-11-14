using System;
using System.Collections.Generic;
using System.IO;
using Jint.Native;
using Jint.Native.Object;
using ValheimJS.Bindings;
using ValheimJS.Scripting;
using ValheimJS.Scripting.Binding;
using ValheimJS.Scripting.Types;
using ValheimJS.Utils;

/// <summary>Verifies the read-only shipped-asset binding, including path confinement.</summary>
internal static class AssetProbe
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-assets-" + Guid.NewGuid().ToString("N"));
        var plugins = Path.Combine(root, "plugins");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        var logs = Path.Combine(root, "logs");
        var plugin = Path.Combine(plugins, "probe");
        Directory.CreateDirectory(Path.Combine(plugin, "behaviours"));
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(plugin, "behaviours", "wander.yaml"), "id: wander\n");
        File.WriteAllText(Path.Combine(plugin, "defaults.toml"), "title = \"demo\"\n[owner]\nname = \"Astrid\"\n");
        File.WriteAllText(Path.Combine(plugins, "secret.txt"), "outside");

        try
        {
            using var context = new JsBindingContext(
                ProcessSide.Client,
                data,
                config,
                plugins,
                logs,
                name => Path.Combine(data, name),
                name => Path.Combine(config, name),
                (_, _) => { });
            var assets = new AssetBindings(context);
            var engine = new Jint.Engine();
            context.AttachEngine(engine, new JsTypeRegistry(engine));

            Equal(Path.Combine(plugin), assets.Root("probe"), "root");
            True(assets.Exists("probe", "behaviours/wander.yaml"), "exists");
            True(assets.Read("probe", "behaviours/wander.yaml")?.Contains("id: wander") == true, "read");
            True(assets.List("probe", "behaviours").Length == 1, "list");
            False(assets.Exists("probe", "missing.yaml"), "missing");

            var yaml = (ObjectInstance)assets.ParseYaml("probe", "behaviours/wander.yaml");
            Equal("wander", yaml.Get("id").ToString(), "asset yaml binding");
            var toml = (ObjectInstance)assets.ParseToml("probe", "defaults.toml");
            Equal("demo", toml.Get("title").ToString(), "asset toml binding");

            CheckParsers();

            try
            {
                assets.Read("probe", "../secret.txt");
                throw new InvalidOperationException("expected traversal rejection");
            }
            catch (UnauthorizedAccessException)
            {
            }

            Console.WriteLine("PASS: read-only plugin assets and path confinement");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: asset probe: " + exception);
            return 1;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CheckParsers()
    {
        var yaml = Cast(Yaml.Parse("""
            title: demo
            counts: [1, 2, 3]
            nested:
              enabled: true
              name: "value"
            """));
        Equal("demo", yaml["title"]?.ToString() ?? "", "yaml title");
        True(yaml["counts"] is List<object?> { Count: 3 }, "yaml list");
        True(yaml["nested"] is IReadOnlyDictionary<string, object?> nested && Equals(nested["enabled"], true), "yaml nested");

        var toml = Toml.Parse("""
            title = "demo"
            enabled = true
            counts = [1, 2, 3]

            [owner]
            name = "Astrid"
            score = 1.5

            [[products]]
            name = "a"

            [[products]]
            name = "b"
            """);
        Equal("demo", toml["title"]?.ToString() ?? "", "toml title");
        True(Equals(toml["enabled"], true), "toml bool");
        True(toml["counts"] is List<object?> { Count: 3 }, "toml array");
        True(toml["owner"] is IReadOnlyDictionary<string, object?> owner && Equals(owner["name"], "Astrid"), "toml table");
        True(toml["products"] is List<object?> { Count: 2 } products && Equals(((IReadOnlyDictionary<string, object?>)products[1]!)["name"], "b"), "toml array table");
    }

    private static IReadOnlyDictionary<string, object?> Cast(object? value) =>
        value as IReadOnlyDictionary<string, object?> ?? throw new InvalidOperationException("expected dictionary");

    private static void True(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
    }

    private static void False(bool value, string label) => True(!value, label);

    private static void Equal(string expected, string actual, string label)
    {
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'");
        }
    }
}
