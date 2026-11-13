#if USE_GUI
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ValheimJS.Core;

namespace ValheimJS.UI;

/// <summary>Small flat select control; the popup lives on the popup canvas so it is not clipped.</summary>
internal sealed class UiSelect : MonoBehaviour
{
    internal UiInstance Instance = null!;
    internal UiPanel Node = null!;
    internal TextMeshProUGUI Label = null!;
    internal RectTransform RootRect = null!;

    private GameObject? _popup;

    internal void Open()
    {
        Close();
        var options = Options();
        if (options.Count == 0)
        {
            return;
        }

        var parent = UiHost.PopupParent();
        var width = Mathf.Max(140f, RootRect.rect.width);
        var height = options.Count * 32f + 8f;
        var popup = UiKit.Rect("SelectPopup", parent);
        var rect = popup.GetComponent<RectTransform>();
        rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(width, height);
        rect.position = RootRect.TransformPoint(new Vector3(-RootRect.rect.width * 0.5f, -RootRect.rect.height * 0.5f, 0f));
        UiKit.Image(popup, UiTheme.Surface, UiTheme.Rounded, sliced: true);
        var layout = popup.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(4, 4, 4, 4);
        layout.spacing = 2f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var current = Instance.Text(Node, "value", "");
        foreach (var option in options)
        {
            var row = UiKit.Rect("Option", popup.transform);
            var image = UiKit.Image(row, option == current ? UiTheme.Raised : UiTheme.Transparent(), UiTheme.Rounded, sliced: true);
            var button = row.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            var label = UiKit.Text("Label", row.transform, 13f, UiTheme.Text, FontStyles.Normal);
            UiKit.Stretch(label.rectTransform, 10f, 4f);
            label.text = option;
            var element = row.AddComponent<LayoutElement>();
            element.preferredHeight = 30f;
            var value = option;
            button.onClick.AddListener(() =>
            {
                Instance.SetProp(Node, "value", value);
                Instance.QueueEvent(Node.Path, "change", value);
                Label.text = value;
                Close();
            });
        }

        _popup = popup;
    }

    internal void Refresh()
    {
        Label.text = Instance.Text(Node, "value", "");
    }

    internal void Close()
    {
        if (_popup != null)
        {
            Destroy(_popup);
            _popup = null;
        }
    }

    private List<string> Options()
    {
        var result = new List<string>();
        var raw = Instance.ResolveProp(Node, "options");
        if (raw is List<object?> list)
        {
            foreach (var item in list)
            {
                if (item != null)
                {
                    result.Add(item.ToString() ?? string.Empty);
                }
            }
        }

        return result;
    }

    private void OnDisable() => Close();

    private void OnDestroy() => Close();
}
#endif
