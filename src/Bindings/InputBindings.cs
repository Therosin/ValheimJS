#if USE_INPUT
using System;
using System.Collections.Generic;
using Jint;
using UnityEngine.InputSystem;
using ValheimJS.Input;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

/// <summary>
/// Keyboard bindings and per-frame callbacks for scripts. Action names are namespaced by the
/// calling plugin, so two plugins can both use "open-menu" without colliding.
/// </summary>
[JsBinder("input", Phase = JsBindPhase.Gameplay, Order = 145,
    Availability = JsAvailability.Client,
    Description = "Keyboard bindings and press/hold/release callbacks.")]
internal sealed class InputBindings : JsBindings
{
    private readonly HashSet<string> _owned = new(StringComparer.Ordinal);

    public InputBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding(Scoped = true, Description = "Maps a named action to a key name such as \"F5\" or \"LeftShift\".")]
    public void Bind(string plugin, string action, string key)
    {
        var name = Scoped(plugin, action);
        if (!KeyboardManager.TryParseKey(key, out var parsed))
        {
            throw new ArgumentException($"Unknown key '{key}'.", nameof(key));
        }

        KeyboardManager.RegisterBinding(name, parsed);
        _owned.Add(name);
    }

    [JsBinding(Scoped = true, Description = "Removes an action's key mapping.")]
    public void Unbind(string plugin, string action) => KeyboardManager.UnregisterBinding(Scoped(plugin, action));

    [JsBinding(Scoped = true, Description = "Registers a callback for a phase: \"press\", \"hold\" or \"release\". Returns true from the callback to consume the input.")]
    public void On(string plugin, string action, string phase, JsCallback handler)
    {
        var name = Scoped(plugin, action);
        Func<bool> callback = () => Invoke(plugin, action, phase, handler);
        switch (phase)
        {
            case "press":
                KeyboardManager.RegisterPressAction(name, callback);
                break;
            case "hold":
                KeyboardManager.RegisterHoldAction(name, callback);
                break;
            case "release":
                KeyboardManager.RegisterReleaseAction(name, callback);
                break;
            default:
                throw new ArgumentException($"Unknown keyboard phase '{phase}'.", nameof(phase));
        }

        _owned.Add(name);
    }

    [JsBinding(Scoped = true, Description = "Removes a phase callback.")]
    public void Off(string plugin, string action, string phase)
    {
        var name = Scoped(plugin, action);
        switch (phase)
        {
            case "press":
                KeyboardManager.UnregisterPressAction(name);
                break;
            case "hold":
                KeyboardManager.UnregisterHoldAction(name);
                break;
            case "release":
                KeyboardManager.UnregisterReleaseAction(name);
                break;
            default:
                throw new ArgumentException($"Unknown keyboard phase '{phase}'.", nameof(phase));
        }
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Json, Returns = "KeyState | null",
        Description = "Current state of a named action, binding it to a raw key name when needed.")]
    public string? State(string plugin, string action)
    {
        var name = Scoped(plugin, action);
        if (!KeyboardManager.TryGetBinding(name, out var key))
        {
            if (!KeyboardManager.TryParseKey(action, out key))
            {
                return null;
            }

            KeyboardManager.RegisterBinding(name, key);
            _owned.Add(name);
        }

        return StateOf(key);
    }

    [JsBinding(ReturnKind = JsReturnKind.Json, Returns = "KeyState | null",
        Description = "Current state of a raw key name, without registering an action.")]
    public string? RawState(string key) =>
        KeyboardManager.TryParseKey(key, out var parsed) ? StateOf(parsed) : null;

    [JsBinding(Description = "True when any bound callback claimed input this frame.")]
    public bool HandledAny() => KeyboardManager.HandledAny;

    [JsBinding(Scoped = true, Description = "Removes every binding and callback owned by this plugin.")]
    public void Clear(string plugin) => Release(plugin);

    public override void Release(string plugin)
    {
        var prefix = plugin + "\0";
        foreach (var name in new List<string>(_owned))
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            KeyboardManager.UnregisterBinding(name);
            KeyboardManager.UnregisterAction(name);
            _owned.Remove(name);
        }
    }

    public override void Dispose()
    {
        foreach (var name in _owned)
        {
            KeyboardManager.UnregisterBinding(name);
            KeyboardManager.UnregisterAction(name);
        }

        _owned.Clear();
    }

    /// <summary>Runs the plugin's callback; a truthy result consumes the input for this frame.</summary>
    private static bool Invoke(string plugin, string action, string phase, JsCallback handler)
    {
        try
        {
#if USE_GUI
            if (ValheimJS.UI.UiHost.TextFocused)
            {
                return false;
            }
#endif
            return handler.Invoke(action, phase).AsBoolean();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"[{plugin}] keyboard handler for '{action}' ({phase}) failed: {exception.Message}", exception);
        }
    }

    private static string Scoped(string plugin, string action)
    {
        if (string.IsNullOrWhiteSpace(action) || action.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Invalid keyboard action.", nameof(action));
        }

        return plugin + "\0" + action;
    }

    private static string? StateOf(Key key)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return null;
        }

        var control = keyboard[key];
        if (control == null)
        {
            return null;
        }

        return "{\"isPressed\":" + (control.isPressed ? "true" : "false") +
               ",\"wasPressedThisFrame\":" + (control.wasPressedThisFrame ? "true" : "false") +
               ",\"wasReleasedThisFrame\":" + (control.wasReleasedThisFrame ? "true" : "false") + "}";
    }
}
#endif
