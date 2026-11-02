// netstandard2.1 lacks this attribute, which C# needs for `init` accessors and records.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
