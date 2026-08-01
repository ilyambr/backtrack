using System.Windows;

namespace CaptureCenter;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string message, string confirmText = "Recycle")
    {
        InitializeComponent();
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>Shows the dialog centered over owner and returns true only if Confirm was clicked.</summary>
    public static bool Ask(Window owner, string message, string confirmText = "Recycle")
    {
        var dialog = new ConfirmDialog(message, confirmText) { Owner = owner };
        return dialog.ShowDialog() == true;
    }
}
