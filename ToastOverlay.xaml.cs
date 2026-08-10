using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Backtrack.Interop;

namespace Backtrack;

public partial class ToastOverlay : Window
{
    // Rec/Stream/Green/Warning/Accent are brand/status accent colors, deliberately
    // IDENTICAL in both themes (see Theme.Dark.xaml's own comment on this), so
    // caching them once as static brushes is fine. PanelBg/Hairline/Text0/Text2
    // are neutrals that DO differ by theme -- toasts are built entirely in code
    // (not XAML), so they can't use DynamicResource; instead these are looked
    // up from the CURRENT theme dictionary at the moment each toast is built
    // (see ThemeBrush below), so a runtime theme swap is picked up by the next
    // toast shown rather than needing a cached brush to somehow update itself.
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x3E, 0xCF, 0x8E));
    private static readonly SolidColorBrush Rec = new(Color.FromRgb(0xFF, 0x5B, 0x52));
    private static readonly SolidColorBrush Stream = new(Color.FromRgb(0xA8, 0x55, 0xF7));
    private static readonly SolidColorBrush Warning = new(Color.FromRgb(0xF0, 0xA0, 0x20));
    private static readonly SolidColorBrush Accent = new(Color.FromRgb(0x3E, 0xCF, 0x8E));

    private static Brush PanelBg => ThemeBrush("PanelBg");
    private static Brush Hairline => ThemeBrush("Hairline");
    private static Brush Text0 => ThemeBrush("Text0");
    private static Brush Text2 => ThemeBrush("Text2");

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    private int _activeUndoCount;
    private bool _overlayActive;

    public ToastOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdatePosition(false);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            ClickThrough.Enable(hwnd);
            ToolWindow.Enable(hwnd);
        };
    }

    public void UpdatePosition(bool overlayActive)
    {
        _overlayActive = overlayActive;
        Rect bounds = DisplayMonitors.ResolveBoundsDiu(AppSettings.Load().DisplayDeviceName);
        Left = bounds.X + 12;
        Top = bounds.Y + (overlayActive ? 58 : 14);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            WindowZOrder.BringToFrontWithoutActivating(hwnd);
        }
    }

    public void ShowRecording(bool started, string? resolvedPath)
    {
        // A real shape instead of a bigger text glyph for the "started" dot --
        // matching the record tile's own Ellipse gives pixel-exact size and
        // centering, where a bigger font glyph (tried first) doesn't reliably
        // center against a TextBlock's line and inflates the row's own height
        // to fit its larger em-box. Explicit Width/Height keeps it fixed at 10px
        // regardless of the row's height, so it can't grow the toast at all.
        UIElement icon = started
            ? new Ellipse { Width = 10, Height = 10, Fill = Rec, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }
            : GlyphIcon("\u25a0", Text2);
        Show(icon, started ? Rec : Text2, started ? "Recording started" : "Recording saved",
            resolvedPath is null ? null : $"Saved at '{resolvedPath}'");
    }

    /// <summary>See ObsService.EncoderOverloadDetected -- summary is a plain-English list of what's actually dropping frames right now (already built by the caller, this just displays it).</summary>
    public void ShowEncoderOverload(string summary) =>
        Show(GlyphIcon("⚠", Warning), Warning, "Encoder overloaded", summary);

    public void ShowStreaming(bool started)
    {
        // Same real-Ellipse-for-the-started-dot reasoning as ShowRecording above.
        UIElement icon = started
            ? new Ellipse { Width = 10, Height = 10, Fill = Stream, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }
            : GlyphIcon("■", Text2);
        Show(icon, started ? Stream : Text2, started ? "Livestream Started" : "Livestream Ended", null);
    }

    public void ShowReplaySaved(string label, string resolvedPath) =>
        Show(GlyphIcon("\u21bb", Green), Green, $"{label} saved", $"Saved at '{resolvedPath}'");

    public void ShowUpdateApplied(string component, string version) =>
        Show(GlyphIcon("\u2b06", Green), Green, $"{component} updated", $"Now on version {version}");

    public void ShowAppStarted(string hotkeyText) =>
        Show(GlyphIcon("\u21bb", Accent), Accent, "Backtrack is running", $"Press {hotkeyText} to open the overlay");

    // Fired right before the download+install actually starts (which can take
    // a while and, for a plugin, closes and relaunches OBS along the way) so
    // it doesn't look like the app just silently glitched or hung.
    public void ShowUpdateInProgress(string component) =>
        Show(GlyphIcon("\u2b07", Accent), Accent, $"Updating {component}...", "Downloading and installing in the background");

    private static TextBlock GlyphIcon(string glyph, Brush color) => new()
    {
        Text = glyph,
        FontSize = 14,
        Foreground = color,
        Margin = new Thickness(0, 1, 10, 0),
        VerticalAlignment = VerticalAlignment.Top,
    };

    /// <summary>Shows a 5-second toast with sliding status indicator and Undo button; calls onExpire only if not undone.</summary>
    public void ShowDeleteUndo(string clipName, Action onExpire, Action? onUndo = null) =>
        ShowDeleteUndoToast("Clip deleted", clipName, onExpire, onUndo);

    /// <summary>
    /// Same idea as ShowDeleteUndo, one toast for a whole batch instead of one
    /// per clip -- deleting several clips at once used to fire ShowDeleteUndo
    /// in a loop, stacking that many separate toasts (each with its own
    /// 60fps DispatcherTimer for the progress bar), which was the actual
    /// cause of the reported slowdown, not just visual clutter.
    /// </summary>
    public void ShowMultiDeleteUndo(int count, Action onExpire, Action? onUndo = null) =>
        ShowDeleteUndoToast("Multi Deletion", $"{count} clips deleted", onExpire, onUndo);

    private void ShowDeleteUndoToast(string title, string subtitle, Action onExpire, Action? onUndo)
    {
        _activeUndoCount++;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        ClickThrough.Disable(hwnd);
        WindowZOrder.BringToFrontWithoutActivating(hwnd);

        // Same Material "delete" icon as the Player screen's own Delete
        // button, not the old Segoe MDL2 Assets trash glyph -- that one
        // looked visually inconsistent with the rest of the icon set.
        var iconBlock = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M6,19c0,1.1,0.9,2,2,2h8c1.1,0,2,-0.9,2,-2V7H6V19zM19,4h-3.5l-1,-1h-5l-1,1H5v2h14V4z"),
            Fill = Rec,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 1, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var msg = new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 12.5, Foreground = Text0, HorizontalAlignment = HorizontalAlignment.Left };
        var sub = new TextBlock { Text = subtitle, FontSize = 10.5, Foreground = Text2, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(msg);
        body.Children.Add(sub);

        var undoButton = new Button
        {
            Content = "Undo",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Text0,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Cursor = Cursors.Hand,
            Padding = new Thickness(8, 4, 8, 4),
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var mainRow = new Grid();
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mainRow.ColumnDefinitions.Add(new ColumnDefinition());
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(iconBlock, 0);
        Grid.SetColumn(body, 1);
        Grid.SetColumn(undoButton, 2);
        mainRow.Children.Add(iconBlock);
        mainRow.Children.Add(body);
        mainRow.Children.Add(undoButton);

        // Sliding Status Indicator (Progress Bar at Bottom)
        var progressTrack = new Grid { Height = 3, Background = ThemeBrush("BorderMedium"), VerticalAlignment = VerticalAlignment.Bottom };
        var progressFill = new Border { Background = Rec, HorizontalAlignment = HorizontalAlignment.Left };
        progressTrack.Children.Add(progressFill);

        var cardContent = new StackPanel();
        cardContent.Children.Add(new Border { Padding = new Thickness(12, 10, 10, 10), Child = mainRow });
        cardContent.Children.Add(progressTrack);

        var toast = new Border
        {
            Background = PanelBg,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Margin = new Thickness(0, 0, 0, 8),
            ClipToBounds = true,
            Child = cardContent,
        };

        ToastStack.Children.Insert(0, toast);

        const double durationSec = 5.0;
        var startTime = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // 60 FPS update

        timer.Tick += (_, _) =>
        {
            double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            double progress = Math.Clamp(1.0 - (elapsed / durationSec), 0.0, 1.0);
            progressFill.Width = progress * toast.ActualWidth;

            if (progress <= 0)
            {
                timer.Stop();
                Finish();
                onExpire();
            }
        };

        undoButton.Click += (_, _) =>
        {
            timer.Stop();
            Finish();
            onUndo?.Invoke();
        };

        void Finish()
        {
            ToastStack.Children.Remove(toast);
            _activeUndoCount--;
            if (_activeUndoCount <= 0)
            {
                _activeUndoCount = 0;
                ClickThrough.Enable(new WindowInteropHelper(this).Handle);
            }
        }

        toast.SizeChanged += (_, _) => progressFill.Width = toast.ActualWidth;
        timer.Start();
    }

    private void Show(UIElement icon, Brush accentColor, string message, string? subMessage)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            WindowZOrder.BringToFrontWithoutActivating(hwnd);
        }

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
        row.Children.Add(icon);
        row.Children.Add(body);

        // Sliding Status Indicator (Progress Bar at Bottom)
        var progressTrack = new Grid { Height = 3, Background = ThemeBrush("BorderMedium"), VerticalAlignment = VerticalAlignment.Bottom };
        var progressFill = new Border { Background = accentColor, HorizontalAlignment = HorizontalAlignment.Left };
        progressTrack.Children.Add(progressFill);

        var cardContent = new StackPanel();
        cardContent.Children.Add(new Border { Padding = new Thickness(12, 10, 14, 10), Child = row });
        cardContent.Children.Add(progressTrack);

        var toast = new Border
        {
            Background = PanelBg,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Margin = new Thickness(0, 0, 0, 8),
            ClipToBounds = true,
            Child = cardContent,
        };

        ToastStack.Children.Insert(0, toast);

        const double durationSec = 4.0;
        var startTime = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // 60 FPS

        timer.Tick += (_, _) =>
        {
            double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            double progress = Math.Clamp(1.0 - (elapsed / durationSec), 0.0, 1.0);
            progressFill.Width = progress * toast.ActualWidth;

            if (progress <= 0)
            {
                timer.Stop();
                ToastStack.Children.Remove(toast);
            }
        };

        toast.SizeChanged += (_, _) => progressFill.Width = toast.ActualWidth;
        timer.Start();
    }
}
