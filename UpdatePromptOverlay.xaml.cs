using System;
using System.Windows;
using System.Windows.Interop;
using Backtrack.Interop;

namespace Backtrack;

/// <summary>
/// Bottom-left, screen-anchored prompt shown when an update was found but
/// deferred because OBS is actively recording/streaming/replaying (see
/// ObsService.IsAnyOutputActiveAsync and its callers in MainWindow). Unlike
/// ToastOverlay/StatusOverlay, this is NOT click-through and NOT always-on --
/// it needs to receive the Install button's clicks, and MainWindow shows/hides
/// it in lockstep with its own visibility (ToggleVisible/CloseOverlay) so it
/// only ever appears while the HUD is actually open, not as a persistent nag.
/// </summary>
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

    /// <summary>Call only while MainWindow is visible -- see the class doc. onInstall runs once, from the button click, then the prompt hides itself.</summary>
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
