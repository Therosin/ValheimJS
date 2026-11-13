#if USE_GUI
using System;
using System.Collections.Generic;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using ValheimJS.Core;
using ValheimJS.Scripting.Binding;
using UnityEngine;

namespace ValheimJS.UI;

/// <summary>One open panel: compiled tree, local state, JS bindings and live widgets.</summary>
internal sealed class UiInstance : IDisposable
{
    private readonly Action<string>? _log;
    private readonly bool _modal;
    private bool _modalRegistered;

    internal UiInstance(
        string id,
        string plugin,
        UiDocument document,
        bool modal,
        JsCallback? get,
        JsCallback? set,
        JsCallback? onEvent,
        Action<string>? log)
    {
        Id = id;
        Plugin = plugin;
        Document = document;
        _modal = modal;
        GetCallback = get;
        SetCallback = set;
        EventCallback = onEvent;
        _log = log;
        UiHost.AddPanel();
        if (modal)
        {
            UiHost.AddModal();
            _modalRegistered = true;
        }

        SeedState(document.Root);
    }

    internal string Id { get; }

    internal string Plugin { get; }

    internal UiDocument Document { get; }

    internal JsCallback? GetCallback { get; }

    internal JsCallback? SetCallback { get; }

    internal JsCallback? EventCallback { get; }

    internal GameObject RootObject { get; set; } = null!;

    internal RectTransform RootRect { get; set; } = null!;

    internal Action? CloseRequested { get; set; }

    internal bool CloseAfterEvents { get; set; }

    internal readonly Dictionary<string, UiWidget> Widgets = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Dictionary<string, object?>> State = new(StringComparer.Ordinal);
    internal readonly Queue<UiEvent> Events = new();

    internal bool Dirty { get; set; } = true;

    internal void RequestClose() => CloseAfterEvents = true;

    internal void Refresh()
    {
        foreach (var widget in Widgets.Values)
        {
            try
            {
                widget.Refresh(this);
            }
            catch (Exception exception)
            {
                Log($"ui '{Id}': refresh failed: {Describe(exception)}");
            }
        }

        Dirty = false;
    }

    internal void DrainEvents()
    {
        while (Events.TryDequeue(out var item))
        {
            if (EventCallback == null)
            {
                continue;
            }

            try
            {
                EventCallback.Invoke(item.Path, item.Name, ToJs(item.Value));
            }
            catch (Exception exception)
            {
                Log($"ui '{Id}': event '{item.Name}' failed: {Describe(exception)}");
            }
        }
    }

    internal void QueueEvent(string path, string name, object? value) =>
        Events.Enqueue(new UiEvent(path, name, value));

    internal object? ResolveProp(UiPanel node, string name) =>
        node.Props.TryGetValue(name, out var value) ? Resolve(value, node.Scope) : null;

    internal object? Resolve(object? value, string scope)
    {
        if (value is UiValue reference)
        {
            return reference.Kind switch
            {
                UiValueKind.Literal => reference.Literal,
                UiValueKind.Binding => ReadBinding(reference.Key!),
                UiValueKind.Local => ReadLocal(scope, reference.Key!),
                _ => null
            };
        }

        if (value is Dictionary<string, object?> map)
        {
            var result = new Dictionary<string, object?>(map.Count, StringComparer.Ordinal);
            foreach (var pair in map)
            {
                result[pair.Key] = Resolve(pair.Value, scope);
            }

            return result;
        }

        if (value is List<object?> list)
        {
            var result = new List<object?>(list.Count);
            foreach (var item in list)
            {
                result.Add(Resolve(item, scope));
            }

            return result;
        }

        return value;
    }

    internal bool SetProp(UiPanel node, string name, object? value)
    {
        if (!node.Props.TryGetValue(name, out var raw) || raw is not UiValue reference)
        {
            return false;
        }

        if (reference.Kind == UiValueKind.Local)
        {
            WriteLocal(node.Scope, reference.Key!, value);
            Dirty = true;
            return true;
        }

        if (reference.Kind == UiValueKind.Binding)
        {
            WriteBinding(reference.Key!, value);
            return true;
        }

        return false;
    }

    internal object? ReadLocal(string scope, string key)
    {
        return State.TryGetValue(scope, out var values) && values.TryGetValue(key, out var value)
            ? value
            : null;
    }

    internal void WriteLocal(string scope, string key, object? value)
    {
        if (!State.TryGetValue(scope, out var values))
        {
            values = new Dictionary<string, object?>(StringComparer.Ordinal);
            State[scope] = values;
        }

        values[key] = value;
    }

    internal string Text(UiPanel node, string name, string fallback = "") =>
        ResolveProp(node, name) as string ?? fallback;

    internal double Number(UiPanel node, string name, double fallback)
    {
        var value = ResolveProp(node, name);
        return value switch
        {
            double number => number,
            float single => single,
            long integer => integer,
            int integer => integer,
            _ => fallback
        };
    }

    internal bool Flag(UiPanel node, string name, bool fallback) =>
        ResolveProp(node, name) as bool? ?? fallback;

    public void Dispose()
    {
        if (_modalRegistered)
        {
            UiHost.RemoveModal();
            _modalRegistered = false;
        }

        UiHost.RemovePanel();
        Widgets.Clear();
        if (RootObject != null)
        {
            UnityEngine.Object.Destroy(RootObject);
            RootObject = null!;
        }
    }

    private void SeedState(UiPanel panel)
    {
        if (panel.State.Count > 0 && !State.ContainsKey(panel.Scope))
        {
            State[panel.Scope] = new Dictionary<string, object?>(panel.State, StringComparer.Ordinal);
        }

        foreach (var child in panel.Children)
        {
            SeedState(child);
        }
    }

    private object? ReadBinding(string key)
    {
        if (GetCallback == null)
        {
            return null;
        }

        try
        {
            return FromJs(GetCallback.Invoke(key));
        }
        catch (Exception exception)
        {
            Log($"ui '{Id}': binding '{key}' failed: {Describe(exception)}");
            return null;
        }
    }

    private void WriteBinding(string key, object? value)
    {
        if (SetCallback == null)
        {
            return;
        }

        try
        {
            SetCallback.Invoke(key, ToJs(value));
        }
        catch (Exception exception)
        {
            Log($"ui '{Id}': binding set '{key}' failed: {Describe(exception)}");
        }
    }

    private JsValue ToJs(object? value)
    {
        var engine = GetCallback?.Engine ?? SetCallback?.Engine ?? EventCallback?.Engine;
        return engine == null || value == null ? JsValue.Null : value is JsValue js ? js : JsPayload.FromPlain(engine, value);
    }

    private static object? FromJs(JsValue value)
    {
        if (value.IsNull() || value.IsUndefined())
        {
            return null;
        }

        if (value.IsString())
        {
            return value.AsString();
        }

        if (value.IsBoolean())
        {
            return value.AsBoolean();
        }

        if (value.IsNumber())
        {
            return value.AsNumber();
        }

        if (value.IsArray())
        {
            var array = value.AsArray();
            var length = (int)array.Length;
            var list = new List<object?>(length);
            for (var index = 0; index < length; index++)
            {
                list.Add(FromJs(array.Get(index)));
            }

            return list;
        }

        if (value is ObjectInstance obj)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in obj.GetOwnPropertyKeys(Jint.Runtime.Types.String))
            {
                var name = key.ToString();
                map[name] = FromJs(obj.Get(name));
            }

            return map;
        }

        return value.ToObject();
    }

    private void Log(string message) => _log?.Invoke(message);

    private static string Describe(Exception exception)
    {
        var inner = exception;
        while (inner.InnerException != null)
        {
            inner = inner.InnerException;
        }

        return inner.GetType().Name + ": " + inner.Message;
    }

    internal readonly struct UiEvent
    {
        internal UiEvent(string path, string name, object? value)
        {
            Path = path;
            Name = name;
            Value = value;
        }

        internal string Path { get; }
        internal string Name { get; }
        internal object? Value { get; }
    }
}

/// <summary>Base widget wrapper; concrete widgets install a refresh callback.</summary>
internal sealed class UiWidget
{
    internal UiPanel Node = null!;
    internal GameObject Root = null!;
    internal RectTransform Rect = null!;
    internal Transform Children = null!;
    internal Action<UiInstance>? RefreshAction;

    internal void Refresh(UiInstance instance) => RefreshAction?.Invoke(instance);
}
#endif
