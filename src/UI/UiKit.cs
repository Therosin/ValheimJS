#if USE_GUI
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ValheimJS.Core;

namespace ValheimJS.UI;

/// <summary>Small uGUI/TMP construction helpers shared by the renderer.</summary>
internal static class UiKit
{
    internal static GameObject Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    internal static Image Image(GameObject go, Color color, Sprite sprite, bool sliced)
    {
        var image = go.GetComponent<UnityEngine.UI.Image>() ?? go.AddComponent<UnityEngine.UI.Image>();
        image.sprite = sprite;
        image.color = color;
        image.type = sliced ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
        image.raycastTarget = true;
        return image;
    }

    internal static TextMeshProUGUI Text(string name, Transform parent, float size, Color color, FontStyles style)
    {
        var go = Rect(name, parent);
        var text = go.AddComponent<TextMeshProUGUI>();
        text.font = UiTheme.Font;
        text.fontSize = size;
        text.color = color;
        text.fontStyle = style;
        text.raycastTarget = false;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Overflow;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        return text;
    }

    internal static Button TextButton(string name, Transform parent, string label, Color color)
    {
        var go = Rect(name, parent);
        var hit = go.AddComponent<Image>();
        hit.color = new Color(0f, 0f, 0f, 0f);
        hit.raycastTarget = true;
        var button = go.AddComponent<Button>();
        button.targetGraphic = hit;
        button.transition = Selectable.Transition.ColorTint;
        var text = Text("Label", go.transform, 18f, color, FontStyles.Normal);
        text.text = label;
        text.alignment = TextAlignmentOptions.Center;
        Stretch(text.rectTransform);
        var element = go.AddComponent<LayoutElement>();
        element.preferredWidth = 24f;
        element.preferredHeight = 24f;
        return button;
    }

    internal static void Stretch(RectTransform rect, float horizontal = 0f, float vertical = 0f)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(horizontal, vertical);
        rect.offsetMax = new Vector2(-horizontal, -vertical);
    }

    internal static void AnchorStretchTop(RectTransform rect, float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(0f, height);
    }

    internal static void AnchorStretch(RectTransform rect, float top, float bottom)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(0f, bottom);
        rect.offsetMax = new Vector2(0f, -top);
    }

    internal static void Flex(GameObject go, float flexWidth, float flexHeight = 0f)
    {
        var element = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        element.flexibleWidth = flexWidth;
        element.flexibleHeight = flexHeight;
    }

    internal static void ApplyLayoutGroup(LayoutGroup group, UiPanel node, UiInstance instance)
    {
        if (group is HorizontalOrVerticalLayoutGroup spaced)
        {
            spaced.spacing = (float)instance.Number(node, "gap", 8f);
        }

        var padding = Mathf.Max(0, (int)instance.Number(node, "padding", 0f));
        group.padding = new RectOffset(padding, padding, padding, padding);
        group.childAlignment = Anchor(instance.Text(node, "align", "top-left"));

        if (group is VerticalLayoutGroup vertical)
        {
            vertical.childControlWidth = true;
            vertical.childControlHeight = true;
            vertical.childForceExpandWidth = true;
            vertical.childForceExpandHeight = false;
        }
        else if (group is HorizontalLayoutGroup horizontal)
        {
            horizontal.childControlWidth = true;
            horizontal.childControlHeight = true;
            horizontal.childForceExpandWidth = false;
            horizontal.childForceExpandHeight = false;
        }
    }

    internal static void ApplySize(UiInstance instance, UiPanel node, GameObject go)
    {
        var width = instance.Number(node, "width", 0d);
        var height = instance.Number(node, "height", 0d);
        var minWidth = instance.Number(node, "minWidth", 0d);
        var minHeight = instance.Number(node, "minHeight", 0d);
        var grow = instance.Number(node, "grow", 0d);
        var growHeight = instance.Number(node, "growHeight", 0d);
        if (width <= 0 && height <= 0 && minWidth <= 0 && minHeight <= 0 && grow <= 0 && growHeight <= 0)
        {
            return;
        }

        var element = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        if (width > 0)
        {
            element.preferredWidth = (float)width;
        }

        if (height > 0)
        {
            element.preferredHeight = (float)height;
        }

        if (minWidth > 0)
        {
            element.minWidth = (float)minWidth;
        }

        if (minHeight > 0)
        {
            element.minHeight = (float)minHeight;
        }

        if (grow > 0)
        {
            element.flexibleWidth = (float)grow;
        }

        if (growHeight > 0)
        {
            element.flexibleHeight = (float)growHeight;
        }
    }

    internal static void ApplyVisibility(UiInstance instance, UiPanel node, GameObject go)
    {
        go.SetActive(instance.Flag(node, "visible", true));
    }

    internal static TextAlignmentOptions Alignment(string? value) => value switch
    {
        "center" => TextAlignmentOptions.Center,
        "right" => TextAlignmentOptions.MidlineRight,
        _ => TextAlignmentOptions.MidlineLeft
    };

    internal static TextAnchor Anchor(string? value) => value switch
    {
        "center" => TextAnchor.MiddleCenter,
        "right" => TextAnchor.MiddleRight,
        "top" => TextAnchor.UpperLeft,
        "bottom" => TextAnchor.LowerLeft,
        _ => TextAnchor.UpperLeft
    };

    internal static string Glyph(string? name) => name switch
    {
        "settings" => "⚙",
        "users" => "◉",
        "globe" => "◎",
        "grid" => "▦",
        "check" => "✓",
        "close" => "×",
        "minus" => "−",
        "plus" => "+",
        "dot" => "•",
        "save" => "✓",
        _ => string.IsNullOrEmpty(name) ? "•" : name
    };
}
#endif
