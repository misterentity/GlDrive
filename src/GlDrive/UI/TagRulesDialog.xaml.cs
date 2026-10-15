using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using GlDrive.Config;

namespace GlDrive.UI;

public partial class TagRulesDialog : Window
{
    private readonly SectionMapping _mapping;
    private readonly ObservableCollection<SkiplistRule> _rules = new();

    public TagRulesDialog(SectionMapping mapping)
    {
        _mapping = mapping;
        InitializeComponent();
        ((DataGridComboBoxColumn)TagRulesGrid.Columns[3]).ItemsSource = Enum.GetValues<SkiplistAction>();

        HeaderText.Text = $"Tag Rules for mapping: {mapping.IrcSection} → {mapping.RemoteSection}";

        foreach (var rule in mapping.TagRules)
            _rules.Add(new SkiplistRule
            {
                Pattern = rule.Pattern,
                IsRegex = rule.IsRegex,
                Action = rule.Action,
                Scope = rule.Scope,
                MatchDirectories = rule.MatchDirectories,
                MatchFiles = rule.MatchFiles,
                Section = rule.Section,
                Expression = rule.Expression
            });
        TagRulesGrid.ItemsSource = _rules;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var rule = new SkiplistRule { Action = SkiplistAction.Deny };
        _rules.Add(rule);
        TagRulesGrid.SelectedItem = rule;
        TagRulesGrid.ScrollIntoView(rule);
        TagRulesGrid.Focus();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (TagRulesGrid.SelectedItem is SkiplistRule r)
            _rules.Remove(r);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TagRulesGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !TagRulesGrid.CommitEdit(DataGridEditingUnit.Row, true)) return;
        _mapping.TagRules = _rules.ToList();
        DialogResult = true;
    }
}
