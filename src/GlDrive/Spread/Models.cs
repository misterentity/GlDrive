using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace GlDrive.Spread;

public class SpreadJobVm : INotifyPropertyChanged
{
    private string _release = "";
    private string _section = "";
    private SpreadJobState _state;
    private double _progressPercent;
    private string _status = "";
    private int _score;
    private string _sourceDisplay = "";
    private string _destDisplay = "";
    private string _filesDoneTotalDisplay = "";
    private bool _isPaused;
    private bool _isPred;
    private bool _isAutoRace;
    private string _scoreLabel = "";
    private PointCollection _sparklinePoints = new();

    public string Id { get; set; } = "";
    public string Release { get => _release; set { _release = value; OnPropertyChanged(); } }
    public string Section { get => _section; set { _section = value; OnPropertyChanged(); } }
    public SpreadJobState State { get => _state; set { _state = value; OnPropertyChanged(); } }
    public double ProgressPercent { get => _progressPercent; set { _progressPercent = value; OnPropertyChanged(); } }
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    public int Score
    {
        get => _score;
        set { if (_score == value) return; _score = value; OnPropertyChanged(); }
    }

    public string SourceDisplay
    {
        get => _sourceDisplay;
        set { if (_sourceDisplay == value) return; _sourceDisplay = value; OnPropertyChanged(); }
    }

    public string DestDisplay
    {
        get => _destDisplay;
        set { if (_destDisplay == value) return; _destDisplay = value; OnPropertyChanged(); }
    }

    public string FilesDoneTotalDisplay
    {
        get => _filesDoneTotalDisplay;
        set { if (_filesDoneTotalDisplay == value) return; _filesDoneTotalDisplay = value; OnPropertyChanged(); }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set { if (_isPaused == value) return; _isPaused = value; OnPropertyChanged(); }
    }

    public bool IsPred
    {
        get => _isPred;
        set { if (_isPred == value) return; _isPred = value; OnPropertyChanged(); }
    }

    public bool IsAutoRace
    {
        get => _isAutoRace;
        set { if (_isAutoRace == value) return; _isAutoRace = value; OnPropertyChanged(); }
    }

    public string ScoreLabel
    {
        get => _scoreLabel;
        set { if (_scoreLabel == value) return; _scoreLabel = value; OnPropertyChanged(); }
    }

    public PointCollection SparklinePoints
    {
        get => _sparklinePoints;
        set { _sparklinePoints = value; OnPropertyChanged(); }
    }

    public SpreadJobVm()
    {
        for (int i = 0; i < 60; i++)
            _sparklinePoints.Add(new Point(i, 20));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class SpreadFileVm : INotifyPropertyChanged
{
    private string _fileName = "";
    private long _size;
    private string _source = "";
    private string _dest = "";
    private double _speedBps;
    private double _progressPercent;
    private string _status = "";

    public string FileName { get => _fileName; set { _fileName = value; OnPropertyChanged(); } }
    public long Size { get => _size; set { _size = value; OnPropertyChanged(); } }
    public string Source { get => _source; set { _source = value; OnPropertyChanged(); } }
    public string Dest { get => _dest; set { _dest = value; OnPropertyChanged(); } }
    public double SpeedBps { get => _speedBps; set { _speedBps = value; OnPropertyChanged(); } }
    public double ProgressPercent { get => _progressPercent; set { _progressPercent = value; OnPropertyChanged(); } }
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    public string SizeFormatted => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{Size / (1024.0 * 1024):F1} MB",
        _ => $"{Size / (1024.0 * 1024 * 1024):F2} GB"
    };

    public string SpeedFormatted => SpeedBps switch
    {
        0 => "",
        < 1024 => $"{SpeedBps:F0} B/s",
        < 1024 * 1024 => $"{SpeedBps / 1024:F1} KB/s",
        _ => $"{SpeedBps / (1024 * 1024):F1} MB/s"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class SpreadScoreVm : INotifyPropertyChanged
{
    private string _siteName = "";
    private int _filesOwned;
    private int _filesTotal;
    private double _speedBps;
    private string _status = "";

    public string SiteName { get => _siteName; set { _siteName = value; OnPropertyChanged(); } }
    public int FilesOwned { get => _filesOwned; set { _filesOwned = value; OnPropertyChanged(); } }
    public int FilesTotal { get => _filesTotal; set { _filesTotal = value; OnPropertyChanged(); } }
    public double OwnedPercent => FilesTotal > 0 ? FilesOwned * 100.0 / FilesTotal : 0;
    public double SpeedBps { get => _speedBps; set { _speedBps = value; OnPropertyChanged(); } }
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    public string SpeedFormatted => SpeedBps switch
    {
        0 => "",
        < 1024 => $"{SpeedBps:F0} B/s",
        < 1024 * 1024 => $"{SpeedBps / 1024:F1} KB/s",
        _ => $"{SpeedBps / (1024 * 1024):F1} MB/s"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class BrowseItemVm : INotifyPropertyChanged
{
    private string _name = "";
    private long _size;
    private DateTime _modified;
    private bool _isDirectory;

    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public long Size { get => _size; set { _size = value; OnPropertyChanged(); } }
    public DateTime Modified { get => _modified; set { _modified = value; OnPropertyChanged(); } }
    public bool IsDirectory { get => _isDirectory; set { _isDirectory = value; OnPropertyChanged(); } }
    public string FullPath { get; set; } = "";

    public string SizeFormatted => IsDirectory ? "<DIR>" : Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{Size / (1024.0 * 1024):F1} MB",
        _ => $"{Size / (1024.0 * 1024 * 1024):F2} GB"
    };

    public string ModifiedFormatted => Modified == DateTime.MinValue ? "" : Modified.ToString("yyyy-MM-dd HH:mm");

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
