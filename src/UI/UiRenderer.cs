#if USE_GUI
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ValheimJS.Core;

namespace ValheimJS.UI;

/// <summary>Builds the flat uGUI/TMP representation of a compiled panel tree.</summary>
internal static class UiRenderer
{
    internal static UiInstance Build(UiInstance instance, string mount)
    {
        var parent = UiHost.Parent(mount);
        var root = BuildNode(instance, instance.Document.Root, parent);
        instance.RootObject = root;
        instance.RootRect = root.GetComponent<RectTransform>();
        if (instance.Document.Root.Type == "window")
        {
            ApplyWindowPlacement(instance, root.GetComponent<RectTransform>());
        }
        else
        {
            var rect = root.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
        }

        instance.Dirty = true;
        return instance;
    }

    private static GameObject BuildNode(UiInstance instance, UiPanel node, Transform parent)
    {
        switch (node.Type)
        {
            case "window":
                return BuildWindow(instance, node, parent);
            case "tabs":
                return BuildTabs(instance, node, parent);
            case "row":
                return BuildLayout(instance, node, parent, horizontal: true);
            case "column":
                return BuildLayout(instance, node, parent, horizontal: false);
            case "panel":
                return BuildPanel(instance, node, parent);
            case "grid":
                return BuildGrid(instance, node, parent);
            case "scroll":
                return BuildScroll(instance, node, parent);
            case "spacer":
                return BuildSpacer(instance, node, parent);
            case "divider":
                return BuildDivider(instance, node, parent);
            case "text":
                return BuildText(instance, node, parent);
            case "image":
                return BuildImage(instance, node, parent);
            case "icon":
                return BuildIcon(instance, node, parent);
            case "button":
                return BuildButton(instance, node, parent);
            case "input":
                return BuildInput(instance, node, parent);
            case "toggle":
                return BuildToggle(instance, node, parent);
            case "slider":
                return BuildSlider(instance, node, parent);
            case "select":
                return BuildSelect(instance, node, parent);
            case "section":
                return BuildSection(instance, node, parent);
            case "setting-row":
                return BuildSettingRow(instance, node, parent);
            case "list-row":
                return BuildListRow(instance, node, parent);
            case "footer":
                return BuildFooter(instance, node, parent);
            case "tab":
                return BuildTab(instance, node, parent);
            default:
                throw new InvalidOperationException($"ui: unsupported panel type '{node.Type}'.");
        }
    }

    private static void BuildChildren(UiInstance instance, UiPanel node, Transform parent)
    {
        foreach (var child in node.Children)
        {
            BuildNode(instance, child, parent);
        }
    }

    private static GameObject BuildWindow(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Window", parent);
        var image = UiKit.Image(root, UiTheme.Window, UiTheme.Rounded, sliced: true);
        var group = root.AddComponent<CanvasGroup>();
        group.alpha = Mathf.Clamp01((float)instance.Number(node, "opacity", 1f));
        var inputGroup = root.AddComponent<UIGroupHandler>();
        inputGroup.m_groupPriority = 100;
        inputGroup.m_setDefaultOnKBM = false;
        inputGroup.m_resetActiveElementOnStateChange = true;

        var rect = root.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2((float)instance.Number(node, "width", 760f), (float)instance.Number(node, "height", 560f));

        var header = UiKit.Rect("Header", root.transform);
        header.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 48f);
        UiKit.AnchorStretchTop(header.GetComponent<RectTransform>(), 48f);
        var headerHit = header.AddComponent<Image>();
        headerHit.color = new Color(0f, 0f, 0f, 0f);
        headerHit.raycastTarget = true;
        var headerLayout = header.AddComponent<HorizontalLayoutGroup>();
        headerLayout.padding = new RectOffset(16, 8, 8, 4);
        headerLayout.spacing = 10f;
        headerLayout.childAlignment = TextAnchor.MiddleLeft;
        headerLayout.childControlWidth = true;
        headerLayout.childControlHeight = true;
        headerLayout.childForceExpandWidth = false;

        var icon = UiKit.Text("Icon", header.transform, UiTheme.Size("lg", 18f), UiTheme.Accent, FontStyles.Bold);
        icon.text = UiKit.Glyph(instance.Text(node, "icon", ""));
        icon.rectTransform.sizeDelta = new Vector2(26f, 26f);

        var titleColumn = UiKit.Rect("Title", header.transform);
        var titleLayout = titleColumn.AddComponent<VerticalLayoutGroup>();
        titleLayout.spacing = 0f;
        titleLayout.childControlWidth = true;
        titleLayout.childControlHeight = true;
        titleLayout.childForceExpandWidth = true;
        UiKit.Flex(titleColumn, 1f);

        var title = UiKit.Text("TitleText", titleColumn.transform, 20f, UiTheme.Text, FontStyles.Bold);
        var subtitle = UiKit.Text("Subtitle", titleColumn.transform, 12f, UiTheme.Muted, FontStyles.Normal);
        var drag = header.AddComponent<WindowDrag>();
        drag.Target = rect;

        var collapse = UiKit.TextButton("Collapse", header.transform, "−", UiTheme.Secondary);
        var collapseText = collapse.GetComponentInChildren<TextMeshProUGUI>();
        var close = UiKit.TextButton("Close", header.transform, "×", UiTheme.Secondary);
        close.onClick.AddListener(() =>
        {
            instance.QueueEvent(node.Path, "close", null);
            instance.RequestClose();
        });

        var content = UiKit.Rect("Content", root.transform);
        UiKit.AnchorStretch(content.GetComponent<RectTransform>(), 48f, 0f);
        var contentLayout = content.AddComponent<VerticalLayoutGroup>();
        contentLayout.padding = new RectOffset(16, 16, 10, 16);
        contentLayout.spacing = 10f;
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = true;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;

        collapse.onClick.AddListener(() =>
        {
            content.SetActive(!content.activeSelf);
            collapseText.text = content.activeSelf ? "−" : "+";
        });

        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = rect,
            Children = content.transform,
            RefreshAction = current =>
            {
                UiKit.ApplyVisibility(current, node, root);
                title.text = current.Text(node, "title", "");
                subtitle.text = current.Text(node, "subtitle", "");
                subtitle.gameObject.SetActive(subtitle.text.Length > 0);
            }
        };
        instance.Widgets[node.Path] = widget;
        BuildChildren(instance, node, content.transform);
        return root;
    }

    private static GameObject BuildLayout(UiInstance instance, UiPanel node, Transform parent, bool horizontal)
    {
        var root = UiKit.Rect(horizontal ? "Row" : "Column", parent);
        var background = instance.Text(node, "background", "");
        if (background.Length > 0)
        {
            UiKit.Image(root, UiTheme.Token(background, UiTheme.Surface), UiTheme.Rounded, sliced: true);
        }

        var layout = horizontal ? (LayoutGroup)root.AddComponent<HorizontalLayoutGroup>() : root.AddComponent<VerticalLayoutGroup>();
        UiKit.ApplyLayoutGroup(layout, node, instance);
        UiKit.ApplySize(instance, node, root);
        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            Children = root.transform,
            RefreshAction = current => UiKit.ApplyVisibility(current, node, root)
        };
        instance.Widgets[node.Path] = widget;
        BuildChildren(instance, node, root.transform);
        return root;
    }

    private static GameObject BuildPanel(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Panel", parent);
        UiKit.Image(root, UiTheme.Token(instance.Text(node, "background", "surface"), UiTheme.Surface), UiTheme.Rounded, sliced: true);
        var layout = root.AddComponent<VerticalLayoutGroup>();
        UiKit.ApplyLayoutGroup(layout, node, instance);
        UiKit.ApplySize(instance, node, root);
        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            Children = root.transform,
            RefreshAction = current => UiKit.ApplyVisibility(current, node, root)
        };
        instance.Widgets[node.Path] = widget;
        BuildChildren(instance, node, root.transform);
        return root;
    }

    private static GameObject BuildGrid(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Grid", parent);
        var grid = root.AddComponent<GridLayoutGroup>();
        var columns = Math.Max(1, (int)instance.Number(node, "columns", 2));
        var gap = (float)instance.Number(node, "gap", 8f);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = columns;
        grid.spacing = new Vector2(gap, gap);
        grid.cellSize = new Vector2((float)instance.Number(node, "cellWidth", 160f), (float)instance.Number(node, "cellHeight", 40f));
        UiKit.ApplySize(instance, node, root);
        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            Children = root.transform,
            RefreshAction = current => UiKit.ApplyVisibility(current, node, root)
        };
        instance.Widgets[node.Path] = widget;
        BuildChildren(instance, node, root.transform);
        return root;
    }

    private static GameObject BuildScroll(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Scroll", parent);
        var scroll = root.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        UiKit.ApplySize(instance, node, root);

        var viewport = UiKit.Rect("Viewport", root.transform);
        UiKit.Stretch(viewport.GetComponent<RectTransform>());
        viewport.AddComponent<RectMask2D>();
        var viewportImage = viewport.AddComponent<Image>();
        viewportImage.color = new Color(0f, 0f, 0f, 0.001f);
        scroll.viewport = viewport.GetComponent<RectTransform>();

        var content = UiKit.Rect("Content", viewport.transform);
        var contentRect = content.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        var layout = content.AddComponent<VerticalLayoutGroup>();
        UiKit.ApplyLayoutGroup(layout, node, instance);
        var fitter = content.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = contentRect;

        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            Children = content.transform,
            RefreshAction = current => UiKit.ApplyVisibility(current, node, root)
        };
        instance.Widgets[node.Path] = widget;
        BuildChildren(instance, node, content.transform);
        return root;
    }

    private static GameObject BuildSpacer(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Spacer", parent);
        var element = root.AddComponent<LayoutElement>();
        element.flexibleWidth = (float)instance.Number(node, "grow", 1f);
        element.flexibleHeight = (float)instance.Number(node, "growHeight", 0f);
        element.preferredHeight = (float)instance.Number(node, "height", 8f);
        UiKit.ApplySize(instance, node, root);
        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            RefreshAction = current => UiKit.ApplyVisibility(current, node, root)
        };
        instance.Widgets[node.Path] = widget;
        return root;
    }

    private static GameObject BuildDivider(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Divider", parent);
        var image = root.AddComponent<Image>();
        image.color = UiTheme.Border;
        image.raycastTarget = false;
        var element = root.AddComponent<LayoutElement>();
        element.preferredHeight = (float)instance.Number(node, "height", 1f);
        element.flexibleWidth = 1f;
        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            RefreshAction = current => UiKit.ApplyVisibility(current, node, root)
        };
        instance.Widgets[node.Path] = widget;
        return root;
    }

    private static GameObject BuildText(UiInstance instance, UiPanel node, Transform parent)
    {
        var text = UiKit.Text("Text", parent, UiTheme.Size(instance.Text(node, "size", "md"), 14f),
            UiTheme.Token(instance.Text(node, "tone", "text"), UiTheme.Text), FontStyles.Normal);
        var widget = new UiWidget
        {
            Node = node,
            Root = text.gameObject,
            Rect = text.rectTransform,
            RefreshAction = current =>
            {
                UiKit.ApplyVisibility(current, node, text.gameObject);
                text.text = current.Text(node, "text", "");
                text.color = UiTheme.Token(current.Text(node, "tone", "text"), UiTheme.Text);
                text.fontSize = UiTheme.Size(current.Text(node, "size", "md"), 14f);
                text.fontStyle = current.Flag(node, "bold", false) ? FontStyles.Bold : FontStyles.Normal;
                text.alignment = UiKit.Alignment(current.Text(node, "align", "left"));
            }
        };
        instance.Widgets[node.Path] = widget;
        return text.gameObject;
    }

    private static GameObject BuildImage(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Image", parent);
        var image = UiKit.Image(root, UiTheme.Token(instance.Text(node, "color", "raised"), UiTheme.Raised), UiTheme.Rounded, sliced: true);
        image.raycastTarget = false;
        UiKit.ApplySize(instance, node, root);
        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            RefreshAction = current =>
            {
                UiKit.ApplyVisibility(current, node, root);
                image.color = UiTheme.Token(current.Text(node, "color", "raised"), UiTheme.Raised);
            }
        };
        instance.Widgets[node.Path] = widget;
        return root;
    }

    private static GameObject BuildIcon(UiInstance instance, UiPanel node, Transform parent)
    {
        var text = UiKit.Text("Icon", parent, UiTheme.Size(instance.Text(node, "size", "lg"), 18f),
            UiTheme.Token(instance.Text(node, "tone", "accent"), UiTheme.Accent), FontStyles.Bold);
        text.text = UiKit.Glyph(instance.Text(node, "name", "dot"));
        var widget = new UiWidget
        {
            Node = node,
            Root = text.gameObject,
            Rect = text.rectTransform,
            RefreshAction = current => UiKit.ApplyVisibility(current, node, text.gameObject)
        };
        instance.Widgets[node.Path] = widget;
        return text.gameObject;
    }

    private static GameObject BuildButton(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Button", parent);
        var image = UiKit.Image(root, ButtonColor(instance, node), UiTheme.Rounded, sliced: true);
        var button = root.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;
        button.colors = ButtonColors(instance, node);
        var label = UiKit.Text("Label", root.transform, UiTheme.Size(instance.Text(node, "size", "md"), 14f),
            ButtonText(instance, node), FontStyles.Normal);
        UiKit.Stretch(label.rectTransform, 10f, 4f);
        label.alignment = TextAlignmentOptions.Center;
        var element = root.AddComponent<LayoutElement>();
        element.preferredHeight = (float)instance.Number(node, "height", 34f);
        element.minHeight = element.preferredHeight;
        element.flexibleHeight = 0f;
        element.preferredWidth = (float)instance.Number(node, "width", 100f);
        element.minWidth = element.preferredWidth;
        if (instance.Flag(node, "grow", false))
        {
            element.flexibleWidth = 1f;
        }

        button.onClick.AddListener(() =>
        {
            instance.QueueEvent(node.Path, instance.Text(node, "onPress", "press"), null);
        });

        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            RefreshAction = current =>
            {
                UiKit.ApplyVisibility(current, node, root);
                label.text = current.Text(node, "label", "");
                label.color = ButtonText(current, node);
                image.color = ButtonColor(current, node);
                button.interactable = current.Flag(node, "enabled", true);
            }
        };
        instance.Widgets[node.Path] = widget;
        return root;
    }

    private static GameObject BuildInput(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Input", parent);
        var background = UiKit.Image(root, UiTheme.Raised, UiTheme.Rounded, sliced: true);
        var element = root.AddComponent<LayoutElement>();
        element.preferredHeight = (float)instance.Number(node, "height", 32f);
        element.minHeight = element.preferredHeight;
        element.flexibleHeight = 0f;
        element.preferredWidth = (float)instance.Number(node, "width", 180f);
        element.minWidth = element.preferredWidth;
        if (instance.Flag(node, "grow", false))
        {
            element.flexibleWidth = 1f;
        }

        var viewport = UiKit.Rect("Text Area", root.transform);
        UiKit.Stretch(viewport.GetComponent<RectTransform>(), 10f, 6f);
        viewport.AddComponent<RectMask2D>();

        var text = UiKit.Text("Text", viewport.transform, 14f, UiTheme.Text, FontStyles.Normal);
        UiKit.Stretch(text.rectTransform);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.richText = false;

        var placeholder = UiKit.Text("Placeholder", viewport.transform, 14f, UiTheme.Muted, FontStyles.Italic);
        UiKit.Stretch(placeholder.rectTransform);
        placeholder.alignment = TextAlignmentOptions.MidlineLeft;
        placeholder.text = instance.Text(node, "placeholder", "");

        var input = root.AddComponent<TMP_InputField>();
        input.targetGraphic = background;
        input.textViewport = viewport.GetComponent<RectTransform>();
        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = TMP_InputField.LineType.SingleLine;
        var focus = root.AddComponent<UiTextFocus>();
        input.onSelect.AddListener(_ => focus.Set(true));
        input.onDeselect.AddListener(_ => focus.Set(false));
        input.onEndEdit.AddListener(value =>
        {
            instance.SetProp(node, "value", value);
            instance.QueueEvent(node.Path, "change", value);
        });
        input.onSubmit.AddListener(value => instance.QueueEvent(node.Path, "submit", value));

        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            RefreshAction = current =>
            {
                UiKit.ApplyVisibility(current, node, root);
                if (!input.isFocused)
                {
                    var value = current.Text(node, "value", "");
                    if (input.text != value)
                    {
                        input.SetTextWithoutNotify(value);
                    }
                }

                placeholder.text = current.Text(node, "placeholder", "");
            }
        };
        instance.Widgets[node.Path] = widget;
        return root;
    }

    private static GameObject BuildToggle(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Toggle", parent);
        var label = instance.Text(node, "label", "");
        Toggle toggle;
        TextMeshProUGUI? labelText = null;
        if (label.Length > 0)
        {
            var layout = root.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            labelText = UiKit.Text("Label", root.transform, 14f, UiTheme.Text, FontStyles.Normal);
            UiKit.Flex(labelText.gameObject, 1f);
            labelText.text = label;
        }

        var switchRoot = UiKit.Rect("Switch", root.transform);
        var switchRect = switchRoot.GetComponent<RectTransform>();
        switchRect.sizeDelta = new Vector2(40f, 22f);
        var element = switchRoot.AddComponent<LayoutElement>();
        element.preferredWidth = 40f;
        element.preferredHeight = 22f;
        if (label.Length == 0)
        {
            var rootElement = root.AddComponent<LayoutElement>();
            rootElement.preferredWidth = 40f;
            rootElement.preferredHeight = 22f;
        }
        var background = UiKit.Image(switchRoot, UiTheme.Active, UiTheme.Rounded, sliced: true);
        var knob = UiKit.Rect("Knob", switchRoot.transform);
        var knobImage = UiKit.Image(knob, UiTheme.Text, UiTheme.Circle, sliced: false);
        knob.GetComponent<RectTransform>().sizeDelta = new Vector2(16f, 16f);
        knob.GetComponent<RectTransform>().anchoredPosition = new Vector2(3f, 0f);
        toggle = switchRoot.AddComponent<Toggle>();
        toggle.targetGraphic = background;
        toggle.graphic = knobImage;
        toggle.transition = Selectable.Transition.None;
        toggle.onValueChanged.AddListener(value =>
        {
            instance.SetProp(node, "value", value);
            instance.QueueEvent(node.Path, "change", value);
            ApplyToggleVisual(toggle, knob.GetComponent<RectTransform>(), background, value);
        });

        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            RefreshAction = current =>
            {
                UiKit.ApplyVisibility(current, node, root);
                var value = current.Flag(node, "value", false);
                if (toggle.isOn != value)
                {
                    toggle.SetIsOnWithoutNotify(value);
                }

                ApplyToggleVisual(toggle, knob.GetComponent<RectTransform>(), background, value);
                if (labelText != null)
                {
                    labelText.text = current.Text(node, "label", "");
                }
            }
        };
        instance.Widgets[node.Path] = widget;
        return root;
    }

    private static GameObject BuildSlider(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Slider", parent);
        var layout = root.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 10f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var rootElement = root.AddComponent<LayoutElement>();
        rootElement.preferredHeight = 32f;
        rootElement.minHeight = 32f;
        rootElement.flexibleHeight = 0f;
        rootElement.preferredWidth = 300f;

        var track = UiKit.Rect("Track", root.transform);
        UiKit.Flex(track, 1f);
        var trackRect = track.GetComponent<RectTransform>();
        trackRect.sizeDelta = new Vector2(0f, 22f);
        var trackElement = track.AddComponent<LayoutElement>();
        trackElement.preferredHeight = 22f;
        trackElement.minHeight = 22f;
        trackElement.flexibleHeight = 0f;
        var background = UiKit.Image(track, UiTheme.Active, UiTheme.Rounded, sliced: true);
        var fillArea = UiKit.Rect("Fill Area", track.transform);
        UiKit.Stretch(fillArea.GetComponent<RectTransform>(), 2f, 4f);
        var fill = UiKit.Image(fillArea, UiTheme.Accent, UiTheme.Rounded, sliced: true);
        var handleArea = UiKit.Rect("Handle Slide Area", track.transform);
        UiKit.Stretch(handleArea.GetComponent<RectTransform>(), 8f, 0f);
        var handle = UiKit.Rect("Handle", handleArea.transform);
        var handleImage = UiKit.Image(handle, UiTheme.Text, UiTheme.Circle, sliced: false);
        handleImage.preserveAspect = true;
        var handleRect = handle.GetComponent<RectTransform>();
        handleRect.anchorMin = handleRect.anchorMax = new Vector2(0.5f, 0.5f);
        handleRect.pivot = new Vector2(0.5f, 0.5f);
        handleRect.sizeDelta = new Vector2(14f, 14f);

        var slider = track.AddComponent<Slider>();
        slider.targetGraphic = handleImage;
        slider.fillRect = fill.rectTransform;
        slider.handleRect = handleRect;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = (float)instance.Number(node, "min", 0f);
        slider.maxValue = (float)instance.Number(node, "max", 1f);
        slider.wholeNumbers = instance.Flag(node, "whole", false);

        var valueText = UiKit.Text("Value", root.transform, 12f, UiTheme.Secondary, FontStyles.Normal);
        valueText.alignment = TextAlignmentOptions.MidlineRight;
        var valueElement = valueText.gameObject.AddComponent<LayoutElement>();
        valueElement.preferredWidth = 58f;
        valueElement.preferredHeight = 22f;

        slider.onValueChanged.AddListener(value =>
        {
            instance.SetProp(node, "value", value);
            instance.QueueEvent(node.Path, "change", value);
            valueText.text = FormatSlider(node, instance, value);
        });

        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            RefreshAction = current =>
            {
                UiKit.ApplyVisibility(current, node, root);
                var value = (float)current.Number(node, "value", slider.minValue);
                if (!Mathf.Approximately(slider.value, value))
                {
                    slider.SetValueWithoutNotify(value);
                }

                valueText.text = FormatSlider(node, current, slider.value);
            }
        };
        instance.Widgets[node.Path] = widget;
        return root;
    }

    private static GameObject BuildSelect(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Select", parent);
        var image = UiKit.Image(root, UiTheme.Raised, UiTheme.Rounded, sliced: true);
        var button = root.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;
        button.colors = ButtonColors(instance, node);
        var element = root.AddComponent<LayoutElement>();
        element.preferredHeight = 32f;
        element.minHeight = 32f;
        element.flexibleHeight = 0f;
        element.preferredWidth = (float)instance.Number(node, "width", 190f);
        element.minWidth = element.preferredWidth;

        var label = UiKit.Text("Label", root.transform, 14f, UiTheme.Text, FontStyles.Normal);
        UiKit.Stretch(label.rectTransform, 10f, 6f);
        label.alignment = TextAlignmentOptions.MidlineLeft;
        var chevron = UiKit.Text("Chevron", root.transform, 12f, UiTheme.Muted, FontStyles.Normal);
        chevron.text = "▾";
        chevron.alignment = TextAlignmentOptions.MidlineRight;
        UiKit.Stretch(chevron.rectTransform, 10f, 6f);

        var select = root.AddComponent<UiSelect>();
        select.Instance = instance;
        select.Node = node;
        select.Label = label;
        select.RootRect = root.GetComponent<RectTransform>();
        button.onClick.AddListener(select.Open);

        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            RefreshAction = current =>
            {
                UiKit.ApplyVisibility(current, node, root);
                select.Refresh();
            }
        };
        instance.Widgets[node.Path] = widget;
        return root;
    }

    private static GameObject BuildTabs(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Tabs", parent);
        var layout = root.AddComponent<VerticalLayoutGroup>();
        UiKit.ApplyLayoutGroup(layout, node, instance);
        UiKit.ApplySize(instance, node, root);

        var bar = UiKit.Rect("TabBar", root.transform);
        var barLayout = bar.AddComponent<HorizontalLayoutGroup>();
        barLayout.spacing = 4f;
        barLayout.childAlignment = TextAnchor.MiddleLeft;
        barLayout.childControlWidth = true;
        barLayout.childControlHeight = true;
        barLayout.childForceExpandWidth = false;
        barLayout.childForceExpandHeight = false;
        var barElement = bar.AddComponent<LayoutElement>();
        barElement.minHeight = 38f;
        barElement.preferredHeight = 38f;
        barElement.flexibleHeight = 0f;

        var content = UiKit.Rect("TabContent", root.transform);
        UiKit.Flex(content, 1f);
        var contentLayout = content.AddComponent<VerticalLayoutGroup>();
        contentLayout.spacing = 8f;
        contentLayout.childControlWidth = true;
        contentLayout.childControlHeight = true;
        contentLayout.childForceExpandWidth = true;
        contentLayout.childForceExpandHeight = false;
        var contentElement = content.GetComponent<LayoutElement>() ?? content.AddComponent<LayoutElement>();
        contentElement.preferredHeight = 0f;
        contentElement.flexibleHeight = 1f;

        var tabs = new List<UiTabsEntry>();
        var activeTab = 0;
        for (var index = 0; index < node.Children.Count; index++)
        {
            var tabNode = node.Children[index];
            if (tabNode.Type != "tab")
            {
                throw new InvalidOperationException($"ui: tabs child must be 'tab', got '{tabNode.Type}'.");
            }

            var tabRoot = UiKit.Rect("TabContent", content.transform);
            UiKit.Stretch(tabRoot.GetComponent<RectTransform>());
            var tabElementRoot = tabRoot.AddComponent<LayoutElement>();
            tabElementRoot.flexibleHeight = 1f;
            var tabLayout = tabRoot.AddComponent<VerticalLayoutGroup>();
            UiKit.ApplyLayoutGroup(tabLayout, tabNode, instance);
            foreach (var child in tabNode.Children)
            {
                BuildNode(instance, child, tabRoot.transform);
            }

            var tabButton = UiKit.Rect("Tab", bar.transform);
            var tabImage = UiKit.Image(tabButton, UiTheme.Transparent(), UiTheme.Rounded, sliced: true);
            var button = tabButton.AddComponent<Button>();
            button.targetGraphic = tabImage;
            button.transition = Selectable.Transition.ColorTint;
            button.colors = ButtonColors(instance, tabNode);
            var tabLabel = UiKit.Text("Label", tabButton.transform, 14f, UiTheme.Muted, FontStyles.Normal);
            UiKit.Stretch(tabLabel.rectTransform, 12f, 4f);
            tabLabel.alignment = TextAlignmentOptions.Center;
            tabLabel.text = InstanceText(instance, tabNode, "label", tabNode.Id ?? "Tab");
            var tabElement = tabButton.AddComponent<LayoutElement>();
            tabElement.preferredHeight = 34f;
            tabElement.minHeight = 34f;
            tabElement.flexibleHeight = 0f;
            tabElement.flexibleWidth = 0f;
            tabElement.preferredWidth = (float)instance.Number(tabNode, "width", 140f);

            var selected = index;
            button.onClick.AddListener(() =>
            {
                if (node.Props.TryGetValue("active", out var active) && active is UiValue)
                {
                    instance.SetProp(node, "active", selected);
                }

                activeTab = selected;
                ApplyTabs(tabs, activeTab);
            });

            tabs.Add(new UiTabsEntry
            {
                Content = tabRoot,
                Button = button,
                Image = tabImage,
                Label = tabLabel
            });
        }

        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            RefreshAction = current =>
            {
                UiKit.ApplyVisibility(current, node, root);
                var active = (int)current.Number(node, "active", activeTab);
                if (active >= 0 && active < tabs.Count)
                {
                    activeTab = active;
                }

                ApplyTabs(tabs, activeTab);
            }
        };
        instance.Widgets[node.Path] = widget;
        ApplyTabs(tabs, activeTab);
        return root;
    }

    private static GameObject BuildSection(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Section", parent);
        var layout = root.AddComponent<VerticalLayoutGroup>();
        UiKit.ApplyLayoutGroup(layout, node, instance);
        UiKit.ApplySize(instance, node, root);

        var title = UiKit.Text("Title", root.transform, 17f, UiTheme.Text, FontStyles.Bold);
        var description = UiKit.Text("Description", root.transform, 13f, UiTheme.Muted, FontStyles.Normal);
        var divider = UiKit.Rect("Divider", root.transform);
        var dividerImage = divider.AddComponent<Image>();
        dividerImage.color = UiTheme.Border;
        dividerImage.raycastTarget = false;
        var dividerElement = divider.AddComponent<LayoutElement>();
        dividerElement.preferredHeight = 1f;

        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            Children = root.transform,
            RefreshAction = current =>
            {
                UiKit.ApplyVisibility(current, node, root);
                title.text = current.Text(node, "title", "");
                description.text = current.Text(node, "description", "");
                description.gameObject.SetActive(description.text.Length > 0);
            }
        };
        instance.Widgets[node.Path] = widget;
        BuildChildren(instance, node, root.transform);
        return root;
    }

    private static GameObject BuildSettingRow(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("SettingRow", parent);
        var layout = root.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 16f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.padding = new RectOffset(0, 0, 4, 4);
        UiKit.ApplySize(instance, node, root);
        var rowElement = root.GetComponent<LayoutElement>() ?? root.AddComponent<LayoutElement>();
        rowElement.preferredHeight = (float)instance.Number(node, "height", 52f);
        rowElement.minHeight = rowElement.preferredHeight;
        rowElement.flexibleHeight = 0f;

        var labelColumn = UiKit.Rect("Label", root.transform);
        var labelLayout = labelColumn.AddComponent<VerticalLayoutGroup>();
        labelLayout.spacing = 2f;
        labelLayout.childControlWidth = true;
        labelLayout.childControlHeight = true;
        labelLayout.childForceExpandWidth = true;
        UiKit.Flex(labelColumn, 1f);

        var label = UiKit.Text("Title", labelColumn.transform, 14f, UiTheme.Text, FontStyles.Normal);
        var hint = UiKit.Text("Hint", labelColumn.transform, 12f, UiTheme.Muted, FontStyles.Normal);

        var control = UiKit.Rect("Control", root.transform);
        var controlLayout = control.AddComponent<HorizontalLayoutGroup>();
        controlLayout.spacing = 8f;
        controlLayout.childAlignment = TextAnchor.MiddleRight;
        controlLayout.childControlWidth = true;
        controlLayout.childControlHeight = true;
        controlLayout.childForceExpandWidth = false;
        controlLayout.childForceExpandHeight = false;
        var controlElement = control.AddComponent<LayoutElement>();
        controlElement.minWidth = 180f;
        controlElement.preferredWidth = 300f;
        controlElement.flexibleWidth = 0f;

        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            Children = control.transform,
            RefreshAction = current =>
            {
                UiKit.ApplyVisibility(current, node, root);
                label.text = current.Text(node, "label", "");
                hint.text = current.Text(node, "hint", "");
                hint.gameObject.SetActive(hint.text.Length > 0);
            }
        };
        instance.Widgets[node.Path] = widget;
        BuildChildren(instance, node, control.transform);
        return root;
    }

    private static GameObject BuildListRow(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("ListRow", parent);
        var layout = root.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 10f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        UiKit.ApplySize(instance, node, root);

        var icon = UiKit.Text("Icon", root.transform, 16f, UiTheme.Accent, FontStyles.Bold);
        icon.text = UiKit.Glyph(instance.Text(node, "icon", "dot"));
        icon.rectTransform.sizeDelta = new Vector2(24f, 24f);

        var primary = UiKit.Text("Name", root.transform, 14f, UiTheme.Text, FontStyles.Normal);
        UiKit.Flex(primary.gameObject, 1f);
        var secondary = UiKit.Text("Secondary", root.transform, 12f, UiTheme.Muted, FontStyles.Normal);
        secondary.alignment = TextAlignmentOptions.MidlineRight;

        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            Children = root.transform,
            RefreshAction = current =>
            {
                UiKit.ApplyVisibility(current, node, root);
                primary.text = current.Text(node, "label", "");
                secondary.text = current.Text(node, "secondary", "");
                secondary.gameObject.SetActive(secondary.text.Length > 0);
            }
        };
        instance.Widgets[node.Path] = widget;
        BuildChildren(instance, node, root.transform);
        return root;
    }

    private static GameObject BuildFooter(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Footer", parent);
        var layout = root.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.MiddleRight;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        var element = root.AddComponent<LayoutElement>();
        element.preferredHeight = 38f;
        element.minHeight = 38f;
        element.flexibleHeight = 0f;
        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            Children = root.transform,
            RefreshAction = current => UiKit.ApplyVisibility(current, node, root)
        };
        instance.Widgets[node.Path] = widget;
        BuildChildren(instance, node, root.transform);
        return root;
    }

    private static GameObject BuildTab(UiInstance instance, UiPanel node, Transform parent)
    {
        var root = UiKit.Rect("Tab", parent);
        var layout = root.AddComponent<VerticalLayoutGroup>();
        UiKit.ApplyLayoutGroup(layout, node, instance);
        var widget = new UiWidget
        {
            Node = node,
            Root = root,
            Rect = root.GetComponent<RectTransform>(),
            Children = root.transform,
            RefreshAction = current => UiKit.ApplyVisibility(current, node, root)
        };
        instance.Widgets[node.Path] = widget;
        BuildChildren(instance, node, root.transform);
        return root;
    }

    private static void ApplyWindowPlacement(UiInstance instance, RectTransform rect)
    {
        var node = instance.Document.Root;
        var width = (float)instance.Number(node, "width", 760f);
        var height = (float)instance.Number(node, "height", 560f);
        rect.sizeDelta = new Vector2(width, height);
        var anchor = instance.Text(node, "anchor", "center");
        var x = (float)instance.Number(node, "x", 0f);
        var y = (float)instance.Number(node, "y", 0f);
        switch (anchor)
        {
            case "top-left":
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(x, -y);
                break;
            case "top-right":
                rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(1f, 1f);
                rect.anchoredPosition = new Vector2(-x, -y);
                break;
            case "left":
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
                rect.pivot = new Vector2(0f, 0.5f);
                rect.anchoredPosition = new Vector2(x, y);
                break;
            case "right":
                rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
                rect.pivot = new Vector2(1f, 0.5f);
                rect.anchoredPosition = new Vector2(-x, y);
                break;
            default:
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = new Vector2(x, y);
                break;
        }
    }

    private static void ApplyTabs(List<UiTabsEntry> tabs, int activeIndex)
    {
        for (var index = 0; index < tabs.Count; index++)
        {
            var entry = tabs[index];
            var active = index == activeIndex;
            entry.Content.SetActive(active);
            entry.Image.color = active ? UiTheme.Raised : UiTheme.Transparent();
            entry.Label.color = active ? UiTheme.Text : UiTheme.Muted;
        }
    }

    private static void ApplyToggleVisual(Toggle toggle, RectTransform knob, Image background, bool value)
    {
        background.color = value ? UiTheme.Accent : UiTheme.Active;
        knob.anchoredPosition = new Vector2(value ? 21f : 3f, 0f);
    }

    private static string FormatSlider(UiPanel node, UiInstance instance, float value)
    {
        var format = instance.Text(node, "format", "0.##");
        return value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Color ButtonColor(UiInstance instance, UiPanel node)
    {
        return instance.Text(node, "variant", "secondary") switch
        {
            "primary" => UiTheme.Accent,
            "danger" => UiTheme.Danger,
            "ghost" => UiTheme.Transparent(),
            _ => UiTheme.Raised
        };
    }

    private static Color ButtonText(UiInstance instance, UiPanel node) =>
        instance.Text(node, "variant", "secondary") == "primary" ? UiTheme.Window : UiTheme.Text;

    private static ColorBlock ButtonColors(UiInstance instance, UiPanel node)
    {
        var normal = ButtonColor(instance, node);
        var block = ColorBlock.defaultColorBlock;
        block.normalColor = normal;
        block.highlightedColor = Color.Lerp(normal, Color.white, 0.08f);
        block.pressedColor = Color.Lerp(normal, Color.black, 0.12f);
        block.selectedColor = normal;
        block.disabledColor = new Color(normal.r, normal.g, normal.b, 0.4f);
        block.colorMultiplier = 1f;
        block.fadeDuration = 0.08f;
        return block;
    }

    private sealed class UiTabsEntry
    {
        internal GameObject Content = null!;
        internal Button Button = null!;
        internal Image Image = null!;
        internal TextMeshProUGUI Label = null!;
    }

    private static string InstanceText(UiInstance instance, UiPanel node, string name, string fallback) =>
        instance.Text(node, name, fallback);
}

/// <summary>Non-interactive window drag handle.</summary>
internal sealed class WindowDrag : MonoBehaviour, IBeginDragHandler, IDragHandler
{
    internal RectTransform Target = null!;
    private Vector2 _offset;

    public void OnBeginDrag(PointerEventData eventData)
    {
        RectTransformUtility.ScreenPointToLocalPointInRectangle(Target, eventData.position, eventData.pressEventCamera, out _offset);
        _offset = (Vector2)Target.localPosition - _offset;
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(Target.parent as RectTransform, eventData.position,
                eventData.pressEventCamera, out var point))
        {
            Target.localPosition = point + _offset;
        }
    }
}
#endif

#if USE_GUI
/// <summary>Tracks text-field focus so host input blocking is released on destroy/reload.</summary>
internal sealed class UiTextFocus : MonoBehaviour
{
    private bool _focused;

    internal void Set(bool focused)
    {
        if (_focused == focused)
        {
            return;
        }

        _focused = focused;
        if (focused)
        {
            UiHost.AddTextFocus();
        }
        else
        {
            UiHost.RemoveTextFocus();
        }
    }

    private void OnDisable() => Set(false);

    private void OnDestroy() => Set(false);
}
#endif
