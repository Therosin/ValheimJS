using System;
using System.Runtime.Serialization;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using UnityEngine;
using ValheimJS.Core;
using ValheimJS.Debugging;
using ValheimJS.Networking;
using ValheimJS.Scripting.Binding;
using ValheimJS.Scripting.Types;

namespace ValheimJS.Bindings;

/// <summary>World-space drawing for gameplay UI, markers and visual debugging.</summary>
[JsBinder("draw", Phase = JsBindPhase.Gameplay, Order = 144,
    Description = "Draw world-space lines and meshes; server calls replicate to clients.")]
internal sealed class DrawBindings : JsBindings
{
    private readonly NetworkScope _scope;
    private readonly NetworkChannel _channel;

    public DrawBindings(JsBindingContext context) : base(context)
    {
        _scope = context.Networking.OpenScope();
        _channel = _scope.Channel("valheimjs:draw", "draw");
        _channel.On<DrawMessage>("draw", (_, message) =>
        {
            if (GameEnvironment.IsServer() || message == null)
            {
                return;
            }

            context.Networking.Post(() => Apply(message));
        });
    }

    [JsBinding(Scoped = true, Description = "Draws one line between two world points.")]
    public void Line(string plugin, float ax, float ay, float az, float bx, float by, float bz,
        [JsNullable] string? color, double duration)
    {
        Submit(new DrawMessage
        {
            Op = "line",
            Numbers = new[] { ax, ay, az, bx, by, bz },
            Color = color ?? "#ffffff",
            Duration = (float)duration
        });
    }

    [JsBinding(Scoped = true, Description = "Draws a connected world-space line strip.")]
    public void Polyline(string plugin, JsValue points, [JsNullable] string? color, double duration)
    {
        Submit(new DrawMessage
        {
            Op = "polyline",
            Points = Flatten(ParsePoints(points)),
            Color = color ?? "#ffffff",
            Duration = (float)duration
        });
    }

    [JsBinding(Scoped = true, Description = "Draws an axis-aligned wire box.")]
    public void Box(string plugin, float x, float y, float z, float sx, float sy, float sz,
        [JsNullable] string? color, double duration)
    {
        Submit(new DrawMessage
        {
            Op = "box",
            Numbers = new[] { x, y, z, sx, sy, sz },
            Color = color ?? "#ffffff",
            Duration = (float)duration
        });
    }

    [JsBinding(Scoped = true, Description = "Draws a horizontal circle.")]
    public void Circle(string plugin, float x, float y, float z, float radius,
        [JsNullable] string? color, double duration, int segments)
    {
        Submit(new DrawMessage
        {
            Op = "circle",
            Numbers = new[] { x, y, z, radius, segments },
            Color = color ?? "#ffffff",
            Duration = (float)duration
        });
    }

    [JsBinding(Scoped = true, Description = "Draws a triangle mesh (wireframe or filled).")]
    public void Mesh(string plugin, JsValue vertices, JsValue indices,
        [JsNullable] string? color, bool filled, double duration)
    {
        Submit(new DrawMessage
        {
            Op = "mesh",
            Points = Flatten(ParsePoints(vertices)),
            Indices = ParseIndices(indices).ToArray(),
            Color = color ?? "#ffffff",
            Duration = (float)duration,
            Filled = filled
        });
    }

    [JsBinding(Scoped = true, Description = "Clears queued world-space primitives.")]
    public void Clear(string plugin)
    {
        if (GameEnvironment.IsServer())
        {
            if (Context.Networking.Available)
            {
                _channel.Emit(Context.Networking.Everybody, "draw", new DrawMessage { Op = "clear" });
            }

            if (Player.m_localPlayer != null)
            {
                WorldDraw.Clear();
            }

            return;
        }

        WorldDraw.Clear();
    }

    public override void Dispose() => _scope.Dispose();

    private void Submit(DrawMessage message)
    {
        if (GameEnvironment.IsServer())
        {
            if (Context.Networking.Available)
            {
                _channel.Emit(Context.Networking.Everybody, "draw", message);
            }

            if (Player.m_localPlayer != null)
            {
                Apply(message);
            }

            return;
        }

        Apply(message);
    }

    private static void Apply(DrawMessage message)
    {
        var color = Parse(message.Color);
        var duration = message.Duration <= 0f ? 0.1f : message.Duration;
        switch (message.Op)
        {
            case "line" when message.Numbers != null && message.Numbers.Length >= 6:
                WorldDraw.Line(
                    new Vector3(message.Numbers[0], message.Numbers[1], message.Numbers[2]),
                    new Vector3(message.Numbers[3], message.Numbers[4], message.Numbers[5]), color, duration);
                break;
            case "polyline":
                var points = Expand(message.Points);
                for (var index = 1; index < points.Count; index++)
                {
                    WorldDraw.Line(points[index - 1], points[index], color, duration);
                }

                break;
            case "box" when message.Numbers != null && message.Numbers.Length >= 6:
                WorldDraw.Box(new Vector3(message.Numbers[0], message.Numbers[1], message.Numbers[2]),
                    new Vector3(message.Numbers[3], message.Numbers[4], message.Numbers[5]), color, duration);
                break;
            case "circle" when message.Numbers != null && message.Numbers.Length >= 5:
                WorldDraw.Circle(new Vector3(message.Numbers[0], message.Numbers[1], message.Numbers[2]),
                    message.Numbers[3], color, (int)message.Numbers[4], duration);
                break;
            case "mesh" when message.Points != null && message.Indices != null:
                WorldDraw.Mesh(Expand(message.Points), message.Indices, color, message.Filled, duration);
                break;
            case "clear":
                WorldDraw.Clear();
                break;
        }
    }

    private static float[] Flatten(System.Collections.Generic.List<Vector3> points)
    {
        var result = new float[points.Count * 3];
        for (var index = 0; index < points.Count; index++)
        {
            result[index * 3] = points[index].x;
            result[index * 3 + 1] = points[index].y;
            result[index * 3 + 2] = points[index].z;
        }

        return result;
    }

    private static System.Collections.Generic.List<Vector3> Expand(float[] values)
    {
        var result = new System.Collections.Generic.List<Vector3>();
        for (var index = 0; index + 2 < values.Length; index += 3)
        {
            result.Add(new Vector3(values[index], values[index + 1], values[index + 2]));
        }

        return result;
    }

    private static Color Parse(string? color)
    {
        return !string.IsNullOrEmpty(color) && ColorUtility.TryParseHtmlString(color, out var parsed)
            ? parsed
            : Color.white;
    }

    private static System.Collections.Generic.List<Vector3> ParsePoints(JsValue value)
    {
        var result = new System.Collections.Generic.List<Vector3>();
        if (value == JsValue.Null || value == JsValue.Undefined || value is not JsArray array)
        {
            return result;
        }
        for (var index = 0; index < array.Length; index++)
        {
            var item = array.Get(index);
            if (item.IsNull() || item.IsUndefined())
            {
                continue;
            }

            var point = item.AsObject();
            result.Add(new Vector3(
                (float)point.Get("x").AsNumber(),
                (float)point.Get("y").AsNumber(),
                (float)point.Get("z").AsNumber()));
        }

        return result;
    }

    private static System.Collections.Generic.List<int> ParseIndices(JsValue value)
    {
        var result = new System.Collections.Generic.List<int>();
        if (value == JsValue.Null || value == JsValue.Undefined || value is not JsArray array)
        {
            return result;
        }
        for (var index = 0; index < array.Length; index++)
        {
            result.Add((int)array.Get(index).AsNumber());
        }

        return result;
    }
}

[DataContract]
internal sealed class DrawMessage
{
    [DataMember(Name = "op")] public string Op = "";
    [DataMember(Name = "numbers")] public float[] Numbers = Array.Empty<float>();
    [DataMember(Name = "points")] public float[] Points = Array.Empty<float>();
    [DataMember(Name = "indices")] public int[] Indices = Array.Empty<int>();
    [DataMember(Name = "color")] public string Color = "#ffffff";
    [DataMember(Name = "duration")] public float Duration = 0.1f;
    [DataMember(Name = "filled")] public bool Filled;
}
