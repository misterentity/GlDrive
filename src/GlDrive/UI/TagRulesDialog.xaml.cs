using System.Collections.ObjectModel;
using System.Windows;
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

        HeaderText.Text = $"Tag Rules for mapping: {mapping.IrcSection} → {mapping.RemoteSection}";

        foreach (var rule in mapping.TagRules)
            _rules.Add(rule);
        TagRulesGrid.ItemsSource = _rules;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        _rules.Add(new SkiplistRule { Action = SkiplistAction.Deny });
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (TagRulesGrid.SelectedItem is SkiplistRule r)
            _rules.Remove(r);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _mapping.TagRules = _rules.ToList();
        DialogResult = true;
    }
}
