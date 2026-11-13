using System;
using System.Collections.Generic;
using ValheimJS.Core;

internal static class UiProbe
{
    internal static int Run()
    {
        try
        {
            CheckExpansion();
            CheckImports();
            CheckBindingsAndState();
            CheckErrors();
            Console.WriteLine("PASS: UI compiler expansion, imports, slots, bindings and validation");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: ui probe: " + exception);
            return 1;
        }
    }

    private static void CheckExpansion()
    {
        var document = Parse("""
            components:
              field:
                type: row
                children:
                  - type: text
                    props:
                      text: { param: label }
                  - slot: control

            panel:
              type: window
              props:
                title: Player Settings
              children:
                - use: field
                  id: name
                  args:
                    label: Name
                  slots:
                    control:
                      - type: input
                        props:
                          value: { bind: "player.name" }
                - type: text
                  props:
                    text: { local: note }
            """);

        Assert(document.Root.Type == "window", "root type");
        Assert(document.Root.Path == "panel", "root path");
        Assert(document.Root.Children.Count == 2, "root children");

        var field = document.Root.Children[0];
        Assert(field.Type == "row", "component root type");
        Assert(field.Path == "panel/name[0]", "component path: " + field.Path);
        Assert(field.Children.Count == 2, "component children");

        var label = field.Children[0];
        Assert(label.Type == "text", "label type");
        Assert((string?)label.Props["text"] == "Name", "param substitution");

        var input = field.Children[1];
        Assert(input.Type == "input", "slot content type");
        var binding = input.Props["value"] as UiValue;
        Assert(binding != null && binding.Kind == UiValueKind.Binding && binding.Key == "player.name",
            "binding preserved through component and slot");

        var local = document.Root.Children[1].Props["text"] as UiValue;
        Assert(local != null && local.Kind == UiValueKind.Local && local.Key == "note", "local preserved");
    }

    private static void CheckImports()
    {
        var widgets = """
            components:
              label:
                type: text
                props:
                  text: { param: value }

            panel:
              type: panel
              children:
                - type: text
                  props:
                    text: ImportedPanel
            """;

        var main = """
            imports:
              widgets: ./widgets.yaml

            components:
              field:
                use: label
                args:
                  value: Imported

            panel:
              type: panel
              children:
                - use: field
                - use: widgets
            """;

        var document = Parse(main, alias => alias == "widgets" ? widgets : null);
        Assert(document.Root.Children.Count == 2, "imported panel children");
        Assert(document.Root.Children[0].Type == "text", "imported component use");
        Assert((string?)document.Root.Children[0].Props["text"] == "Imported", "imported component args");
        Assert(document.Root.Children[1].Type == "panel", "imported panel exposed as component");
    }

    private static void CheckBindingsAndState()
    {
        var document = Parse("""
            components:
              box:
                type: panel
                state:
                  open: false
                children:
                  - type: text
                    props:
                      text: { local: open }

            panel:
              type: panel
              state:
                root: yes
              children:
                - use: box
            """);

        Assert(document.Root.State.Count == 1 && document.Root.State.ContainsKey("root"), "root state");
        var box = document.Root.Children[0];
        Assert(box.Scope == box.Path, "component scope");
        Assert(box.State.Count == 1 && box.State.ContainsKey("open"), "component state");
        var local = box.Children[0].Props["text"] as UiValue;
        Assert(local != null && local.Kind == UiValueKind.Local, "component local ref");
    }

    private static void CheckErrors()
    {
        Throws(() => Parse("""
            components:
              field:
                type: row
                children:
                  - slot: control
            panel:
              type: panel
              children:
                - use: field
            """), "missing slot");

        Throws(() => Parse("""
            components:
              a: { use: b }
              b: { use: a }
            panel: { use: a }
            """), "component cycle");

        Throws(() => Parse("""
            panel:
              type: nonsense
            """), "unknown type");

        Throws(() => Parse("""
            panel:
              type: panel
              children:
                - use: missing
            """), "missing component");

        Throws(() => Parse("""
            imports:
              a: ./a.yaml
            panel:
              type: panel
            """), "imports without resolver");

        var a = "imports:\n  b: ./b.yaml\npanel:\n  type: panel\n";
        var b = "imports:\n  a: ./a.yaml\npanel:\n  type: panel\n";
        Throws(() => Parse(a, alias => alias == "b" ? b : a), "circular import");

        Throws(() => Parse("""
            panel:
              type: panel
              state:
                value: { bind: "x" }
            """), "binding in state");
    }

    private static UiDocument Parse(string yaml, Func<string, string?>? imports = null) =>
        UiCompiler.Parse(yaml, "ui-probe.yaml", imports);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("assertion failed: " + message);
        }
    }

    private static void Throws(Action action, string message)
    {
        try
        {
            action();
        }
        catch (UiException)
        {
            return;
        }

        throw new InvalidOperationException("expected UiException: " + message);
    }
}
