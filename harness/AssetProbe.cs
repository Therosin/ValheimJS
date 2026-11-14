using System;
using System.IO;
using ValheimJS.Bindings;
using ValheimJS.Scripting;
using ValheimJS.Scripting.Binding;

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

            Equal(Path.Combine(plugin), assets.Root("probe"), "root");
            True(assets.Exists("probe", "behaviours/wander.yaml"), "exists");
            True(assets.Read("probe", "behaviours/wander.yaml")?.Contains("id: wander") == true, "read");
            True(assets.List("probe", "behaviours").Length == 1, "list");
            False(assets.Exists("probe", "missing.yaml"), "missing");

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
