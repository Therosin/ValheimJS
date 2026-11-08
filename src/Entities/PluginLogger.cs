namespace ValheimJS.Entities;

/// <summary>Small logging seam so the entity layer keeps using the plugin logger.</summary>
internal static class PluginLogger
{
    internal static void Warn(string message) => Plugin.Logger.LogWarning(message);
}
