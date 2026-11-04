#if USE_INPUT
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ValheimJS.Input;

/// <summary>Named keyboard bindings and per-frame press, hold and release handlers.</summary>
public static class KeyboardManager
{
    private static readonly Dictionary<string, Key> Bindings = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Func<bool>> Press = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Func<bool>> Hold = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Func<bool>> Release = new(StringComparer.Ordinal);
    private static readonly HashSet<Key> ClaimedKeys = new();
    private static int _frame = -1;
    public static bool HandledAny { get; private set; }

    public static void RegisterBinding(string action, Key key)
    {
        if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("Action must not be empty.", nameof(action));
        if (key == Key.None || !Enum.IsDefined(typeof(Key), key)) throw new ArgumentOutOfRangeException(nameof(key));
        Bindings[action] = key;
    }
    public static void UnregisterBinding(string action) => Bindings.Remove(action);
    public static void RegisterAction(string action, Func<bool> handler) => RegisterPressAction(action, handler);
    public static void RegisterAction(string action, Action handler) => RegisterPressAction(action, handler);
    public static void RegisterPressAction(string action, Func<bool> handler) => Add(Press, action, handler);
    public static void RegisterHoldAction(string action, Func<bool> handler) => Add(Hold, action, handler);
    public static void RegisterReleaseAction(string action, Func<bool> handler) => Add(Release, action, handler);
    public static void RegisterPressAction(string action, Action handler) => RegisterPressAction(action, Wrap(handler));
    public static void RegisterHoldAction(string action, Action handler) => RegisterHoldAction(action, Wrap(handler));
    public static void RegisterReleaseAction(string action, Action handler) => RegisterReleaseAction(action, Wrap(handler));
    public static void UnregisterPressAction(string action) => Press.Remove(action);
    public static void UnregisterHoldAction(string action) => Hold.Remove(action);
    public static void UnregisterReleaseAction(string action) => Release.Remove(action);
    public static void UnregisterAction(string action) { Press.Remove(action); Hold.Remove(action); Release.Remove(action); }
    public static void Clear() { Bindings.Clear(); Press.Clear(); Hold.Clear(); Release.Clear(); ClaimedKeys.Clear(); HandledAny = false; _frame = -1; }
    // FixedUpdate can consume the last dynamic input sample before this frame's Update runs.
    internal static bool IsClaimed(Key key) =>
        (_frame == Time.frameCount || (Time.inFixedTimeStep && _frame == Time.frameCount - 1)) && ClaimedKeys.Contains(key);

    public static bool TryGetBinding(string action, out Key key)
    {
        if (!string.IsNullOrWhiteSpace(action) && Bindings.TryGetValue(action, out key)) return true;
        if (TryParseKey(action, out key)) { Bindings[action] = key; return true; }
        return false;
    }
    public static bool TryParseKey(string? name, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = name!.Trim().Replace(" ", "").Replace("-", "").Replace("_", "").ToLowerInvariant();
        if (normalized == "ctrl" || normalized == "control") normalized = "leftctrl";
        if (normalized == "alt") normalized = "leftalt";
        if (normalized == "shift") normalized = "leftshift";
        if (normalized == "return") normalized = "enter";
        if (normalized == "esc") normalized = "escape";
        foreach (var candidate in Enum.GetNames(typeof(Key)))
        {
            if (candidate.Replace("_", "").ToLowerInvariant() != normalized) continue;
            key = (Key)Enum.Parse(typeof(Key), candidate);
            return key != Key.None;
        }
        return false;
    }
    public static void Update()
    {
        if (_frame == Time.frameCount) return;
        _frame = Time.frameCount;
        HandledAny = false;
        ClaimedKeys.Clear();
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        // Handlers may register or remove bindings during dispatch.
        foreach (var entry in new List<KeyValuePair<string, Key>>(Bindings))
        {
            if (!Bindings.TryGetValue(entry.Key, out var current) || current != entry.Value) continue;
            var control = keyboard[entry.Value];
            if (control == null) continue;
            if (control.wasPressedThisFrame) Invoke(Press, entry.Key, entry.Value);
            if (control.isPressed) Invoke(Hold, entry.Key, entry.Value);
            if (control.wasReleasedThisFrame) Invoke(Release, entry.Key, entry.Value);
        }
    }
    private static Func<bool> Wrap(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        return () => { action(); return false; };
    }
    private static void Add(Dictionary<string, Func<bool>> handlers, string action, Func<bool> handler)
    {
        if (string.IsNullOrWhiteSpace(action)) throw new ArgumentException("Action must not be empty.", nameof(action));
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (handlers.ContainsKey(action)) throw new InvalidOperationException($"Action '{action}' already has a handler for this phase.");
        handlers.Add(action, handler);
    }
    private static void Invoke(Dictionary<string, Func<bool>> handlers, string action, Key key)
    {
        if (!handlers.TryGetValue(action, out var handler)) return;
        try { if (handler()) { HandledAny = true; ClaimedKeys.Add(key); } }
        catch (Exception exception) { Plugin.Logger.LogWarning("[Keyboard] handler failed: " + exception); }
    }
}
#endif
