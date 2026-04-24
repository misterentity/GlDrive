using System.Windows.Controls;

namespace GlDrive.UI;

public partial class AgentView : UserControl
{
    public AgentView()
    {
        InitializeComponent();
        DataContext = new AgentViewModel();
    }
}
