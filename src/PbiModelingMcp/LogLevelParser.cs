using Serilog.Events;

namespace PbiModelingMcp;

/// <summary>
/// Parses the <c>PBI_MCP__LogLevel</c> setting into a Serilog
/// <see cref="LogEventLevel"/>. Unrecognized values fall back to
/// <see cref="LogEventLevel.Information"/>; the caller is expected to surface
/// a warning in that case so an operator typo is visible.
/// </summary>
internal static class LogLevelParser
{
    /// <summary>
    /// Parse <paramref name="raw"/> into a Serilog level.
    /// </summary>
    /// <param name="raw">Raw configured value, possibly null/empty.</param>
    /// <param name="recognized">
    /// True when <paramref name="raw"/> matched a known token (including
    /// null/empty, which deliberately means "use the default"). False when
    /// the operator supplied a non-empty value that did not match — the
    /// caller should log a warning so the typo isn't silent.
    /// </param>
    public static LogEventLevel Parse(string? raw, out bool recognized)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
                recognized = true;
                return LogEventLevel.Information;
            case "verbose":
            case "trace":
            case "0":
                recognized = true;
                return LogEventLevel.Verbose;
            case "debug":
            case "1":
                recognized = true;
                return LogEventLevel.Debug;
            case "information":
            case "info":
            case "2":
                recognized = true;
                return LogEventLevel.Information;
            case "warning":
            case "warn":
            case "3":
                recognized = true;
                return LogEventLevel.Warning;
            case "error":
            case "4":
                recognized = true;
                return LogEventLevel.Error;
            case "fatal":
            case "critical":
            case "5":
                recognized = true;
                return LogEventLevel.Fatal;
            default:
                recognized = false;
                return LogEventLevel.Information;
        }
    }
}
