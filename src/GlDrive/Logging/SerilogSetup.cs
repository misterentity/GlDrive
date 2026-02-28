using System.IO;
using System.Text.RegularExpressions;
using GlDrive.Config;
using Serilog;
using Serilog.Events;

namespace GlDrive.Logging;

public static partial class SerilogSetup
{
    public static void Configure(LoggingConfig? config = null)
    {
        config ??= new LoggingConfig();
        var logFolder = Path.Combine(ConfigManager.AppDataPath, "logs");
        Directory.CreateDirectory(logFolder);

        var level = config.Level?.ToLowerInvariant() switch
        {
            "debug" => LogEventLevel.Debug,
            "verbose" => LogEventLevel.Verbose,
            "warning" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            _ => LogEventLevel.Information
        };

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.With(new PasswordRedactingEnricher())
            .WriteTo.File(
                Path.Combine(logFolder, "gldrive-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: config.MaxFileSizeMb * 1024 * 1024,
                retainedFileCountLimit: config.RetainedFiles,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("GlDrive logging initialized at {Level} level", level);
    }

    [GeneratedRegex(@"(?i)(pass(?:word)?|pwd)\s*[:=]\s*\S+")]
    private static partial Regex PasswordPattern();

    private class PasswordRedactingEnricher : Serilog.Core.ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, Serilog.Core.ILogEventPropertyFactory propertyFactory)
        {
            // Serilog doesn't have a built-in message rewrite mechanism,
            // but we avoid logging passwords by never passing them as parameters.
            // This enricher exists as a safety net placeholder.
        }
    }
}
