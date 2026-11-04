using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;


namespace ValheimJS.Utils
{

    internal class FileLogger
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, string> _currentLogFiles = new Dictionary<string, string>();
        private const int MaxLogFiles = 5;

        /// <summary>Appends a line to a daily file and applies the five-file retention limit for that log name.</summary>
        /// <param name="baseFilePath">The base path, such as logs/plugin.log; the date is inserted before its extension.</param>
        /// <param name="message">The line to append, already formatted if timestamps or severity are wanted.</param>
        /// <remarks>
        /// This low-level writer expects the parent directory to exist. It uses the local date, serializes writes,
        /// and checks retention on the first write to each daily path. File failures are reported to Console
        /// instead of propagated. Prefer Logger for BepInEx output, filtering, formatting, and directory creation.
        /// </remarks>
        /// <example><code>
        /// // For a separate, already-formatted diagnostic stream:
        /// var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "diagnostics", "capture.log");
        /// System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        /// FileLogger.WriteLog(path, "Player reference captured.");
        /// </code></example>
        public static void WriteLog(string baseFilePath, string message)
        {
            try
            {
                lock (_lock)
                {
                    var datedFilePath = GetDatedFilePath(baseFilePath);

                    // Check if we need to cleanup old files (only when switching to a new day)
                    if (!_currentLogFiles.TryGetValue(baseFilePath, out var currentFile) || currentFile != datedFilePath)
                    {
                        using (System.IO.File.Open(datedFilePath, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.Read)) { }
                        CleanupOldLogFiles(baseFilePath);
                        _currentLogFiles[baseFilePath] = datedFilePath;
                    }

                    using (var writer = new System.IO.StreamWriter(datedFilePath, true))
                    {
                        writer.WriteLine(message);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"Failed to write log: {ex.Message}");
            }
        }

        private static string GetDatedFilePath(string baseFilePath)
        {
            var directory = System.IO.Path.GetDirectoryName(baseFilePath);
            var fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(baseFilePath);
            var extension = System.IO.Path.GetExtension(baseFilePath);
            var dateString = DateTime.Now.ToString("yyyy-MM-dd");

            return System.IO.Path.Combine(directory, $"{fileNameWithoutExtension}_{dateString}{extension}");
        }

        private static void CleanupOldLogFiles(string baseFilePath)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(baseFilePath);
                var fileNameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(baseFilePath);
                var extension = System.IO.Path.GetExtension(baseFilePath);

                if (!System.IO.Directory.Exists(directory))
                    return;

                // Find all log files matching the pattern
                var searchPattern = $"{fileNameWithoutExtension}_????-??-??{extension}";
                var logFiles = System.IO.Directory.GetFiles(directory, searchPattern)
                    .Select(f => new System.IO.FileInfo(f))
                    .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                    .ToArray();

                // Delete files beyond the MaxLogFiles limit
                if (logFiles.Length > MaxLogFiles)
                {
                    for (int i = MaxLogFiles; i < logFiles.Length; i++)
                    {
                        try
                        {
                            logFiles[i].Delete();
                        }
                        catch (Exception ex)
                        {
                            System.Console.WriteLine($"Failed to delete old log file {logFiles[i].Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"Failed to cleanup old log files: {ex.Message}");
            }
        }
    }

    /// <summary>Selects the logger's severity threshold; Debug enables every level.</summary>
    public enum LogLevel
    {
        /// <summary>Includes informational messages, warnings, and errors.</summary>
        Info = 0,
        /// <summary>Includes warnings and errors.</summary>
        Warning = 1,
        /// <summary>Includes errors only, unless IsDebug separately enables debug messages.</summary>
        Error = 2,
        /// <summary>Includes all message levels.</summary>
        Debug = 3
    }

    /// <summary>Provides BepInEx logging with severity filtering, subsystem labels, and optional daily files.</summary>
    public class Logger : IDisposable
    {
        private static class DefaultLogger
        {
            internal static readonly Logger Value = new Logger("ValheimJS.Utils");
        }

        /// <summary>Gets the cached default logger used by the registry and lifecycle helpers.</summary>
        /// <remarks>Created on first access with source name ValheimJS.Utils and no file output. Configure it during host initialization.</remarks>
        /// <example><code>
        /// Logger.Shared.IsDebug = debugEnabled;
        /// Logger.Shared.LogInfo("Startup", "Waiting for ready game services.");
        /// </code></example>
        public static Logger Shared { get { return DefaultLogger.Value; } }

        /// <summary>Gets or sets whether debug messages bypass this logger's verbosity filter.</summary>
        /// <value>False by default. Does not change filtering for Info, Warning, or Error messages.</value>
        /// <remarks>BepInEx listener settings still determine whether debug messages appear in its output.</remarks>
        public bool IsDebug { get; set; }

        /// <summary>Unregisters this logger's BepInEx source and disposes it.</summary>
        /// <remarks>
        /// Call during plugin shutdown after its patches and callbacks stop logging. Do not use this instance
        /// again. Shared is a single cached logger, so disposing it does not create a replacement on later access.
        /// </remarks>
        /// <example><code>
        /// // In the owning plugin's shutdown hook:
        /// logger.LogInfo("Stopping plugin.");
        /// logger.Dispose();
        /// </code></example>
        public void Dispose()
        {
            BepInEx.Logging.Logger.Sources.Remove(_log);
            _log.Dispose();
        }

        private readonly string _name;
        private readonly string? _filePath;
        private readonly ManualLogSource _log;

        /// <summary>Gets or sets this logger's severity threshold.</summary>
        /// <value>Info by default, which includes Info, Warning, and Error messages.</value>
        /// <remarks>
        /// Warning includes Warning and Error; Error includes only Error; Debug includes all four levels.
        /// IsDebug can additionally enable debug messages at any threshold. BepInEx listeners may apply
        /// further filtering. The optional daily file follows this logger's filter independently of listeners.
        /// </remarks>
        public LogLevel LogLevel { get; set; } = LogLevel.Info;

        /// <summary>Creates a BepInEx log source with optional daily file output.</summary>
        /// <param name="name">The plugin or feature name shown as the BepInEx source and in file messages.</param>
        /// <param name="filePath">An optional base file path; null or empty disables extra file output.</param>
        /// <remarks>
        /// Relative paths resolve at construction. Missing parent directories are created. Files use the local
        /// date, for example plugin_2026-11-02.log for plugin.log, and retain up to five matching daily files
        /// when retention succeeds. Default verbosity is Info. Own this logger for the plugin lifetime and
        /// dispose it on shutdown. Path and directory creation failures can propagate from construction.
        /// </remarks>
        /// <example><code>
        /// // During plugin initialization:
        /// var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "logs", "plugin.log");
        /// var logger = new Logger("ExamplePlugin", path);
        /// logger.LogLevel = LogLevel.Info;
        /// logger.IsDebug = debugEnabled; // For example, a host configuration value.
        /// logger.LogInfo("Startup", "Patches installed; waiting for game services.");
        /// </code></example>
        public Logger(string name, string? filePath = null)
        {
            _name = name;
            _log = BepInEx.Logging.Logger.CreateLogSource(name);

            if (!string.IsNullOrEmpty(filePath))
            {
                var directory = System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                    System.IO.Directory.CreateDirectory(directory);

                _filePath = System.IO.Path.GetFullPath(filePath);
            }
        }

        // Preserve enum values while using the usual severity threshold:
        // Info includes warnings/errors; Debug includes everything.
        private bool ShouldLog(LogLevel level)
        {
            if (level == LogLevel.Debug) return IsDebug || LogLevel == LogLevel.Debug;
            return LogLevel == LogLevel.Debug || level >= LogLevel;
        }

        private void WriteLog(string message, string level)
        {
            if (!string.IsNullOrEmpty(_filePath))
            {
                var formatted = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level.ToUpper()}] {_name}: {message}";
                FileLogger.WriteLog(_filePath, formatted);
            }
        }

        /// <summary>Writes an informational message about normal setup or lifecycle progress.</summary>
        /// <param name="message">The message text; null or empty messages are ignored.</param>
        /// <remarks>Emitted at Info or Debug verbosity. Accepted messages go to the BepInEx source and the optional daily file.</remarks>
        /// <example><code>
        /// logger.LogInfo("Player service captured.");
        /// </code></example>
        public void LogInfo(string message)
        {
            if (!ShouldLog(LogLevel.Info)) return;
            if (string.IsNullOrEmpty(message)) return;
            _log.LogInfo(message);
            WriteLog(message, "info");
        }

        /// <summary>Writes an informational message about normal setup or lifecycle progress with a subsystem label.</summary>
        /// <param name="source">The patch or subsystem label prefixed to the message.</param>
        /// <param name="message">The message text; null or empty messages are ignored.</param>
        /// <remarks>Emitted at Info or Debug verbosity. Accepted messages use the format source: message in BepInEx and optional file output.</remarks>
        /// <example><code>
        /// logger.LogInfo("PlayerPatch", "Player service captured.");
        /// </code></example>
        public void LogInfo(string source, string message)
        {
            if (!ShouldLog(LogLevel.Info)) return;
            if (string.IsNullOrEmpty(message)) return;
            _log.LogInfo(source + ": " + message);
            WriteLog($"{source}: {message}", "info");
        }

        /// <summary>Writes a warning about a recoverable condition.</summary>
        /// <param name="message">The message text; null or empty messages are ignored.</param>
        /// <remarks>Emitted at Info, Warning, or Debug verbosity. Accepted messages go to the BepInEx source and the optional daily file.</remarks>
        /// <example><code>
        /// logger.LogWarning("Player service is not ready; retrying later.");
        /// </code></example>
        public void LogWarning(string message)
        {
            if (!ShouldLog(LogLevel.Warning)) return;
            if (string.IsNullOrEmpty(message)) return;
            _log.LogWarning(message);
            WriteLog(message, "warning");
        }

        /// <summary>Writes a warning about a recoverable condition with a subsystem label.</summary>
        /// <param name="source">The patch or subsystem label prefixed to the message.</param>
        /// <param name="message">The message text; null or empty messages are ignored.</param>
        /// <remarks>Emitted at Info, Warning, or Debug verbosity. Accepted messages use the format source: message in BepInEx and optional file output.</remarks>
        /// <example><code>
        /// logger.LogWarning("PlayerPatch", "Player service is not ready; retrying later.");
        /// </code></example>
        public void LogWarning(string source, string message)
        {
            if (!ShouldLog(LogLevel.Warning)) return;
            if (string.IsNullOrEmpty(message)) return;
            _log.LogWarning(source + ": " + message);
            WriteLog($"{source}: {message}", "warning");
        }

        /// <summary>Writes an error about an operation that cannot proceed.</summary>
        /// <param name="message">The message text; null or empty messages are ignored.</param>
        /// <remarks>Emitted at every defined verbosity level. Accepted messages go to the BepInEx source and the optional daily file.</remarks>
        /// <example><code>
        /// logger.LogError("Required scene service is unavailable; cannot open the panel.");
        /// </code></example>
        public void LogError(string message)
        {
            if (!ShouldLog(LogLevel.Error)) return;
            if (string.IsNullOrEmpty(message)) return;
            _log.LogError(message);
            WriteLog(message, "error");
        }

        /// <summary>Writes an error about an operation that cannot proceed with a subsystem label.</summary>
        /// <param name="source">The patch or subsystem label prefixed to the message.</param>
        /// <param name="message">The message text; null or empty messages are ignored.</param>
        /// <remarks>Emitted at every defined verbosity level. Accepted messages use the format source: message in BepInEx and optional file output.</remarks>
        /// <example><code>
        /// logger.LogError("PlayerPatch", "Required scene service is unavailable; cannot open the panel.");
        /// </code></example>
        public void LogError(string source, string message)
        {
            if (!ShouldLog(LogLevel.Error)) return;
            if (string.IsNullOrEmpty(message)) return;
            _log.LogError(source + ": " + message);
            WriteLog($"{source}: {message}", "error");
        }

        /// <summary>Writes a diagnostic message about capture or availability checks.</summary>
        /// <param name="message">The message text; null or empty messages are ignored.</param>
        /// <remarks>Emitted when IsDebug is true or verbosity is Debug; BepInEx listeners may still filter debug output. Accepted messages go to the BepInEx source and the optional daily file.</remarks>
        /// <example><code>
        /// logger.LogDebug("Player service instance already captured.");
        /// </code></example>
        public void LogDebug(string message)
        {
            if (!ShouldLog(LogLevel.Debug)) return;
            if (string.IsNullOrEmpty(message)) return;
            _log.LogDebug(message);
            WriteLog(message, "debug");
        }

        /// <summary>Writes a diagnostic message about capture or availability checks with a subsystem label.</summary>
        /// <param name="source">The patch or subsystem label prefixed to the message.</param>
        /// <param name="message">The message text; null or empty messages are ignored.</param>
        /// <remarks>Emitted when IsDebug is true or verbosity is Debug; BepInEx listeners may still filter debug output. Accepted messages use the format source: message in BepInEx and optional file output.</remarks>
        /// <example><code>
        /// logger.LogDebug("PlayerPatch", "Player service instance already captured.");
        /// </code></example>
        public void LogDebug(string source, string message)
        {
            if (!ShouldLog(LogLevel.Debug)) return;
            if (string.IsNullOrEmpty(message)) return;
            _log.LogDebug(source + ": " + message);
            WriteLog($"{source}: {message}", "debug");
        }
    }

}
