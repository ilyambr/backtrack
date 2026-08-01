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
    private static readonly SolidColorBrush Accent = new(Color.FromRgb(0xF5, 0xF6, 0xF8));

    // The rest of this window is click-through (see ClickThrough.Enable below) so
    // it never blocks the game underneath -- but an Undo toast has a real button
    // that needs actual clicks, so click-through is toggled off while one is showing.
    private int _activeUndoCount;

    public ToastOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Left = 12;
            Top = 58;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            ClickThrough.Enable(hwnd);
            ToolWindow.Enable(hwnd);
        };
    }

    public void ShowRecording(bool started, string? resolvedPath) =>
        Show(started ? "●" : "■", started ? Rec : Text2,
            started ? "Recording started" : "Recording saved",
            resolvedPath is null ? null : $"Saved at '{resolvedPath}'");

    public void ShowReplaySaved(string label, string resolvedPath) =>
        Show("↻", Green, $"{label} saved", $"Saved at '{resolvedPath}'");

    public void ShowUpdateApplied(string component, string version) =>
        Show("⬆", Green, $"{component} updated", $"Now on version {version}");

    /// <summary>Shows a 5-second countdown toast with a real Undo button; calls onExpire only if it wasn't undone in time.</summary>
    public void ShowDeleteUndo(string clipName, Action onExpire)
    {
        _activeUndoCount++;
        ClickThrough.Disable(new WindowInteropHelper(this).Handle);

        var iconBlock = new TextBlock { Text = "🗑", FontSize = 13, Foreground = Rec, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };

        var msg = new TextBlock { Text = "Clip deleted", FontWeight = FontWeights.Bold, FontSize = 12.5, Foreground = Text0 };
        var sub = new TextBlock { Text = clipName, FontSize = 10.5, Foreground = Text2, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 };
        var body = new StackPanel();
        body.Children.Add(msg);
        body.Children.Add(sub);

        var countdownText = new TextBlock { Text = "5", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var countdownBadge = new Border { Width = 18, Height = 18, BorderBrush = Accent, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Child = countdownText };
        var undoButton = new Button
        {
            Content = "Undo",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Accent,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(8, 0, 0, 0),
            Focusable = false,
        };
        var undoPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        undoPanel.Children.Add(countdownBadge);
        undoPanel.Children.Add(undoButton);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(iconBlock, 0);
        Grid.SetColumn(body, 1);
        Grid.SetColumn(undoPanel, 2);
        row.Children.Add(iconBlock);
        row.Children.Add(body);
        row.Children.Add(undoPanel);

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

        int remaining = 5;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0)
            {
                timer.Stop();
                Finish();
                onExpire();
            }
            else
            {
                countdownText.Text = remaining.ToString();
            }
        };

        undoButton.Click += (_, _) =>
        {
            timer.Stop();
            Finish();
        };

        void Finish()
        {
            ToastStack.Children.Remove(toast);
            _activeUndoCount--;
            if (_activeUndoCount == 0)
                ClickThrough.Enable(new WindowInteropHelper(this).Handle);
        }

        timer.Start();
    }

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
