#if USE_GUI
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimJS.UI;

/// <summary>Flat dark host theme shared by every plugin panel.</summary>
internal static class UiTheme
{
    internal static readonly Color Window = Hex("#121214", 0.92f);
    internal static readonly Color Surface = Hex("#1a1a1d", 0.96f);
    internal static readonly Color Raised = Hex("#212126", 1f);
    internal static readonly Color Hover = Hex("#29292f", 1f);
    internal static readonly Color Active = Hex("#303037", 1f);
    internal static readonly Color Border = Hex("#ffffff", 0.07f);
    internal static readonly Color Text = Hex("#ececf0", 1f);
    internal static readonly Color Secondary = Hex("#b4b4bc", 1f);
    internal static readonly Color Muted = Hex("#8b8b93", 1f);
    internal static readonly Color Accent = Hex("#c9a24a", 1f);
    internal static readonly Color Success = Hex("#63a46a", 1f);
    internal static readonly Color Danger = Hex("#b85c5c", 1f);

    private static TMP_FontAsset? _font;
    private static Sprite? _rounded;
    private static Sprite? _circle;

    internal static TMP_FontAsset Font
    {
        get
        {
            if (_font != null)
            {
                return _font;
            }

            _font = TMP_Settings.defaultFontAsset;
            if (_font == null)
            {
                _font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(asset => asset != null);
            }

            if (_font == null)
            {
                throw new InvalidOperationException("ui: no TMP font asset is available.");
            }

            return _font;
        }
    }

    internal static Sprite Rounded
    {
        get
        {
            if (_rounded == null)
            {
                _rounded = MakeRounded(32, 7, new Vector4(8, 8, 8, 8));
            }

            return _rounded;
        }
    }

    internal static Sprite Circle
    {
        get
        {
            if (_circle == null)
            {
                _circle = MakeCircle(32);
            }

            return _circle;
        }
    }

    internal static Color Token(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "window" => Window,
            "surface" => Surface,
            "raised" => Raised,
            "hover" => Hover,
            "active" => Active,
            "border" => Border,
            "text" => Text,
            "secondary" => Secondary,
            "muted" => Muted,
            "accent" => Accent,
            "success" => Success,
            "danger" => Danger,
            "transparent" => new Color(0f, 0f, 0f, 0f),
            _ => value.StartsWith("#", StringComparison.Ordinal) ? Hex(value, 1f) : fallback
        };
    }

    internal static float Size(string? value, float fallback) => value switch
    {
        "sm" => 12f,
        "md" => 14f,
        "lg" => 18f,
        "title" => 26f,
        "section" => 19f,
        _ => fallback
    };

    internal static Color Transparent() => new(0f, 0f, 0f, 0f);

    internal static Color Hex(string value, float alpha)
    {
        if (!ColorUtility.TryParseHtmlString(value, out var color))
        {
            color = Color.magenta;
        }

        color.a = alpha;
        return color;
    }

    private static Sprite MakeRounded(int size, int radius, Vector4 border)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "ValheimJS_Rounded",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };

        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var inside = InsideRounded(x, y, size, radius);
                pixels[y * size + x] = inside ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect, border);
    }

    private static Sprite MakeCircle(int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "ValheimJS_Circle",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };

        var pixels = new Color32[size * size];
        var center = (size - 1) * 0.5f;
        var radius = center - 0.5f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var dy = y - center;
                pixels[y * size + x] = dx * dx + dy * dy <= radius * radius
                    ? new Color32(255, 255, 255, 255)
                    : new Color32(255, 255, 255, 0);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    private static bool InsideRounded(int x, int y, int size, int radius)
    {
        var min = radius;
        var max = size - 1 - radius;
        var cx = x < min ? min : x > max ? max : x;
        var cy = y < min ? min : y > max ? max : y;
        var dx = x - cx;
        var dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }
}
#endif
