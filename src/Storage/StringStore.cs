using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ValheimJS.Utils;

namespace ValheimJS.Storage;

/// <summary>
/// Backing format for <c>ctx.host.storage</c>. Values are base64-encoded in a JSON object so
/// tabs, newlines, control characters and non-ASCII text survive a round trip. A legacy
/// tab-separated file is read once and rewritten in the current format on the next set.
/// </summary>
internal static class StringStore
{
    internal static Dictionary<string, string> Read(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var text = File.ReadAllText(path);
        return text.TrimStart().StartsWith("{", StringComparison.Ordinal)
            ? ParseJson(text)
            : ParseLegacy(text);
    }

    internal static void Write(string path, IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder();
        builder.Append('{');
        var first = true;
        foreach (var pair in values.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(Json.Quote(Encode(pair.Key)))
                .Append(':')
                .Append(Json.Quote(Encode(pair.Value)));
        }

        builder.Append('}');

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, builder.ToString(), new UTF8Encoding(false));
        try
        {
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        catch (PlatformNotSupportedException)
        {
            File.Copy(temporary, path, overwrite: true);
            File.Delete(temporary);
        }
    }

    private static Dictionary<string, string> ParseJson(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var cursor = 0;
        SkipWhitespace(text, ref cursor);
        Expect(text, ref cursor, '{');
        SkipWhitespace(text, ref cursor);
        if (Peek(text, cursor) == '}')
        {
            cursor++;
            SkipWhitespace(text, ref cursor);
            RequireEnd(text, cursor);
            return values;
        }

        while (true)
        {
            var key = Decode(ParseString(text, ref cursor));
            SkipWhitespace(text, ref cursor);
            Expect(text, ref cursor, ':');
            SkipWhitespace(text, ref cursor);
            var value = Decode(ParseString(text, ref cursor));
            values[key] = value;

            SkipWhitespace(text, ref cursor);
            if (Peek(text, cursor) == ',')
            {
                cursor++;
                SkipWhitespace(text, ref cursor);
                continue;
            }

            Expect(text, ref cursor, '}');
            break;
        }

        SkipWhitespace(text, ref cursor);
        RequireEnd(text, cursor);
        return values;
    }

    private static Dictionary<string, string> ParseLegacy(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('\t');
            if (separator <= 0)
            {
                continue;
            }

            values[line.Substring(0, separator)] = line.Substring(separator + 1);
        }

        return values;
    }

    private static string ParseString(string text, ref int cursor)
    {
        Expect(text, ref cursor, '"');
        var builder = new StringBuilder();
        while (cursor < text.Length)
        {
            var character = text[cursor++];
            if (character == '"')
            {
                return builder.ToString();
            }

            if (character != '\\')
            {
                builder.Append(character);
                continue;
            }

            if (cursor >= text.Length)
            {
                break;
            }

            var escape = text[cursor++];
            switch (escape)
            {
                case '"': builder.Append('"'); break;
                case '\\': builder.Append('\\'); break;
                case '/': builder.Append('/'); break;
                case 'b': builder.Append('\b'); break;
                case 'f': builder.Append('\f'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'u':
                    if (cursor + 4 > text.Length)
                    {
                        throw new FormatException("storage string has a truncated unicode escape");
                    }

                    builder.Append((char)Convert.ToUInt16(text.Substring(cursor, 4), 16));
                    cursor += 4;
                    break;
                default:
                    throw new FormatException("storage string has an invalid escape");
            }
        }

        throw new FormatException("storage string is not terminated");
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException exception)
        {
            throw new FormatException("storage value is not valid base64", exception);
        }
    }

    private static void SkipWhitespace(string text, ref int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }
    }

    private static char Peek(string text, int cursor) => cursor < text.Length ? text[cursor] : '\0';

    private static void Expect(string text, ref int cursor, char expected)
    {
        if (Peek(text, cursor) != expected)
        {
            throw new FormatException($"storage expected '{expected}' at offset {cursor}");
        }

        cursor++;
    }

    private static void RequireEnd(string text, int cursor)
    {
        if (cursor != text.Length)
        {
            throw new FormatException($"storage has trailing content at offset {cursor}");
        }
    }
}
