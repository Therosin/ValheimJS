using System;
using System.Collections.Generic;
using System.IO;
using Acornima;

namespace ValheimJS.Scripting.Binding;

/// <summary>
/// Minimal Source Map v3 decoder for plugin bundle positions. Only generated line/column to
/// original source/line/column is needed; names and sourcesContent are ignored.
/// </summary>
internal sealed class SourceMap
{
    private const string Base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private readonly string[] _sources;
    private readonly List<Segment>[] _lines;

    internal SourceMap(IReadOnlyList<string> sources, string mappings, string mapDirectory)
    {
        _sources = new string[sources.Count];
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index] ?? string.Empty;
            _sources[index] = source.Length == 0
                ? source
                : Path.GetFullPath(Path.Combine(mapDirectory, source));
        }

        _lines = Decode(mappings);
    }

    internal SourceLocation Map(SourceLocation location)
    {
        var generatedLine = location.Start.Line;
        var generatedColumn = location.Start.Column;
        if (generatedLine <= 0 || generatedLine > _lines.Length)
        {
            return location;
        }

        var segments = _lines[generatedLine - 1];
        Segment? best = null;
        foreach (var segment in segments)
        {
            if (segment.GeneratedColumn > generatedColumn)
            {
                break;
            }

            best = segment;
        }

        if (best is not { } mapped || mapped.SourceIndex < 0 || mapped.SourceIndex >= _sources.Length)
        {
            return location;
        }

        var start = Position.From(mapped.OriginalLine + 1, mapped.OriginalColumn);
        return SourceLocation.From(start, start, _sources[mapped.SourceIndex]);
    }

    private static List<Segment>[] Decode(string mappings)
    {
        var lines = mappings.Split(';');
        var result = new List<Segment>[lines.Length];
        var sourceIndex = 0;
        var originalLine = 0;
        var originalColumn = 0;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var segments = new List<Segment>();
            result[lineIndex] = segments;
            var text = lines[lineIndex];
            if (text.Length == 0)
            {
                continue;
            }

            var generatedColumn = 0;
            foreach (var segmentText in text.Split(','))
            {
                if (segmentText.Length == 0)
                {
                    continue;
                }

                var cursor = 0;
                generatedColumn += DecodeValue(segmentText, ref cursor);
                if (cursor >= segmentText.Length)
                {
                    continue;
                }

                sourceIndex += DecodeValue(segmentText, ref cursor);
                originalLine += DecodeValue(segmentText, ref cursor);
                originalColumn += DecodeValue(segmentText, ref cursor);
                if (cursor < segmentText.Length)
                {
                    DecodeValue(segmentText, ref cursor); // name index
                }

                segments.Add(new Segment(generatedColumn, sourceIndex, originalLine, originalColumn));
            }
        }

        return result;
    }

    private static int DecodeValue(string text, ref int cursor)
    {
        var value = 0;
        var shift = 0;
        int digit;
        do
        {
            if (cursor >= text.Length)
            {
                throw new FormatException("source map segment ended early");
            }

            digit = Base64.IndexOf(text[cursor++]);
            if (digit < 0)
            {
                throw new FormatException("source map segment contains an invalid base64 character");
            }

            value += (digit & 31) << shift;
            shift += 5;
        } while ((digit & 32) != 0);

        var negative = (value & 1) != 0;
        value >>= 1;
        return negative ? -value : value;
    }

    private readonly struct Segment
    {
        internal Segment(int generatedColumn, int sourceIndex, int originalLine, int originalColumn)
        {
            GeneratedColumn = generatedColumn;
            SourceIndex = sourceIndex;
            OriginalLine = originalLine;
            OriginalColumn = originalColumn;
        }

        internal int GeneratedColumn { get; }
        internal int SourceIndex { get; }
        internal int OriginalLine { get; }
        internal int OriginalColumn { get; }
    }
}

/// <summary>Source maps registered by the runtime, keyed by the generated bundle path.</summary>
internal static class SourceMapRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SourceMap> Maps =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void Register(string generatedPath, SourceMap map) => Maps[generatedPath] = map;

    internal static SourceLocation Map(SourceLocation location)
    {
        if (string.IsNullOrEmpty(location.SourceFile))
        {
            return location;
        }

        return Maps.TryGetValue(location.SourceFile, out var map) ? map.Map(location) : location;
    }

    internal static void Clear() => Maps.Clear();
}
