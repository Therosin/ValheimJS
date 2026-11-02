using System.Globalization;

namespace ValheimJS.Utils;

/// <summary>Tiny JSON emitters. The host builds small documents by hand; parsing lives in JS.</summary>
internal static class Json
{
    internal static string Quote(string? value)
    {
        if (value == null)
        {
            return "null";
        }

        return "\"" +
               value.Replace("\\", "\\\\")
                   .Replace("\"", "\\\"")
                   .Replace("\n", "\\n")
                   .Replace("\r", "\\r")
                   .Replace("\t", "\\t") +
               "\"";
    }

    internal static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    internal static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
