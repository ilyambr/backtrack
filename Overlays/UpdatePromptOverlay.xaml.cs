using System;
using System.Windows;
using System.Windows.Interop;
using Backtrack.Interop;

namespace Backtrack;

public partial class UpdatePromptOverlay : Window
{
    private Action? _onInstall;

    public UpdatePromptOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) => ToolWindow.Enable(new WindowInteropHelper(this).Handle);
        SizeChanged += (_, _) => Reposition();
    }

    private void Reposition()
    {
        Rect bounds = DisplayMonitors.ResolveBoundsDiu(AppSettings.Load().DisplayDeviceName);
        Left = bounds.X + 12;
        Top = bounds.Y + bounds.Height - ActualHeight - 12;
    }

    public void ShowPrompt(string componentDisplayName, Action onInstall)
    {
        BodyText.Text = $"A new update is available for \"{componentDisplayName}\", would you like to install it?";
        _onInstall = onInstall;
        Reposition();
        Show();
        WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(this).Handle);
    }

    public void HidePrompt()
    {
        _onInstall = null;
        Hide();
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        Action? action = _onInstall;
        HidePrompt();
        action?.Invoke();
    }
}
