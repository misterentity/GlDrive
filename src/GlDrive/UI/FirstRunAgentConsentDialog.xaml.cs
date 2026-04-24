using System.Windows;

namespace GlDrive.UI;

public partial class FirstRunAgentConsentDialog : Window
{
    public string ModelId { get; set; } = "";

    public FirstRunAgentConsentDialog(string modelId)
    {
        ModelId = modelId;
        InitializeComponent();
        DataContext = this;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void Accept_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
}
