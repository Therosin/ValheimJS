using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimJS.Utils;

/// <summary>Reports a malformed TOML document. Line and column are 1-based.</summary>
public sealed class TomlException : Exception
{
    public TomlException(string message, string? source, int line, int column)
        : base(Format(message, source, line, column))
    {
        Source = source;
        Line = line;
        Column = column;
    }

    public new string? Source { get; }

    public int Line { get; }

    public int Column { get; }

    private static string Format(string message, string? source, int line, int column) =>
        (source == null ? $"line {line}, column {column}" : $"{source}:{line}:{column}") + $": {message}";
}

/// <summary>
/// Small TOML 1.0 reader for plugin configuration/assets. It returns plain dictionaries, lists,
/// strings, booleans and numbers; date/time values stay strings. Tables, dotted keys, arrays,
/// inline tables, basic/literal strings and multiline strings are supported.
/// </summary>
public static class Toml
{
    public static Dictionary<string, object?> Parse(string text, string? source = null)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        return new Parser(text, source).Parse();
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly string? _source;
        private readonly Dictionary<string, object?> _root = new(StringComparer.Ordinal);
        private Dictionary<string, object?> _current;
        private int _index;
        private int _line = 1;
        private int _column = 1;

        internal Parser(string text, string? source)
        {
            _text = text;
            _source = source;
            _current = _root;
        }

        internal Dictionary<string, object?> Parse()
        {
            while (true)
            {
                SkipTrivia();
                if (End) return _root;

                if (Peek() == '[') ParseTableHeader();
                else ParseKeyValue(_current, terminatedByBrace: false);
                EndOfStatement();
            }
        }

        private void ParseTableHeader()
        {
            var arrayTable = Peek(1) == '[';
            Consume(arrayTable ? 2 : 1);
            SkipSpaces();
            var path = ParseKeyPath();
            SkipSpaces();
            Expect(']');
            if (arrayTable) Expect(']');

            if (path.Count == 0) Error("table name is required");
            var table = arrayTable ? OpenArrayTable(path) : OpenTable(path);
            _current = table;
        }

        private Dictionary<string, object?> OpenTable(List<string> path)
        {
            var node = _root;
            for (var i = 0; i < path.Count; i++)
            {
                if (!node.TryGetValue(path[i], out var value))
                {
                    var created = new Dictionary<string, object?>(StringComparer.Ordinal);
                    node[path[i]] = created;
                    node = created;
                    continue;
                }

                if (value is Dictionary<string, object?> child)
                {
                    node = child;
                    continue;
                }

                Error($"'{string.Join(".", path)}' is not a table");
            }

            return node;
        }

        private Dictionary<string, object?> OpenArrayTable(List<string> path)
        {
            if (path.Count == 0) Error("array table name is required");
            var parent = OpenTable(path.GetRange(0, path.Count - 1));
            var key = path[^1];
            if (!parent.TryGetValue(key, out var value))
            {
                var list = new List<object?>();
                parent[key] = list;
                var created = new Dictionary<string, object?>(StringComparer.Ordinal);
                list.Add(created);
                return created;
            }

            if (value is not List<object?>) Error($"'{string.Join(".", path)}' is not an array table");
            var element = new Dictionary<string, object?>(StringComparer.Ordinal);
            ((List<object?>)value!).Add(element);
            return element;
        }

        private void ParseKeyValue(Dictionary<string, object?> table, bool terminatedByBrace)
        {
            var path = ParseKeyPath();
            SkipSpaces();
            Expect('=');
            SkipSpaces();
            var value = ParseValue();
            Assign(table, path, value);
            if (!terminatedByBrace) return;
        }

        private void Assign(Dictionary<string, object?> table, List<string> path, object? value)
        {
            if (path.Count == 0) Error("key is required");
            var node = table;
            for (var i = 0; i < path.Count - 1; i++)
            {
                if (!node.TryGetValue(path[i], out var existing))
                {
                    var created = new Dictionary<string, object?>(StringComparer.Ordinal);
                    node[path[i]] = created;
                    node = created;
                    continue;
                }

                if (existing is not Dictionary<string, object?>) Error($"'{path[i]}' is not a table");
                node = (Dictionary<string, object?>)existing!;
            }

            var key = path[^1];
            if (node.ContainsKey(key)) Error($"duplicate key '{string.Join(".", path)}'");
            node[key] = value;
        }

        private List<string> ParseKeyPath()
        {
            var parts = new List<string>();
            while (true)
            {
                SkipSpaces();
                parts.Add(ParseKey());
                SkipSpaces();
                if (Peek() != '.') break;
                Consume(1);
            }

            return parts;
        }

        private string ParseKey()
        {
            if (End) Error("key is required");
            var c = Peek();
            if (c == '"' || c == '\'') return ParseString();
            var start = _index;
            while (!End && IsBareKey(Peek())) Consume(1);
            if (start == _index) Error("key is required");
            return _text[start.._index];
        }

        private object? ParseValue()
        {
            if (End) Error("value is required");
            var c = Peek();
            if (c == '"' || c == '\'') return ParseString();
            if (c == '[') return ParseArray();
            if (c == '{') return ParseInlineTable();

            var start = _index;
            while (!End && !IsValueTerminator(Peek())) Consume(1);
            var token = _text[start.._index].Trim();
            if (token.Length == 0) Error("value is required");
            return ParseBareValue(token);
        }

        private object? ParseArray()
        {
            Expect('[');
            var values = new List<object?>();
            while (true)
            {
                SkipTrivia();
                if (Peek() == ']') { Consume(1); return values; }
                values.Add(ParseValue());
                SkipTrivia();
                if (Peek() == ',') { Consume(1); continue; }
                Expect(']');
                return values;
            }
        }

        private Dictionary<string, object?> ParseInlineTable()
        {
            Expect('{');
            var table = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                SkipSpaces();
                if (Peek() == '}') { Consume(1); return table; }
                ParseKeyValue(table, terminatedByBrace: true);
                SkipSpaces();
                if (Peek() == ',') { Consume(1); continue; }
                Expect('}');
                return table;
            }
        }

        private object? ParseBareValue(string token)
        {
            if (token == "true") return true;
            if (token == "false") return false;
            var normalized = token.Replace("_", string.Empty, StringComparison.Ordinal);
            if (normalized.Equals("inf", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("+inf", StringComparison.OrdinalIgnoreCase)) return double.PositiveInfinity;
            if (normalized.Equals("-inf", StringComparison.OrdinalIgnoreCase)) return double.NegativeInfinity;
            if (normalized.Equals("nan", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("+nan", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("-nan", StringComparison.OrdinalIgnoreCase)) return double.NaN;

            if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(normalized[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return hex;
            if (normalized.StartsWith("0o", StringComparison.OrdinalIgnoreCase) &&
                TryParseBase(normalized[2..], 8, out var octal)) return octal;
            if (normalized.StartsWith("0b", StringComparison.OrdinalIgnoreCase) &&
                TryParseBase(normalized[2..], 2, out var binary)) return binary;

            if (normalized.Contains('.') || normalized.IndexOf('e') >= 0 || normalized.IndexOf('E') >= 0)
            {
                if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
            }
            else if (long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                return integer;
            }

            // TOML dates/times are kept as strings; no date type crosses into JS.
            if (LooksLikeDate(token)) return token;
            Error($"unsupported TOML value '{token}'");
            return null;
        }

        private string ParseString()
        {
            var quote = Peek();
            var multiline = Peek(1) == quote && Peek(2) == quote;
            Consume(multiline ? 3 : 1);
            var builder = new StringBuilder();
            if (multiline && Peek() == '\n') Consume(1);

            while (true)
            {
                if (End) Error("unterminated string");
                if (multiline && Peek() == quote && Peek(1) == quote && Peek(2) == quote)
                {
                    Consume(3);
                    return builder.ToString();
                }

                if (!multiline && Peek() == quote)
                {
                    Consume(1);
                    return builder.ToString();
                }

                if (!multiline && Peek() == '\n') Error("newline in string");
                var c = Peek();
                Consume(1);
                if (c == '\\' && quote == '"')
                {
                    builder.Append(ParseEscape());
                    continue;
                }

                builder.Append(c);
            }
        }

        private string ParseEscape()
        {
            if (End) Error("unterminated escape");
            var c = Peek();
            Consume(1);
            return c switch
            {
                'b' => "\b",
                't' => "\t",
                'n' => "\n",
                'f' => "\f",
                'r' => "\r",
                '"' => "\"",
                '\\' => "\\",
                'u' => ParseUnicode(4),
                'U' => ParseUnicode(8),
                _ => throw Error($"unsupported escape '\\{c}'")
            };
        }

        private string ParseUnicode(int digits)
        {
            if (_index + digits > _text.Length) Error("truncated unicode escape");
            var hex = _text.Substring(_index, digits);
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) Error("invalid unicode escape");
            Consume(digits);
            return char.ConvertFromUtf32(code);
        }

        private void EndOfStatement()
        {
            SkipSpaces();
            if (Peek() == '#') SkipComment();
            if (End) return;
            if (Peek() == '\r') Consume(1);
            if (Peek() == '\n') { Consume(1); return; }
            Error("expected end of line");
        }

        private void SkipTrivia()
        {
            while (!End)
            {
                if (Peek() == '#') { SkipComment(); continue; }
                if (char.IsWhiteSpace(Peek())) { Consume(1); continue; }
                break;
            }
        }

        private void SkipSpaces()
        {
            while (!End && (Peek() == ' ' || Peek() == '\t')) Consume(1);
        }

        private void SkipComment()
        {
            while (!End && Peek() != '\n') Consume(1);
        }

        private void Expect(char expected)
        {
            if (End || Peek() != expected) Error($"expected '{expected}'");
            Consume(1);
        }

        private char Peek(int offset = 0) =>
            _index + offset < _text.Length ? _text[_index + offset] : '\0';

        private bool End => _index >= _text.Length;

        private void Consume(int count)
        {
            for (var i = 0; i < count && _index < _text.Length; i++)
            {
                if (_text[_index] == '\n') { _line++; _column = 1; }
                else _column++;
                _index++;
            }
        }

        private TomlException Error(string message) =>
            throw new TomlException(message, _source, _line, _column);

        private static bool IsBareKey(char c) =>
            char.IsLetterOrDigit(c) || c == '_' || c == '-';

        private static bool IsValueTerminator(char c) =>
            c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == ',' || c == ']' || c == '}' || c == '#';

        private static bool LooksLikeDate(string token)
        {
            if (token.Length < 8) return false;
            var hasDigit = false;
            var hasSeparator = false;
            foreach (var c in token)
            {
                if (char.IsDigit(c)) hasDigit = true;
                else if (c == '-' || c == ':' || c == 'T' || c == 'Z' || c == '+' || c == '.') hasSeparator = true;
                else return false;
            }

            return hasDigit && hasSeparator;
        }

        private static bool TryParseBase(string text, int numberBase, out long value)
        {
            value = 0;
            foreach (var c in text)
            {
                var digit = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    >= 'A' and <= 'F' => c - 'A' + 10,
                    _ => -1
                };
                if (digit < 0 || digit >= numberBase) return false;
                value = value * numberBase + digit;
            }

            return true;
        }
    }
}
