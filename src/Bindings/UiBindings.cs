#if USE_GUI
using System;
using System.Collections.Generic;
using System.Linq;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using ValheimJS.Core;
using ValheimJS.Scripting.Binding;
using ValheimJS.UI;

namespace ValheimJS.Bindings;

/// <summary>
/// Client-only declarative panels. YAML is compiled by the Unity-free UI compiler; this binder
/// owns per-plugin documents, open instances, JS value/event callbacks and host cadence.
/// </summary>
[JsBinder("ui", Phase = JsBindPhase.Gameplay, Order = 150,
    Availability = JsAvailability.Client,
    Description = "Declarative YAML panels with bound values and event callbacks.")]
internal sealed class UiBindings : ScopedBindings<string, UiInstance>
{
    private const int MaxDocumentsPerPlugin = 32;

    private sealed class DocumentEntry
    {
        internal string Id = string.Empty;
        internal UiDocument Document = null!;
    }

    private readonly Dictionary<string, Dictionary<string, DocumentEntry>> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _documentIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _instanceIds = new(StringComparer.Ordinal);
    private bool _sawLocalPlayer;

    public UiBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding(Scoped = true, Description = "Compiles a YAML panel document; imports come from options.imports.")]
    public string Define(string plugin, string yaml, JsValue options)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new ArgumentException("ui.define: YAML is required.");
        }

        var settings = ObjectOptions(options, "ui.define(options)");
        var source = OptionalString(settings, "source", "ui.define(source)") ?? "<ui>";
        var imports = ReadImports(settings);
        var documents = Documents(plugin);
        if (documents.Count >= MaxDocumentsPerPlugin)
        {
            throw new InvalidOperationException($"ui.define: this plugin already has {MaxDocumentsPerPlugin} documents.");
        }

        var document = UiCompiler.Parse(yaml, source, alias => imports.TryGetValue(alias, out var text) ? text : null);
        var id = "doc" + Next(_documentIds, plugin);
        documents[id] = new DocumentEntry { Id = id, Document = document };
        return id;
    }

    [JsBinding(Scoped = true, Description = "Opens a compiled document as a panel; returns its instance id.")]
    public string Open(string plugin, string documentId, JsValue options)
    {
        if (!Documents(plugin).TryGetValue(documentId, out var entry))
        {
            throw new InvalidOperationException($"ui.open: document '{documentId}' does not exist for this plugin.");
        }

        var settings = ObjectOptions(options, "ui.open(options)");
        var mount = OptionalString(settings, "mount", "ui.open(mount)") ?? "window";
        if (mount is not ("overlay" or "window" or "popup"))
        {
            throw new ArgumentException("ui.open: mount must be overlay, window or popup.");
        }

        var modal = OptionalBool(settings, "modal", false);
        if (!modal && entry.Document.Root.Props.TryGetValue("modal", out var modalValue) && modalValue is bool rootModal)
        {
            modal = rootModal;
        }
        var get = Callback(settings, "get", "ui.open(get)");
        var set = Callback(settings, "set", "ui.open(set)");
        var onEvent = Callback(settings, "onEvent", "ui.open(onEvent)");
        UiHost.Ensure();

        var instance = new UiInstance(
            "panel" + Next(_instanceIds, plugin),
            plugin,
            entry.Document,
            modal,
            get,
            set,
            onEvent,
            message => LogError($"[{plugin}] {message}"));
        try
        {
            UiRenderer.Build(instance, mount);
            For(plugin)[instance.Id] = instance;
            return instance.Id;
        }
        catch
        {
            instance.Dispose();
            throw;
        }
    }

    [JsBinding(Scoped = true, Description = "Closes and destroys one open panel.")]
    public void Close(string plugin, string instanceId)
    {
        if (!Remove(plugin, instanceId, out var instance))
        {
            return;
        }

        instance.Dispose();
    }

    [JsBinding(Scoped = true, Description = "Marks a panel for a binding refresh on the next host tick.")]
    public void Invalidate(string plugin, string instanceId) => Require(plugin, instanceId, "ui: panel").Dirty = true;

    public override void Pump()
    {
        // Panels live under DontDestroyOnLoad. If the client leaves the world, the panel would
        // otherwise stay on top of the main menu and keep swallowing clicks. Close it before
        // doing any binding work; documents remain registered for the next session.
        if (!GameEnvironment.Dedicated)
        {
            if (Player.m_localPlayer != null)
            {
                _sawLocalPlayer = true;
            }
            else if (_sawLocalPlayer)
            {
                // The client was in a world and the local player is gone: left to the main menu.
                _sawLocalPlayer = false;
                CloseAll();
                return;
            }
        }

        if (!Any)
        {
            return;
        }

        foreach (var instance in Values.ToList())
        {
            instance.Refresh();
            instance.DrainEvents();
            if (instance.CloseAfterEvents)
            {
                Close(instance.Plugin, instance.Id);
            }
        }
    }

    private void CloseAll()
    {
        foreach (var instance in Values.ToList())
        {
            Close(instance.Plugin, instance.Id);
        }
    }

    public override void Release(string plugin)
    {
        base.Release(plugin);
        _documents.Remove(plugin);
        _documentIds.Remove(plugin);
        _instanceIds.Remove(plugin);
    }

    public override void Dispose()
    {
        base.Dispose();
        _documents.Clear();
        _documentIds.Clear();
        _instanceIds.Clear();
        UiHost.Dispose();
    }

    protected override void OnRelease(UiInstance value) => value.Dispose();

    private JsCallback? Callback(ObjectInstance? options, string name, string label)
    {
        var value = options?.Get(name) ?? JsValue.Undefined;
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        return value is Function function
            ? new JsCallback(Engine, function, label)
            : throw new ArgumentException($"{label} must be a function.");
    }

    private static ObjectInstance? ObjectOptions(JsValue value, string label)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        return value as ObjectInstance ?? throw new ArgumentException($"{label} must be an object.");
    }

    private static string? OptionalString(ObjectInstance? options, string name, string label)
    {
        var value = options?.Get(name) ?? JsValue.Undefined;
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        return value.IsString() ? value.AsString() : throw new ArgumentException($"{label} must be a string.");
    }

    private static bool OptionalBool(ObjectInstance? options, string name, bool fallback)
    {
        var value = options?.Get(name) ?? JsValue.Undefined;
        if (value.IsUndefined() || value.IsNull())
        {
            return fallback;
        }

        return value.IsBoolean() ? value.AsBoolean() : throw new ArgumentException($"ui.open({name}) must be a boolean.");
    }

    private static Dictionary<string, string> ReadImports(ObjectInstance? options)
    {
        var imports = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options?.Get("imports") is not ObjectInstance source)
        {
            return imports;
        }

        foreach (var key in source.GetOwnPropertyKeys(Jint.Runtime.Types.String))
        {
            var alias = key.ToString();
            var value = source.Get(alias);
            if (!value.IsString())
            {
                throw new ArgumentException($"ui.define: import '{alias}' must be YAML text.");
            }

            imports[alias] = value.AsString();
        }

        return imports;
    }

    private Dictionary<string, DocumentEntry> Documents(string plugin)
    {
        if (!_documents.TryGetValue(plugin, out var documents))
        {
            documents = new Dictionary<string, DocumentEntry>(StringComparer.Ordinal);
            _documents[plugin] = documents;
        }

        return documents;
    }

    private static int Next(Dictionary<string, int> counters, string plugin)
    {
        counters.TryGetValue(plugin, out var value);
        value++;
        counters[plugin] = value;
        return value;
    }
}
#endif
