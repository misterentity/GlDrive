using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GlDrive.Downloads.ArchiveExtraction;

/// <summary>
/// Persisted as <c>%AppData%\GlDrive\extractor-settings.json</c> (camelCase). The property
/// names are the file's shape and the <c>extractor.state</c>/<c>extractor.settings</c>
/// automation contract — rename nothing here.
/// </summary>
public class ExtractorSettings
{
    public List<string> WatchFolders { get; set; } = [];
    public bool WatchEnabled { get; set; }
    public bool DeleteAfterExtract { get; set; }
    public bool ScanSubfolders { get; set; } = true;
    public bool CreateSubfolder { get; set; }
    public int OutputMode { get; set; }
    public int OverwriteMode { get; set; }
    public string CustomOutputPath { get; set; } = "";
}

/// <summary>One queued archive. Bound by the extractor window and serialized by <c>extractor.state</c>.</summary>
public class ArchiveItem : INotifyPropertyChanged
{
    private string _status = "Queued";
    private double _progress;
    private int _entryCount;
    private string? _errorMessage;

    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ArchiveType { get; init; } = "";
    public long Size { get; init; }
    public string SizeText { get; init; } = "";

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); }
    }

    public int EntryCount
    {
        get => _entryCount;
        set { _entryCount = value; OnPropertyChanged(); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One progress observation from a running extraction. Either field may be absent; a
/// <see cref="Percent"/> of -1 means indeterminate (the streaming reader cannot know the total).
/// </summary>
public readonly record struct ArchiveProgress(double? Percent = null, int? EntryCount = null);
