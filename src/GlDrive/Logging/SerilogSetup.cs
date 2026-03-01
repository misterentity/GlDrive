using System.IO;
using GlDrive.Config;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace GlDrive.Logging;

public static class SerilogSetup
{
    private static LoggingLevelSwitch? _levelSwitch;

    public static void Configure(LoggingConfig? config = null)
    {
        config ??= new LoggingConfig();
        var logFolder = Path.Combine(ConfigManager.AppDataPath, "logs");
        Directory.CreateDirectory(logFolder);

        _levelSwitch = new LoggingLevelSwitch(ParseLevel(config.Level));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_levelSwitch)
            .WriteTo.File(
                Path.Combine(logFolder, "gldrive-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: config.MaxFileSizeMb * 1024 * 1024,
                retainedFileCountLimit: config.RetainedFiles,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("GlDrive logging initialized at {Level} level", _levelSwitch.MinimumLevel);
    }

    /// <summary>
    /// Change log level at runtime without restarting the app.
    /// </summary>
    public static void SetLevel(string level)
    {
        if (_levelSwitch == null) return;
        _levelSwitch.MinimumLevel = ParseLevel(level);
        Log.Information("Log level changed to {Level}", _levelSwitch.MinimumLevel);
    }

    private static LogEventLevel ParseLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "debug" => LogEventLevel.Debug,
        "verbose" => LogEventLevel.Verbose,
        "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        _ => LogEventLevel.Information
    };
}
