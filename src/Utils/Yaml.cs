using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimJS.Utils;

/// <summary>Reports a malformed document. Line and column are 1-based.</summary>
public sealed class YamlException : Exception
{
    public YamlException(string message, string? source, int line, int column)
        : base(Format(message, source, line, column))
    {
        Source = source;
        Line = line;
        Column = column;
    }

    /// <summary>Source name supplied to <see cref="Yaml.Parse"/>, when any.</summary>
    public new string? Source { get; }

    public int Line { get; }

    public int Column { get; }

    private static string Format(string message, string? source, int line, int column)
    {
        var location = source == null ? $"line {line}, column {column}" : $"{source}:{line}:{column}";
        return $"{location}: {message}";
    }
}

/// <summary>
/// Insertion-ordered mapping produced by <see cref="Yaml.Parse"/>. Order matters for authoring
/// order (imports, state defaults) and .NET dictionaries do not contract to preserve it.
/// </summary>
public sealed class YamlMap : IReadOnlyDictionary<string, object?>
{
    private readonly List<KeyValuePair<string, object?>> _entries = new();
    private readonly Dictionary<string, int> _positions = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public IEnumerable<string> Keys
    {
        get
        {
            foreach (var entry in _entries)
            {
                yield return entry.Key;
            }
        }
    }

    public IEnumerable<object?> Values
    {
        get
        {
            foreach (var entry in _entries)
            {
                yield return entry.Value;
            }
        }
    }

    public object? this[string key]
    {
        get
        {
            if (!_positions.TryGetValue(key, out var position))
            {
                throw new KeyNotFoundException($"YAML map has no key '{key}'.");
            }

            return _entries[position].Value;
        }
    }

    public bool ContainsKey(string key) => _positions.ContainsKey(key);

    public bool TryGetValue(string key, out object? value)
    {
        if (_positions.TryGetValue(key, out var position))
        {
            value = _entries[position].Value;
            return true;
        }

        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Add(string key, object? value)
    {
        _positions.Add(key, _entries.Count);
        _entries.Add(new KeyValuePair<string, object?>(key, value));
    }
}

/// <summary>
/// Parser for the YAML subset used by authored data files: block mappings and sequences (including
/// same-indent sequences), flow collections, quoted and plain scalars, block scalars (<c>|</c>,
/// <c>&gt;</c> with chomping indicators), comments and one document per file.
/// <para>
/// Anchors, aliases, tags, multiple documents, complex keys, multi-line quoted scalars and tab
/// indentation are not interpreted. Output values are <see cref="YamlMap"/>,
/// <see cref="List{T}"/> of <see cref="object"/>, <see cref="string"/>, <see cref="long"/>,
/// <see cref="double"/>, <see cref="bool"/> or <c>null</c>.
/// </para>
/// </summary>
public static class Yaml
{
    public static object? Parse(string text, string? source = null)
    {
        if (text == null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        return new Parser(text, source).ParseDocument();
    }

    private sealed class Line
    {
        internal string Raw = string.Empty;
        internal string Text = string.Empty;
        internal int Indent;
        internal bool Blank;
        internal bool Comment;
        internal int Number;
    }

    private sealed class Parser
    {
        private readonly Line[] _lines;
        private readonly string? _source;
        private int _index;

        internal Parser(string text, string? source)
        {
            _source = source;
            var raw = text.Split('\n');
            _lines = new Line[raw.Length];
            for (var i = 0; i < raw.Length; i++)
            {
                _lines[i] = BuildLine(raw[i], i + 1);
            }
        }

        internal object? ParseDocument()
        {
            var line = Peek();
            if (line == null)
            {
                return null;
            }

            if (line.Text == "---")
            {
                Advance();
                line = Peek();
                if (line == null)
                {
                    return null;
                }
            }
            else if (line.Text == "...")
            {
                return null;
            }

            var value = ParseBlock(line.Indent);

            var trailing = Peek();
            if (trailing != null && trailing.Text == "...")
            {
                Advance();
                trailing = Peek();
            }

            if (trailing != null)
            {
                throw Error("unexpected content; the document must hold a single root value", trailing.Number, trailing.Indent + 1);
            }

            return value;
        }

        private Line BuildLine(string raw, int number)
        {
            if (number == 1 && raw.Length > 0 && raw[0] == '\uFEFF')
            {
                raw = raw.Substring(1);
            }

            if (raw.Length > 0 && raw[raw.Length - 1] == '\r')
            {
                raw = raw.Substring(0, raw.Length - 1);
            }

            var indent = 0;
            while (indent < raw.Length && raw[indent] == ' ')
            {
                indent++;
            }

            if (indent < raw.Length && raw[indent] == '\t')
            {
                var whitespaceOnly = true;
                for (var i = indent; i < raw.Length; i++)
                {
                    if (!char.IsWhiteSpace(raw[i]))
                    {
                        whitespaceOnly = false;
                        break;
                    }
                }

                if (!whitespaceOnly)
                {
                    throw new YamlException("tabs are not allowed for indentation", _source, number, indent + 1);
                }
            }

            var text = raw.Substring(indent).TrimEnd();
            return new Line
            {
                Raw = raw,
                Text = text,
                Indent = indent,
                Blank = text.Length == 0,
                Comment = text.Length > 0 && text[0] == '#',
                Number = number
            };
        }

        private void Advance() => _index++;

        private Line? Peek()
        {
            while (_index < _lines.Length && (_lines[_index].Blank || _lines[_index].Comment))
            {
                _index++;
            }

            return _index < _lines.Length ? _lines[_index] : null;
        }

        private YamlException Error(string message, int line, int column) => new(message, _source, line, column);

        private object? ParseBlock(int indent)
        {
            var line = Peek();
            if (line == null)
            {
                throw Error("unexpected end of document; expected a value", _lines.Length, 1);
            }

            if (line.Indent != indent)
            {
                throw Error($"expected indentation {indent}", line.Number, line.Indent + 1);
            }

            return IsSequenceItem(line.Text) ? ParseSequence(indent) : ParseMapping(indent);
        }

        private YamlMap ParseMapping(int indent)
        {
            var map = new YamlMap();
            while (true)
            {
                var line = Peek();
                if (line == null || line.Indent != indent || IsSequenceItem(line.Text))
                {
                    break;
                }

                Advance();
                AddEntry(map, line.Text, indent, line.Number);
            }

            return map;
        }

        private void AddEntry(YamlMap map, string text, int indent, int lineNumber)
        {
            if (!TrySplitKey(text, out var key, out var tail))
            {
                throw Error("expected 'key: value'", lineNumber, indent + 1);
            }

            if (map.ContainsKey(key))
            {
                throw Error($"duplicate key '{key}'", lineNumber, indent + 1);
            }

            map.Add(key, ParseValue(tail, indent, lineNumber));
        }

        private object? ParseValue(string rest, int indent, int lineNumber)
        {
            if (rest.Length > 0)
            {
                return ParseInline(rest, indent, lineNumber);
            }

            var next = Peek();
            if (next == null)
            {
                return null;
            }

            if (next.Indent > indent)
            {
                return ParseBlock(next.Indent);
            }

            // YAML permits a block sequence at the same indentation as its key.
            return next.Indent == indent && IsSequenceItem(next.Text) ? ParseSequence(indent) : null;
        }

        private List<object?> ParseSequence(int indent)
        {
            var list = new List<object?>();
            while (true)
            {
                var line = Peek();
                if (line == null || line.Indent != indent || !IsSequenceItem(line.Text))
                {
                    break;
                }

                Advance();
                list.Add(ParseSequenceValue(line.Text.Substring(1), indent, line.Number));
            }

            return list;
        }

        /// <summary>Parses the content after a sequence dash; <paramref name="afterDash"/> keeps leading spaces.</summary>
        private object? ParseSequenceValue(string afterDash, int dashIndent, int lineNumber)
        {
            var spaces = 0;
            while (spaces < afterDash.Length && afterDash[spaces] == ' ')
            {
                spaces++;
            }

            var text = afterDash.Substring(spaces);
            var contentIndent = dashIndent + 1 + spaces;

            if (text.Length == 0)
            {
                var next = Peek();
                return next != null && next.Indent > dashIndent ? ParseBlock(next.Indent) : null;
            }

            if (IsSequenceItem(text))
            {
                var nested = new List<object?> { ParseSequenceValue(text.Substring(1), contentIndent, lineNumber) };
                while (true)
                {
                    var line = Peek();
                    if (line == null || line.Indent != contentIndent || !IsSequenceItem(line.Text))
                    {
                        break;
                    }

                    Advance();
                    nested.Add(ParseSequenceValue(line.Text.Substring(1), contentIndent, line.Number));
                }

                return nested;
            }

            if (TrySplitKey(text, out _, out _))
            {
                return ParseItemMapping(text, contentIndent, lineNumber);
            }

            return ParseInline(text, dashIndent, lineNumber);
        }

        private YamlMap ParseItemMapping(string firstEntry, int indent, int lineNumber)
        {
            var map = new YamlMap();
            AddEntry(map, firstEntry, indent, lineNumber);

            while (true)
            {
                var line = Peek();
                if (line == null || line.Indent < indent)
                {
                    break;
                }

                if (line.Indent > indent)
                {
                    throw Error("unexpected indentation inside a sequence item", line.Number, line.Indent + 1);
                }

                if (IsSequenceItem(line.Text))
                {
                    break;
                }

                Advance();
                AddEntry(map, line.Text, indent, line.Number);
            }

            return map;
        }

        private object? ParseInline(string text, int ownerIndent, int lineNumber)
        {
            if (text.Length == 0)
            {
                return null;
            }

            if (text[0] == '|' || text[0] == '>')
            {
                return ParseBlockScalar(text, ownerIndent, lineNumber);
            }

            if (text[0] == '[' || text[0] == '{')
            {
                return ParseFlow(text, lineNumber);
            }

            if (text[0] == '"' || text[0] == '\'')
            {
                var position = 0;
                var value = ParseQuoted(text, ref position, lineNumber);
                EnsureTrailing(text, position, lineNumber);
                return value;
            }

            return ParsePlain(text);
        }

        private void EnsureTrailing(string text, int position, int lineNumber)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            if (position < text.Length && text[position] != '#')
            {
                throw Error("unexpected content after a quoted scalar", lineNumber, position + 1);
            }
        }

        private object? ParseFlow(string first, int lineNumber)
        {
            var builder = new StringBuilder(first);
            while (!FlowComplete(builder.ToString()))
            {
                if (_index >= _lines.Length)
                {
                    throw Error("unterminated flow collection", lineNumber, 1);
                }

                var line = _lines[_index];
                _index++;
                if (line.Comment)
                {
                    continue;
                }

                builder.Append(' ').Append(line.Text);
            }

            var parser = new FlowParser(builder.ToString(), _source, lineNumber);
            var value = parser.ParseValue();
            parser.EnsureEnd();
            return value;
        }

        private static bool FlowComplete(string text)
        {
            var depth = 0;
            var quote = '\0';
            var tokenStart = true;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    if (quote == '"' && c == '\\')
                    {
                        i++;
                        continue;
                    }

                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    tokenStart = true;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    if (tokenStart)
                    {
                        quote = c;
                    }

                    tokenStart = false;
                    continue;
                }

                if (c == '[' || c == '{')
                {
                    depth++;
                    tokenStart = true;
                }
                else if (c == ']' || c == '}')
                {
                    depth--;
                    tokenStart = false;
                    if (depth < 0)
                    {
                        return true;
                    }
                }
                else if (c == ',' || c == ':')
                {
                    tokenStart = true;
                }
                else
                {
                    tokenStart = false;
                }
            }

            return depth <= 0;
        }

        private string ParseBlockScalar(string indicator, int ownerIndent, int lineNumber)
        {
            var literal = indicator[0] == '|';
            var chomp = ' ';
            var explicitIndent = 0;

            for (var i = 1; i < indicator.Length; i++)
            {
                var c = indicator[i];
                if (c == '-' || c == '+')
                {
                    chomp = c;
                }
                else if (c >= '1' && c <= '9')
                {
                    explicitIndent = c - '0';
                }
                else if (c == '#' || char.IsWhiteSpace(c))
                {
                    break;
                }
                else
                {
                    throw Error($"invalid block scalar indicator '{indicator}'", lineNumber, 1);
                }
            }

            var content = new List<Line>();
            var minIndent = int.MaxValue;
            while (_index < _lines.Length)
            {
                var line = _lines[_index];
                if (!line.Blank && line.Indent <= ownerIndent)
                {
                    break;
                }

                content.Add(line);
                if (!line.Blank)
                {
                    minIndent = Math.Min(minIndent, line.Indent);
                }

                _index++;
            }

            var indent = explicitIndent > 0
                ? ownerIndent + explicitIndent
                : minIndent == int.MaxValue ? ownerIndent + 1 : minIndent;

            if (indent <= ownerIndent)
            {
                throw Error("block scalar content must be indented deeper than its key", lineNumber, 1);
            }

            var lines = new List<string>(content.Count);
            foreach (var line in content)
            {
                if (line.Blank)
                {
                    lines.Add(string.Empty);
                }
                else if (line.Indent < indent)
                {
                    throw Error("block scalar line is less indented than the first content line", line.Number, line.Indent + 1);
                }
                else
                {
                    lines.Add(line.Raw.Length >= indent ? line.Raw.Substring(indent) : string.Empty);
                }
            }

            var lastContent = lines.Count - 1;
            while (lastContent >= 0 && lines[lastContent].Length == 0)
            {
                lastContent--;
            }

            var body = lastContent < 0
                ? string.Empty
                : literal
                    ? string.Join("\n", lines.GetRange(0, lastContent + 1))
                    : Fold(lines, lastContent);

            switch (chomp)
            {
                case '-':
                    return body.TrimEnd('\n');
                case '+':
                    if (lastContent < 0)
                    {
                        return new string('\n', lines.Count);
                    }

                    return body + new string('\n', lines.Count - 1 - lastContent + 1);
                default:
                    return body.Length == 0 ? string.Empty : body.TrimEnd('\n') + "\n";
            }
        }

        private static string Fold(List<string> lines, int lastContent)
        {
            var builder = new StringBuilder();
            for (var i = 0; i <= lastContent; i++)
            {
                builder.Append(lines[i]);
                if (i == lastContent)
                {
                    break;
                }

                if (lines[i].Length == 0 || lines[i + 1].Length == 0)
                {
                    builder.Append('\n');
                }
                else
                {
                    builder.Append(' ');
                }
            }

            return builder.ToString();
        }

        private static bool IsSequenceItem(string text) =>
            text.Length > 0 && text[0] == '-' && (text.Length == 1 || text[1] == ' ' || text[1] == '\t');

        private static bool TrySplitKey(string text, out string key, out string rest)
        {
            key = string.Empty;
            rest = string.Empty;
            if (text.Length == 0)
            {
                return false;
            }

            if (text[0] == '"' || text[0] == '\'')
            {
                var position = 0;
                string parsed;
                try
                {
                    parsed = ParseQuoted(text, ref position, 0);
                }
                catch (YamlException)
                {
                    return false;
                }

                while (position < text.Length && (text[position] == ' ' || text[position] == '\t'))
                {
                    position++;
                }

                if (position >= text.Length || text[position] != ':')
                {
                    return false;
                }

                if (position + 1 < text.Length && text[position + 1] != ' ' && text[position + 1] != '\t')
                {
                    return false;
                }

                key = parsed;
                rest = text.Substring(position + 1).Trim();
                return true;
            }

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != ':')
                {
                    continue;
                }

                if (i + 1 < text.Length && text[i + 1] != ' ' && text[i + 1] != '\t')
                {
                    continue;
                }

                key = text.Substring(0, i).Trim();
                rest = text.Substring(i + 1).Trim();
                return key.Length > 0;
            }

            return false;
        }

        private static object? ParsePlain(string text)
        {
            var stripped = StripComment(text).Trim();
            return stripped.Length == 0 ? null : ConvertPlain(stripped);
        }

        private static string StripComment(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '#' && (i == 0 || text[i - 1] == ' ' || text[i - 1] == '\t'))
                {
                    return text.Substring(0, i).TrimEnd();
                }
            }

            return text;
        }

        private static object? ConvertPlain(string text)
        {
            switch (text)
            {
                case "~":
                case "null":
                case "Null":
                case "NULL":
                    return null;
                case "true":
                case "True":
                case "TRUE":
                    return true;
                case "false":
                case "False":
                case "FALSE":
                    return false;
            }

            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            {
                return integer;
            }

            if (double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture, out var number) &&
                (text.IndexOf('.') >= 0 || text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0))
            {
                return number;
            }

            return text;
        }

        internal static string ParseQuoted(string text, ref int position, int lineNumber) =>
            ParseQuoted(text, ref position, lineNumber, null);

        private static string ParseQuoted(string text, ref int position, int lineNumber, string? source)
        {
            if (position >= text.Length)
            {
                throw new YamlException("expected a quoted scalar", source, lineNumber, position + 1);
            }

            var quote = text[position];
            if (quote != '"' && quote != '\'')
            {
                throw new YamlException("expected a quoted scalar", source, lineNumber, position + 1);
            }

            position++;
            var builder = new StringBuilder();
            while (position < text.Length)
            {
                var c = text[position++];
                if (c == quote)
                {
                    if (quote == '\'' && position < text.Length && text[position] == '\'')
                    {
                        builder.Append('\'');
                        position++;
                        continue;
                    }

                    return builder.ToString();
                }

                if (quote == '"' && c == '\\')
                {
                    if (position >= text.Length)
                    {
                        throw new YamlException("unterminated escape sequence", source, lineNumber, position + 1);
                    }

                    var escaped = text[position++];
                    switch (escaped)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case '0': builder.Append('\0'); break;
                        case 'u': AppendCodePoint(builder, text, ref position, 4, source, lineNumber); break;
                        case 'U': AppendCodePoint(builder, text, ref position, 8, source, lineNumber); break;
                        case 'x': AppendCodePoint(builder, text, ref position, 2, source, lineNumber); break;
                        default:
                            throw new YamlException($"unknown escape '\\{escaped}'", source, lineNumber, position);
                    }

                    continue;
                }

                builder.Append(c);
            }

            throw new YamlException("unterminated quoted scalar", source, lineNumber, position);
        }

        private static void AppendCodePoint(StringBuilder builder, string text, ref int position, int digits, string? source, int lineNumber)
        {
            if (position + digits > text.Length)
            {
                throw new YamlException("truncated escape sequence", source, lineNumber, position + 1);
            }

            if (!int.TryParse(text.Substring(position, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                throw new YamlException("invalid escape sequence", source, lineNumber, position + 1);
            }

            builder.Append(char.ConvertFromUtf32(code));
            position += digits;
        }

        private sealed class FlowParser
        {
            private readonly string _text;
            private readonly string? _source;
            private readonly int _line;
            private int _position;

            internal FlowParser(string text, string? source, int line)
            {
                _text = text;
                _source = source;
                _line = line;
            }

            internal object? ParseValue()
            {
                SkipWhitespace();
                if (_position >= _text.Length)
                {
                    throw Error("expected a value");
                }

                var c = _text[_position];
                if (c == '[')
                {
                    return ParseSequence();
                }

                if (c == '{')
                {
                    return ParseMap();
                }

                if (c == '"' || c == '\'')
                {
                    return ParseQuoted(_text, ref _position, _line, _source);
                }

                return ParsePlainValue();
            }

            internal void EnsureEnd()
            {
                SkipWhitespace();
                if (_position >= _text.Length || _text[_position] == '#')
                {
                    return;
                }

                throw Error("unexpected content after the flow collection");
            }

            private List<object?> ParseSequence()
            {
                _position++;
                var list = new List<object?>();
                SkipWhitespace();
                if (Take(']'))
                {
                    return list;
                }

                while (true)
                {
                    list.Add(ParseValue());
                    SkipWhitespace();
                    if (Take(']'))
                    {
                        return list;
                    }

                    if (!Take(','))
                    {
                        throw Error("expected ',' or ']'");
                    }

                    SkipWhitespace();
                    if (Take(']'))
                    {
                        return list;
                    }
                }
            }

            private YamlMap ParseMap()
            {
                _position++;
                var map = new YamlMap();
                SkipWhitespace();
                if (Take('}'))
                {
                    return map;
                }

                while (true)
                {
                    SkipWhitespace();
                    var key = ParseKey();
                    SkipWhitespace();
                    if (!Take(':'))
                    {
                        throw Error("expected ':' in a flow mapping");
                    }

                    var value = ParseValue();
                    if (map.ContainsKey(key))
                    {
                        throw Error($"duplicate key '{key}'");
                    }

                    map.Add(key, value);

                    SkipWhitespace();
                    if (Take('}'))
                    {
                        return map;
                    }

                    if (!Take(','))
                    {
                        throw Error("expected ',' or '}'");
                    }

                    SkipWhitespace();
                    if (Take('}'))
                    {
                        return map;
                    }
                }
            }

            private string ParseKey()
            {
                if (_position >= _text.Length)
                {
                    throw Error("expected a mapping key");
                }

                if (_text[_position] == '"' || _text[_position] == '\'')
                {
                    return ParseQuoted(_text, ref _position, _line, _source);
                }

                var start = _position;
                while (_position < _text.Length && _text[_position] != ':')
                {
                    _position++;
                }

                var key = _text.Substring(start, _position - start).Trim();
                if (key.Length == 0)
                {
                    throw Error("expected a mapping key");
                }

                return key;
            }

            private object? ParsePlainValue()
            {
                var start = _position;
                while (_position < _text.Length)
                {
                    var c = _text[_position];
                    if (c == ',' || c == ']' || c == '}')
                    {
                        break;
                    }

                    if (c == '#' && _position > start && char.IsWhiteSpace(_text[_position - 1]))
                    {
                        break;
                    }

                    _position++;
                }

                var raw = _text.Substring(start, _position - start).Trim();
                if (raw.Length == 0)
                {
                    throw Error("expected a scalar");
                }

                return ConvertPlain(raw);
            }

            private void SkipWhitespace()
            {
                while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
                {
                    _position++;
                }
            }

            private bool Take(char expected)
            {
                if (_position >= _text.Length || _text[_position] != expected)
                {
                    return false;
                }

                _position++;
                return true;
            }

            private YamlException Error(string message) => new(message, _source, _line, _position + 1);
        }
    }
}
