using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlDrive.Services;

namespace GlDrive.UI;

public partial class ProblemsWindow : Window
{
    private readonly ProblemsService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private ProblemFixPreview? _preview;
    private long _changeVersion;
    private long _previewVersion = -1;
    private int _changeQueued;
    private bool _ready;
    private bool _working;
    private bool _lifetimeDisposed;
    private volatile bool _closed;

    public ProblemsWindow(ProblemsService service)
    {
        _service = service;
        InitializeComponent();
        _ready = true;
        _service.Changed += Service_Changed;
        Render(_service.Snapshot);
        Closed += (_, _) =>
        {
            _closed = true;
            _service.Changed -= Service_Changed;
            _lifetime.Cancel();
            _preview = null;
            ProblemsGrid.ItemsSource = null;
            DisposeLifetimeIfClosed();
        };
    }

    private void Service_Changed()
    {
        Interlocked.Increment(ref _changeVersion);
        if (_closed || Interlocked.Exchange(ref _changeQueued, 1) != 0) return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref _changeQueued, 0);
                if (_closed) return;
                var hadPreview = _preview != null;
                Render(_service.Snapshot);
                if (hadPreview) StatusText.Text = "Conditions changed. Preview the fix again before applying it.";
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _changeQueued, 0); }
    }

    private void Render(ProblemSnapshot snapshot)
    {
        var selectedId = (ProblemsGrid.SelectedItem as HealthProblem)?.Id;
        InvalidatePreview();
        ProblemsGrid.ItemsSource = snapshot.Problems;
        ProblemsGrid.Items.Refresh();
        ProblemsGrid.SelectedItem = snapshot.Problems.FirstOrDefault(problem => problem.Id == selectedId);
        UpdateSelection();
        CheckedAtText.Text = snapshot.CheckedAtUtc == default
            ? "Checks have not been refreshed."
            : $"Checked {snapshot.CheckedAtUtc.LocalDateTime:g}";
        StatusText.Text = snapshot.CheckedAtUtc == default
            ? "Choose Refresh checks to inspect the current conditions."
            : snapshot.StatusText();
        UpdateButtons();
    }

    private void Problem_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        InvalidatePreview();
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var problem = ProblemsGrid.SelectedItem as HealthProblem;
        ProblemTitleText.Text = problem?.Title ?? "Select a problem to review its evidence.";
        EvidenceText.Text = problem?.Evidence ?? "";
        RemedyText.Text = problem?.Remedy ?? "";
        UpdateButtons();
    }

    private void InvalidatePreview()
    {
        _preview = null;
        _previewVersion = -1;
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewDescriptionText.Text = DiffText.Text = ImpactText.Text = "";
        ApplyButton.IsEnabled = false;
    }

    private void UpdateButtons()
    {
        if (_closed) return;
        RefreshButton.IsEnabled = ProblemsGrid.IsEnabled = !_working;
        PreviewButton.IsEnabled = !_working && ProblemsGrid.SelectedItem is HealthProblem { FixAvailable: true };
        ApplyButton.IsEnabled = !_working && _preview != null && _previewVersion == Interlocked.Read(ref _changeVersion)
            && (ProblemsGrid.SelectedItem as HealthProblem)?.Id == _preview.ProblemId;
    }

    private void SetWorking(bool working)
    {
        _working = working;
        UpdateButtons();
        DisposeLifetimeIfClosed();
    }

    private void DisposeLifetimeIfClosed()
    {
        if (!_closed || _working || _lifetimeDisposed) return;
        _lifetimeDisposed = true;
        _lifetime.Dispose();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _closed) return;
        InvalidatePreview();
        SetWorking(true);
        StatusText.Text = "Checking existing state…";
        try
        {
            var snapshot = await BackgroundAsync(() => _service.RefreshAsync(_lifetime.Token));
            if (!_closed) Render(snapshot);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch { if (!_closed) StatusText.Text = "Checks could not be refreshed. Existing engine activity continues."; }
        finally { SetWorking(false); }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _closed || ProblemsGrid.SelectedItem is not HealthProblem { FixAvailable: true } problem) return;
        InvalidatePreview();
        var version = Interlocked.Read(ref _changeVersion);
        SetWorking(true);
        StatusText.Text = "Preparing the fix preview…";
        try
        {
            var preview = await BackgroundAsync(() => _service.PreviewFix(problem.Id));
            if (_closed) return;
            if (version != Interlocked.Read(ref _changeVersion) || (ProblemsGrid.SelectedItem as HealthProblem)?.Id != problem.Id
                || preview.ProblemId != problem.Id)
            {
                StatusText.Text = "Conditions changed. Refresh and preview the fix again.";
                return;
            }
            _preview = preview;
            _previewVersion = version;
            PreviewDescriptionText.Text = preview.Description;
            DiffText.Text = preview.Diff;
            ImpactText.Text = preview.Impact;
            PreviewPanel.Visibility = Visibility.Visible;
            StatusText.Text = "Review the proposed change and its impact. Apply preview makes this change.";
        }
        catch (OperationCanceledException) when (_closed) { }
        catch { if (!_closed) StatusText.Text = "A current fix preview is unavailable. Refresh the checks and try again."; }
        finally { SetWorking(false); }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_working || _closed || _preview is not { } preview) return;
        if (_previewVersion != Interlocked.Read(ref _changeVersion) || (ProblemsGrid.SelectedItem as HealthProblem)?.Id != preview.ProblemId)
        {
            InvalidatePreview();
            StatusText.Text = "Conditions changed. Preview the fix again before applying it.";
            return;
        }
        InvalidatePreview();
        SetWorking(true);
        StatusText.Text = "Applying the previewed change…";
        var applied = false;
        try
        {
            await BackgroundAsync(() => _service.ApplyFixAsync(preview, _lifetime.Token));
            applied = true;
            var snapshot = await BackgroundAsync(() => _service.RefreshAsync(_lifetime.Token));
            if (_closed) return;
            Render(snapshot);
            StatusText.Text = snapshot.StatusText(applied: true);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch
        {
            if (!_closed) StatusText.Text = applied
                ? "The change was applied, but the checks could not be refreshed. Choose Refresh checks to try again."
                : "The fix could not be completed. Refresh the checks and review a new preview.";
        }
        finally { SetWorking(false); }
    }

    private Task<T> BackgroundAsync<T>(Func<T> action)
    {
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        return Task.Run(action, _lifetime.Token);
    }

    private Task<T> BackgroundAsync<T>(Func<Task<T>> action)
    {
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        return Task.Run(action, _lifetime.Token);
    }

    private Task BackgroundAsync(Func<Task> action)
    {
        using var flow = ExecutionContext.IsFlowSuppressed() ? default : ExecutionContext.SuppressFlow();
        return Task.Run(action, _lifetime.Token);
    }
}
