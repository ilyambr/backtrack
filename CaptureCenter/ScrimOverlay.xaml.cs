using System;
using System.Windows;
using System.Windows.Input;

namespace CaptureCenter;

public partial class ScrimOverlay : Window
{
    public event Action? Dismissed;

    public ScrimOverlay()
    {
        InitializeComponent();
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Dismissed?.Invoke();

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Dismissed?.Invoke();
}
