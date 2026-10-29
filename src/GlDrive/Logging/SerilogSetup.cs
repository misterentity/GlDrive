using System.IO;
using GlDrive.AiAgent;
using GlDrive.Config;
using GlDrive.Services.Automation;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace GlDrive.Logging;

public static class SerilogSetup
{
    private static LoggingLevelSwitch? _levelSwitch;
    public static FlightRecorder? Recorder { get; private set; }
    public static ComponentTraceSwitches Traces { get; } = new();

    /// <summary>
    /// How many size-triggered rolls a single retained day is allowed before the
    /// file-count cap starts evicting. Bounds worst-case log disk at
    /// RetainedFiles * IntradayRollAllowance * MaxFileSizeMb, additionally capped at 300 MiB
    /// while leaving normal days (1 file each) well clear of the cap.
    /// </summary>
    internal const int IntradayRollAllowance = 8;

    /// <summary>
    /// Singleton sink installed during Configure(). Assign Recorder after TelemetryRecorder is ready.
    /// </summary>
    public static ErrorSignatureSink AgentSink { get; } = new ErrorSignatureSink();

    public static void Configure(LoggingConfig? config = null, AppConfig? applicationConfig = null)
    {
        config ??= new LoggingConfig();
        var logFolder = Path.Combine(ConfigManager.AppDataPath, "logs");
        Directory.CreateDirectory(logFolder);

        _levelSwitch = new LoggingLevelSwitch(ParseLevel(config.Level));
        Recorder?.Dispose();
        var diagnostics = new DiagnosticAutomation(applicationConfig ?? new AppConfig { Logging = config });
        Recorder = new FlightRecorder(ConfigManager.AppDataPath, () => diagnostics.CreateTextRedactor(requireCompleteInventory: true), () => config.IncidentCaptureEnabled);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Logger(disk => disk.MinimumLevel.Verbose().Filter.ByIncludingOnly(e => e.Level >= _levelSwitch.MinimumLevel ||
                Traces.IsEnabled(ComponentTraceSwitches.Component(e), DateTimeOffset.UtcNow)).WriteTo.File(
                Path.Combine(logFolder, "gldrive-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: Math.Clamp(config.MaxFileSizeMb, 1, 300) * 1024L * 1024,
                rollOnFileSizeLimit: true,
                // RetainedFiles means DAYS of history, but retainedFileCountLimit counts
                // FILES — and rollOnFileSizeLimit adds a file every time a day exceeds the
                // size cap. So a noisy day silently ate its neighbours' history: on
                // 2026-08-03 one mid-day roll cut "3 days" down to ~1.5, destroying the
                // retention exactly when an incident made it worth having.
                // Time is now the semantic bound; the count is only a disk-blowout stop,
                // sized to allow IntradayRollAllowance rolls per retained day.
                retainedFileTimeLimit: TimeSpan.FromDays(Math.Clamp(config.RetainedFiles, 1, 365)),
                retainedFileCountLimit: RetainedFileCount(config),
                // Program.Main records rejected second launches before Serilog is configured.
                // The primary keeps this file open for its whole lifetime, so allow that
                // one-line audit append (and other watchdog/startup diagnostics) to coexist.
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
            .WriteTo.Sink(AgentSink)
            .WriteTo.Sink(Recorder)
            .CreateLogger();

        Log.Information("GlDrive logging initialized at {Level} level", _levelSwitch.MinimumLevel);
    }

    internal static int RetainedFileCount(LoggingConfig config) => Math.Min(Math.Clamp(config.RetainedFiles, 1, 365) * IntradayRollAllowance,
        Math.Max(1, 300 / Math.Clamp(config.MaxFileSizeMb, 1, 300)));

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
        "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };
}
