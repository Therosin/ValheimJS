namespace ValheimJS.Scripting;

/// <summary>
/// Contract version for the JS-facing API (host functions, bindings, hook payload shapes).
/// Bump on breaking changes; plugins whose manifest asks for another major version are
/// rejected with a clear log line instead of failing unpredictably at runtime.
/// </summary>
public static class HostApiVersion
{
    public const string Current = "1";
}
