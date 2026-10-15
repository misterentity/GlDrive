using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace GlDrive.UI;

/// <summary>
/// A toggle chip whose checked state belongs to the view model. Click, Space and the UI
/// Automation Toggle pattern all run <see cref="ToggleCommand"/> exactly once and never flip
/// IsChecked locally; a one-way IsChecked binding then reflects the filter the command chose.
/// (A plain ToggleButton would toggle itself and then also run its Command, and its automation
/// Toggle skips the Command entirely.)
/// </summary>
public class FilterChip : ToggleButton
{
    public static readonly DependencyProperty ToggleCommandProperty = DependencyProperty.Register(
        nameof(ToggleCommand), typeof(ICommand), typeof(FilterChip));

    public static readonly DependencyProperty ToggleCommandParameterProperty = DependencyProperty.Register(
        nameof(ToggleCommandParameter), typeof(object), typeof(FilterChip));

    public ICommand? ToggleCommand
    {
        get => (ICommand?)GetValue(ToggleCommandProperty);
        set => SetValue(ToggleCommandProperty, value);
    }

    public object? ToggleCommandParameter
    {
        get => GetValue(ToggleCommandParameterProperty);
        set => SetValue(ToggleCommandParameterProperty, value);
    }

    protected override void OnToggle()
    {
        var command = ToggleCommand;
        var parameter = ToggleCommandParameter;
        if (command?.CanExecute(parameter) == true)
            command.Execute(parameter);
    }
}

/// <summary>
/// The Search tab's chip semantics: quality and source are each exclusive (choosing the active
/// value again clears it), size toggles, and "clear" resets everything.
/// </summary>
public static class SearchFilterToggle
{
    public readonly record struct State(string Quality, string Source, bool BigSize)
    {
        public bool AnyActive => Quality.Length > 0 || Source.Length > 0 || BigSize;
    }

    public static State Apply(State current, string? arg)
    {
        if (string.IsNullOrEmpty(arg)) return current;
        if (arg == "clear") return new State("", "", false);
        var idx = arg.IndexOf(':');
        if (idx < 0) return current;
        var kind = arg[..idx];
        var value = arg[(idx + 1)..];
        return kind switch
        {
            "quality" => current with { Quality = current.Quality == value ? "" : value },
            "source" => current with { Source = current.Source == value ? "" : value },
            "size" => current with { BigSize = !current.BigSize },
            _ => current
        };
    }

    public const long BigSizeBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>The Search grid's row predicate for a filter state.</summary>
    public static bool Matches(State state, string? releaseName, long size)
    {
        var name = releaseName ?? "";
        if (state.Quality.Length > 0 && !name.Contains(state.Quality, StringComparison.OrdinalIgnoreCase)) return false;
        if (state.Source.Length > 0 && !name.Contains(state.Source, StringComparison.OrdinalIgnoreCase)) return false;
        return !state.BigSize || size >= BigSizeBytes;
    }
}

/// <summary>
/// The Search tab's filtered view over its result collection, and how per-site results are
/// published into it while the filter stays active.
/// </summary>
public static class SearchResultsView
{
    /// <summary>
    /// Filters the collection's default view (the one the bound DataGrid uses) by the current
    /// chip state. Callers refresh the returned view when the state changes.
    /// </summary>
    public static ICollectionView Attach(ObservableCollection<SearchResultVm> results, Func<SearchFilterToggle.State> state)
    {
        var view = CollectionViewSource.GetDefaultView(results);
        view.Filter = obj => obj is SearchResultVm vm && SearchFilterToggle.Matches(state(), vm.ReleaseName, vm.Size);
        return view;
    }

    /// <summary>
    /// Adds one site's results. Plain adds: the filtered view places each item as it arrives and
    /// keeps the current item and selection. Wrapping these adds in DeferRefresh threw
    /// "Cannot change or check the contents or Current position of CollectionView while Refresh
    /// is being deferred" in production, failing the whole search.
    /// </summary>
    public static void Publish(ObservableCollection<SearchResultVm> results, IEnumerable<SearchResultVm> batch)
    {
        foreach (var result in batch) results.Add(result);
    }
}
