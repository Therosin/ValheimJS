using System.Collections.Generic;
using UnityEngine;
using ValheimJS.Core;

namespace ValheimJS.Debugging;

/// <summary>
/// World-space line/mesh drawing API used by gameplay tooling (markers, paths, nav overlays).
/// Primitives are queued for a few frames and rendered by a hidden GL renderer with depth testing.
/// </summary>
internal static class WorldDraw
{
    internal enum Primitive : byte
    {
        Line,
        Triangle
    }

    internal struct Item
    {
        internal Primitive Primitive;
        internal Vector3 A;
        internal Vector3 B;
        internal Vector3 C;
        internal Color Color;
        internal float Expires;
    }

    private static readonly List<Item> Items = new();
    private static WorldDrawRenderer? _renderer;

    internal static bool Enabled { get; set; } = true;

    internal static void Line(Vector3 a, Vector3 b, Color color, float duration = 0.1f)
    {
        if (!Enabled)
        {
            return;
        }

        EnsureRenderer();
        Items.Add(new Item
        {
            Primitive = Primitive.Line,
            A = a,
            B = b,
            Color = color,
            Expires = Time.unscaledTime + Mathf.Max(0.01f, duration)
        });
    }

    internal static void Polyline(IReadOnlyList<Vector3> points, Color color, float duration = 0.1f)
    {
        for (var index = 1; index < points.Count; index++)
        {
            Line(points[index - 1], points[index], color, duration);
        }
    }

    internal static void Triangle(Vector3 a, Vector3 b, Vector3 c, Color color, float duration = 0.1f)
    {
        if (!Enabled)
        {
            return;
        }

        EnsureRenderer();
        Items.Add(new Item
        {
            Primitive = Primitive.Triangle,
            A = a,
            B = b,
            C = c,
            Color = color,
            Expires = Time.unscaledTime + Mathf.Max(0.01f, duration)
        });
    }

    internal static void Box(Vector3 center, Vector3 size, Color color, float duration = 0.1f)
    {
        var half = size * 0.5f;
        var a = center + new Vector3(-half.x, -half.y, -half.z);
        var b = center + new Vector3(half.x, -half.y, -half.z);
        var c = center + new Vector3(half.x, -half.y, half.z);
        var d = center + new Vector3(-half.x, -half.y, half.z);
        var e = a + Vector3.up * size.y;
        var f = b + Vector3.up * size.y;
        var g = c + Vector3.up * size.y;
        var h = d + Vector3.up * size.y;

        Line(a, b, color, duration);
        Line(b, c, color, duration);
        Line(c, d, color, duration);
        Line(d, a, color, duration);
        Line(e, f, color, duration);
        Line(f, g, color, duration);
        Line(g, h, color, duration);
        Line(h, e, color, duration);
        Line(a, e, color, duration);
        Line(b, f, color, duration);
        Line(c, g, color, duration);
        Line(d, h, color, duration);
    }

    internal static void Circle(Vector3 center, float radius, Color color, int segments = 24, float duration = 0.1f)
    {
        var previous = center + new Vector3(radius, 0f, 0f);
        for (var index = 1; index <= segments; index++)
        {
            var angle = Mathf.PI * 2f * index / segments;
            var next = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Line(previous, next, color, duration);
            previous = next;
        }
    }

    internal static void Mesh(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, Color color, bool filled, float duration = 0.1f)
    {
        if (indices.Count < 3)
        {
            return;
        }

        for (var index = 0; index + 2 < indices.Count; index += 3)
        {
            var a = indices[index];
            var b = indices[index + 1];
            var c = indices[index + 2];
            if (a < 0 || b < 0 || c < 0 || a >= vertices.Count || b >= vertices.Count || c >= vertices.Count)
            {
                continue;
            }

            if (filled)
            {
                Triangle(vertices[a], vertices[b], vertices[c], color, duration);
            }
            else
            {
                Line(vertices[a], vertices[b], color, duration);
                Line(vertices[b], vertices[c], color, duration);
                Line(vertices[c], vertices[a], color, duration);
            }
        }
    }

    internal static void Clear() => Items.Clear();

    internal static IReadOnlyList<Item> Pending => Items;

    internal static void RemoveAt(int index) => Items.RemoveAt(index);

    private static void EnsureRenderer()
    {
        if (_renderer != null)
        {
            return;
        }

        var parent = ValheimJsHost.WorldRoot;
        var existing = parent.Find("WorldDraw");
        if (existing != null)
        {
            _renderer = existing.GetComponent<WorldDrawRenderer>() ?? existing.gameObject.AddComponent<WorldDrawRenderer>();
            return;
        }

        var go = new GameObject("WorldDraw");
        go.transform.SetParent(parent, false);
        _renderer = go.AddComponent<WorldDrawRenderer>();
    }
}

/// <summary>Renders queued <see cref="WorldDraw"/> primitives; survives scene changes.</summary>
internal sealed class WorldDrawRenderer : MonoBehaviour
{
    private Material? _material;

    private void OnRenderObject()
    {
        if (!WorldDraw.Enabled || WorldDraw.Pending.Count == 0)
        {
            return;
        }

        if (_material == null)
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                return;
            }

            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            // World-space overlay: obey depth and never write into it, so it cannot make
            // terrain/characters look transparent or clip them.
            _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            _material.SetInt("_ZWrite", 0);
            _material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        }

        var now = Time.unscaledTime;
        for (var index = WorldDraw.Pending.Count - 1; index >= 0; index--)
        {
            if (WorldDraw.Pending[index].Expires < now)
            {
                WorldDraw.RemoveAt(index);
            }
        }

        if (WorldDraw.Pending.Count == 0)
        {
            return;
        }

        _material.SetPass(0);
        GL.PushMatrix();
        Render(WorldDraw.Primitive.Line, GL.LINES);
        Render(WorldDraw.Primitive.Triangle, GL.TRIANGLES);
        GL.PopMatrix();
    }

    private static void Render(WorldDraw.Primitive primitive, int mode)
    {
        GL.Begin(mode);
        foreach (var item in WorldDraw.Pending)
        {
            if (item.Primitive != primitive)
            {
                continue;
            }

            GL.Color(item.Color);
            GL.Vertex3(item.A.x, item.A.y, item.A.z);
            GL.Vertex3(item.B.x, item.B.y, item.B.z);
            if (primitive == WorldDraw.Primitive.Triangle)
            {
                GL.Vertex3(item.C.x, item.C.y, item.C.z);
            }
        }

        GL.End();
    }

    private void OnDestroy() => _material = null;
}
