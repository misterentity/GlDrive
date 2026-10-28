using System.Windows;
using GlDrive.Irc;

namespace GlDrive.UI;

public partial class IrcSearchWindow : Window
{
    private readonly IrcService _service;
    private CancellationTokenSource? _search;
    private bool _closed;
    public IrcSearchWindow(IrcService service)
    {
        InitializeComponent();
        _service = service;
        ServerLabel.Text = service.ServerName;
        Closed += (_, _) => { _closed = true; _search?.Cancel(); ResultsGrid.ItemsSource = null; };
        Loaded += (_, _) => PatternBox.Focus();
    }
    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (_search != null) return;
        using var cancellation = new CancellationTokenSource();
        _search = cancellation;
        SearchButton.IsEnabled = false; CancelButton.IsEnabled = true;
        ResultsGrid.ItemsSource = null; StatusLabel.Text = "Searching…";
        try
        {
            var result = await _service.SearchHistoryAsync(PatternBox.Text, IncludePrivateBox.IsChecked == true, cancellation.Token);
            if (_closed) return;
            ResultsGrid.ItemsSource = result.Matches;
            StatusLabel.Text = $"{result.Matches.Count} matches; {result.FilesRead} log files. " + (result.Status switch
            {
                "complete" => "Search complete.",
                "result-limit" => "Result limit reached; narrow the expression.",
                "pattern-timeout" => "Expression took too long; simplify it. Partial results shown.",
                "time-budget" => "Search time limit reached. Partial results shown.",
                "invalid-pattern" => "Enter a valid regular expression (at most 512 characters).",
                "invalid-server" => "This server cannot be searched.",
                _ => "Some history could not be read. Partial results shown."
            });
        }
        catch (OperationCanceledException) { if (!_closed) StatusLabel.Text = "Search cancelled."; }
        catch { if (!_closed) StatusLabel.Text = "Search unavailable. IRC continues normally."; }
        finally
        {
            _search = null;
            if (!_closed) { SearchButton.IsEnabled = true; CancelButton.IsEnabled = false; }
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _search?.Cancel();
}
