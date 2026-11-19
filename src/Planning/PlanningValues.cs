using System;
using Jint;
using System.Collections.Generic;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace ValheimJS.Planning;

/// <summary>Parsed plan options. Step state itself lives in <see cref="PlanHost"/>.</summary>
internal sealed class PlanOptionsData
{
    internal double? TimeoutMs { get; private set; }
    internal double? PauseGraceMs { get; private set; }

    internal static PlanOptionsData Parse(JsValue value)
    {
        var options = new PlanOptionsData();
        if (value.IsNull() || value.IsUndefined())
        {
            return options;
        }

        if (value is not ObjectInstance obj)
        {
            throw new ArgumentException("plan options must be an object.");
        }

        options.TimeoutMs = ReadNumber(obj, "timeoutMs");
        options.PauseGraceMs = ReadNumber(obj, "pauseGraceMs");
        return options;
    }

    internal static double? ReadNumber(ObjectInstance obj, string name)
    {
        var value = obj.Get(name);
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        if (!value.IsNumber())
        {
            throw new ArgumentException($"plan option '{name}' must be a number.");
        }

        var number = value.AsNumber();
        if (double.IsNaN(number) || double.IsInfinity(number) || number < 0)
        {
            throw new ArgumentException($"plan option '{name}' must be a finite number >= 0.");
        }

        return number;
    }
}

/// <summary>Parsed retry policy for one step.</summary>
internal readonly struct RetryOptions
{
    internal RetryOptions(int attempts, double delayMs)
    {
        Attempts = attempts;
        DelayMs = delayMs;
    }

    internal int Attempts { get; }
    internal double DelayMs { get; }

    internal static RetryOptions? Parse(ObjectInstance? step)
    {
        if (step == null || step.Get("retry") is not ObjectInstance retry)
        {
            return null;
        }

        var attemptsValue = retry.Get("attempts");
        if (!attemptsValue.IsNumber())
        {
            throw new ArgumentException("step.retry.attempts must be a number.");
        }

        var attempts = attemptsValue.AsNumber();
        if (double.IsNaN(attempts) || double.IsInfinity(attempts) || attempts < 0 || attempts > 1000)
        {
            throw new ArgumentException("step.retry.attempts must be between 0 and 1000.");
        }

        var delay = PlanOptionsData.ReadNumber(retry, "delayMs") ?? 0;
        return new RetryOptions((int)attempts, delay);
    }
}
