using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CaptureCenter.Interop;

namespace CaptureCenter;

public partial class PairingRequestOverlay : Window
{
    private Action? _onAllow;
    private Action? _onDeny;
    private readonly DispatcherTimer _expiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _secondsRemaining;

    public PairingRequestOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) => ToolWindow.Enable(new WindowInteropHelper(this).Handle);
        _expiryTimer.Tick += (_, _) => Tick();
    }

    /// <summary>Pops up centered and focused (unlike the passive overlays, this one needs a real decision), auto-denying after 60s so an ignored request doesn't sit there forever.</summary>
    public void ShowRequest(string deviceName, string code, Action onAllow, Action onDeny)
    {
        _onAllow = onAllow;
        _onDeny = onDeny;
        DeviceNameText.Text = $"\"{deviceName}\" wants to connect to your clips";
        CodeText.Text = string.Join(" ", code.ToCharArray());

        Show();
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = (SystemParameters.PrimaryScreenHeight - ActualHeight) / 2;
        Activate();

        _secondsRemaining = 60;
        ExpiryText.Text = $"Expires in {_secondsRemaining}s";
        _expiryTimer.Start();
    }

    private void Tick()
    {
        _secondsRemaining--;
        if (_secondsRemaining <= 0)
            Deny();
        else
            ExpiryText.Text = $"Expires in {_secondsRemaining}s";
    }

    private void AllowButton_Click(object sender, RoutedEventArgs e) => Allow();

    private void DenyButton_Click(object sender, RoutedEventArgs e) => Deny();

    private void Allow()
    {
        _expiryTimer.Stop();
        Hide();
        _onAllow?.Invoke();
    }

    private void Deny()
    {
        _expiryTimer.Stop();
        Hide();
        _onDeny?.Invoke();
    }
}
