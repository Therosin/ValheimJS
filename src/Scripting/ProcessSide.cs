using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimJS.Scripting;

public enum ProcessSide { Server, Client }

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public abstract class ProcessSideAttribute : Attribute { }
public sealed class SharedAttribute : ProcessSideAttribute { }
public sealed class ServerOnlyAttribute : ProcessSideAttribute { }
public sealed class ClientOnlyAttribute : ProcessSideAttribute { }

/// <summary>Metadata-only selection: validate the entire catalog before constructing anything.</summary>
public static class ProcessPolicy
{
    public static ProcessSide Resolve(string processName, string configured = "auto")
    {
        switch (configured.Trim().ToLowerInvariant())
        {
            case "server": return ProcessSide.Server;
            case "client": return ProcessSide.Client;
            case "auto":
                switch (processName.ToLowerInvariant())
                {
                    case "valheim_server": return ProcessSide.Server;
                    case "valheim": return ProcessSide.Client;
                    default: throw new InvalidOperationException($"Unknown process '{processName}'; set General.ProcessSide to server or client.");
                }
            default: throw new InvalidOperationException("General.ProcessSide must be auto, server, or client.");
        }
    }

    public static IReadOnlyList<Type> Select(IEnumerable<Type> types, ProcessSide side)
    {
        var catalog = types.Select(type => (Type: type, Attributes: type.GetCustomAttributes(typeof(ProcessSideAttribute), false))).ToList();
        foreach (var item in catalog)
            if (item.Attributes.Length != 1)
                throw new InvalidOperationException($"{item.Type.FullName}: require exactly one of [Shared], [ServerOnly], [ClientOnly].");
        return catalog.Where(item => item.Attributes[0] is SharedAttribute ||
                (side == ProcessSide.Server ? item.Attributes[0] is ServerOnlyAttribute : item.Attributes[0] is ClientOnlyAttribute))
            .OrderBy(item => item.Attributes[0] is SharedAttribute ? 0 : 1)
            .ThenBy(item => item.Type.FullName, StringComparer.Ordinal)
            .Select(item => item.Type).ToList();
    }
}
