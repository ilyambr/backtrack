using System.Windows;
using System.Windows.Controls;

namespace Backtrack.UI.ReplayRecord;

public partial class SaveReplayView : UserControl
{
    public SaveReplayView()
    {
        InitializeComponent();
    }

    private MainWindow? Main => MainWindow.Instance ?? Window.GetWindow(this) as MainWindow;

    private void BackToIdle_Click(object sender, RoutedEventArgs e) => Main?.BackToIdle_Click(sender, e);
}
