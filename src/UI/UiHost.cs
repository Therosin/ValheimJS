#if USE_GUI
using System;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimJS.UI;

/// <summary>Client-only root for plugin panels and popups.</summary>
internal static class UiHost
{
    private static Canvas? _overlay;
    private static Canvas? _windows;
    private static Canvas? _popups;
    private static int _modalCount;
    private static int _panelCount;

    internal static bool HasModal => _modalCount > 0;

    internal static bool HasPanels => _panelCount > 0;

    /// <summary>True while a panel is open and Alt is held.</summary>
    internal static bool CursorUnlock { get; private set; }

    internal static void AddPanel() => _panelCount++;

    internal static void RemovePanel() => _panelCount = Math.Max(0, _panelCount - 1);

    internal static void Tick()
    {
        CursorUnlock = HasPanels &&
            (ZInput.GetKey(KeyCode.LeftAlt) || ZInput.GetKey(KeyCode.RightAlt));
    }

    internal static int TextFocusCount { get; private set; }

    internal static bool TextFocused => TextFocusCount > 0;

    internal static void Ensure()
    {
        if (_overlay != null)
        {
            return;
        }

        _overlay = CreateCanvas("OverlayCanvas", 500);
        _windows = CreateCanvas("WindowCanvas", 1000);
        _popups = CreateCanvas("PopupCanvas", 1600);
    }

    internal static void AddModal() => _modalCount++;

    internal static void RemoveModal() => _modalCount = Math.Max(0, _modalCount - 1);

    internal static void AddTextFocus() => TextFocusCount++;

    internal static void RemoveTextFocus() => TextFocusCount = Math.Max(0, TextFocusCount - 1);

    internal static Transform Parent(string? mount)
    {
        Ensure();
        return mount switch
        {
            "overlay" => _overlay!.transform,
            "popup" => _popups!.transform,
            _ => _windows!.transform
        };
    }

    internal static Transform PopupParent()
    {
        Ensure();
        return _popups!.transform;
    }

    internal static void Dispose()
    {
        if (_overlay != null)
        {
            UnityEngine.Object.Destroy(_overlay.gameObject);
        }

        if (_windows != null)
        {
            UnityEngine.Object.Destroy(_windows.gameObject);
        }

        if (_popups != null)
        {
            UnityEngine.Object.Destroy(_popups.gameObject);
        }

        _overlay = null;
        _windows = null;
        _popups = null;
        _modalCount = 0;
        _panelCount = 0;
        CursorUnlock = false;
        TextFocusCount = 0;
    }

    private static Canvas CreateCanvas(string name, int sortingOrder)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        go.transform.SetParent(ValheimJS.Core.ValheimJsHost.UiRoot, false);
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;

        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;
        return canvas;
    }
}
#endif
