namespace GlDrive.Downloads.ArchiveExtraction;

/// <summary>
/// The one live extraction engine. Startup auto-watch, the extractor window and MCP
/// automation all share it, so a queue built by automation is the queue the window shows.
///
/// Lifetime is unchanged from when the engine lived inside the window: closing the window
/// releases (disposes) the engine — watchers stop — and the next user creates a fresh one.
/// </summary>
internal static class ExtractorEngineHost
{
    private static readonly object Gate = new();
    private static ArchiveExtractionEngine? _current;

    internal static ArchiveExtractionEngine? Current
    {
        get { lock (Gate) return _current; }
    }

    /// <summary>
    /// The live engine, creating it if none exists. <paramref name="startWatchers"/> only
    /// applies on creation: false loads saved settings without starting saved watchers.
    /// </summary>
    internal static ArchiveExtractionEngine Acquire(SynchronizationContext? ui, bool startWatchers)
    {
        lock (Gate)
            return _current ??= new ArchiveExtractionEngine(ui, new ExtractorEngineOptions { StartWatchers = startWatchers });
    }

    /// <summary>Dispose <paramref name="engine"/> and forget it if it is the live one.</summary>
    internal static void Release(ArchiveExtractionEngine engine)
    {
        lock (Gate)
            if (ReferenceEquals(_current, engine)) _current = null;
        engine.Dispose();
    }
}
