using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace ValheimJS.Commands;

public sealed class ParsedChatCommand
{
    internal ParsedChatCommand(string route, Dictionary<string, string> options, string text)
    { Route = route; Options = new ReadOnlyDictionary<string, string>(options); Text = text; }
    public string Route { get; }
    public IReadOnlyDictionary<string, string> Options { get; }
    public string Text { get; }
}

/// <summary>Leading options only; after the first ordinary token the remaining text is literal.</summary>
public static class ChatCommandParser
{
    public const int MaxLength = 4096;
    private static readonly Regex RoutePattern = new(@"^[A-Za-z0-9_-]+(?::[A-Za-z0-9_-]+)?$", RegexOptions.Compiled);
    private static readonly Regex KeyPattern = new(@"^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.Compiled);
    public static bool IsCommand(string text) => !string.IsNullOrEmpty(text) && text[0] == '!';
    public static string Route(string value)
    {
        if (value == null || !RoutePattern.IsMatch(value)) throw new ArgumentException("Commands use !name or !namespace:name.");
        return value.ToLowerInvariant();
    }
    public static ParsedChatCommand Parse(string text)
    {
        if (!IsCommand(text) || text.Length > MaxLength) throw new ArgumentException("Invalid command or command exceeds 4096 characters.");
        var index = 1;
        while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
        var route = Route(text.Substring(1, index - 1));
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index == text.Length) return new ParsedChatCommand(route, options, "");
            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != '=') index++;
            if (index == text.Length || text[index] != '=') return new ParsedChatCommand(route, options, text.Substring(start));
            var key = text.Substring(start, index - start);
            if (!KeyPattern.IsMatch(key)) throw new ArgumentException("Invalid option name.");
            index++;
            string value;
            if (index < text.Length && text[index] == '"')
            {
                index++;
                var builder = new StringBuilder();
                var closed = false;
                while (index < text.Length)
                {
                    var character = text[index++];
                    if (character == '"') { closed = true; break; }
                    if (character == '\\')
                    {
                        if (index == text.Length || (text[index] != '\\' && text[index] != '"')) throw new ArgumentException("Quoted options support only \\\\ and \\\" escapes.");
                        character = text[index++];
                    }
                    builder.Append(character);
                }
                if (!closed || (index < text.Length && !char.IsWhiteSpace(text[index]))) throw new ArgumentException("Malformed quoted option.");
                value = builder.ToString();
            }
            else
            {
                var valueStart = index;
                while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
                value = text.Substring(valueStart, index - valueStart);
            }
            if (options.ContainsKey(key)) throw new ArgumentException($"Duplicate option '{key}'.");
            options.Add(key, value);
        }
    }
}
