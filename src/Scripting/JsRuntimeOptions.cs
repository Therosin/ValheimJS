namespace ValheimJS.Scripting;

/// <summary>
/// Execution guardrails applied to the shared engine. Plugin code is untrusted by default: a
/// runaway loop must not hang the server frame, so the engine is bounded rather than trusted.
/// </summary>
public sealed class JsRuntimeOptions
{
    /// <summary>Maximum statements per script execution (load or hook). 0 disables the limit.</summary>
    public long MaxStatements { get; init; } = 5_000_000;

    /// <summary>Wall-clock budget per execution in milliseconds. 0 disables the limit.</summary>
    public int TimeoutMs { get; init; } = 2_000;

    /// <summary>Maximum JS call-stack depth. 0 disables the limit.</summary>
    public int LimitRecursion { get; init; } = 256;

    /// <summary>Maximum engine memory in bytes; 0 disables the limit.</summary>
    public long MemoryLimitBytes { get; init; }
}
