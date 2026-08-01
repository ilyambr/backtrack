using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CaptureCenter.Interop;

namespace CaptureCenter;

public partial class ToastOverlay : Window
{
    private static readonly SolidColorBrush PanelBg = new(Color.FromArgb(240, 20, 21, 24));
    private static readonly SolidColorBrush Hairline = new(Color.FromArgb(31, 255, 255, 255));
    private static readonly SolidColorBrush Text0 = new(Color.FromRgb(0xF5, 0xF6, 0xF8));
    private static readonly SolidColorBrush Text2 = new(Color.FromRgb(0x76, 0x7D, 0x87));
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x3E, 0xCF, 0x8E));
    private static readonly SolidColorBrush Rec = new(Color.FromRgb(0xFF, 0x5B, 0x52));

    public ToastOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Left = 12;
            Top = 60;
            ClickThrough.Enable(new WindowInteropHelper(this).Handle);
        };
    }

    public void ShowRecording(bool started) =>
        Show(started ? "●" : "■", started ? Rec : Text2, started ? "Recording started" : "Recording stopped", null);

    public void ShowReplaySaved(string label, string path) =>
        Show("↻", Green, "Replay saved", $"{label} – {System.IO.Path.GetFileName(path)}");

    private void Show(string icon, Brush iconColor, string message, string? subMessage)
    {
        var iconBlock = new TextBlock { Text = icon, FontSize = 14, Foreground = iconColor, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };

        var msg = new TextBlock { Text = message, FontWeight = FontWeights.Bold, FontSize = 12.5, Foreground = Text0 };
        var body = new StackPanel();
        body.Children.Add(msg);
        if (!string.IsNullOrEmpty(subMessage))
        {
            body.Children.Add(new TextBlock
            {
                Text = subMessage,
                FontSize = 10.5,
                Foreground = Text2,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 210,
            });
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(iconBlock);
        row.Children.Add(body);

        var toast = new Border
        {
            Background = PanelBg,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 8),
            Child = row,
        };

        ToastStack.Children.Insert(0, toast);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ToastStack.Children.Remove(toast);
        };
        timer.Start();
    }
}
