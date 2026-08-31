using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;

namespace Backtrack;

public partial class ToastOverlay : Window
{
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x3E, 0xCF, 0x8E));
    private static readonly SolidColorBrush Rec = new(Color.FromRgb(0xFF, 0x5B, 0x52));
    private static readonly SolidColorBrush Stream = new(Color.FromRgb(0xA8, 0x55, 0xF7));
    private static readonly SolidColorBrush Warning = new(Color.FromRgb(0xF0, 0xA0, 0x20));
    private static readonly SolidColorBrush Accent = new(Color.FromRgb(0x3E, 0xCF, 0x8E));
    private static readonly SolidColorBrush Grey = new(Color.FromRgb(0xAE, 0xB4, 0xBD));

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
        if (!started)
        {
            AudioCues.PlayRecordingSaved();
        }

        UIElement icon = started
            ? new Ellipse { Width = 10, Height = 10, Fill = Rec, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }
            : GlyphIcon("\u25a0", Text2);
        Show(icon, started ? Rec : Text2, started ? "Recording started" : "Recording saved",
            resolvedPath is null ? null : $"Saved at '{resolvedPath}'", truncateSubMessage: true);
    }

    public void ShowEncoderOverload(string summary) =>
        Show(GlyphIcon("⚠", Warning), Warning, "Encoder overloaded", summary);

    public void ShowStorageLimitWarning(string? summary = null) =>
        Show(GlyphIcon("⚠", Warning), Warning, "Storage limit reached", summary ?? "Free up space or raise the limit in Settings.");

    public void ShowRemotePcDisconnected(string ip)
    {
        var icon = new Path
        {
            Data = Geometry.Parse("F1 M256 32c14.2 0 27.3 7.5 34.5 19.8l216 368c7.3 12.4 7.3 27.7 .2 40.1S486.3 480 472 480L40 480c-14.3 0-27.6-7.7-34.7-20.1s-7-27.8 .2-40.1l216-368C228.7 39.5 241.8 32 256 32zm0 128c-13.3 0-24 10.7-24 24l0 112c0 13.3 10.7 24 24 24s24-10.7 24-24l0-112c0-13.3-10.7-24-24-24zm32 224a32 32 0 1 0 -64 0 32 32 0 1 0 64 0z"),
            Fill = Rec,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 1, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Show(icon, Rec, "Remote PC Disconnected", $"{ip} lost connection.", durationSec: 10.0);
    }

    public void ShowStreaming(bool started)
    {
        UIElement icon = started
            ? new Ellipse { Width = 10, Height = 10, Fill = Stream, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }
            : GlyphIcon("■", Text2);
        Show(icon, started ? Stream : Text2, started ? "Livestream Started" : "Livestream Ended", null);
    }

    public void ShowRecordingCancelled(string? name = null, string? duration = null)
    {
        string subMessage;
        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(duration))
            subMessage = $"{name} · {duration} discarded";
        else if (!string.IsNullOrEmpty(name))
            subMessage = $"{name} · discarded";
        else if (!string.IsNullOrEmpty(duration))
            subMessage = $"{duration} discarded";
        else
            subMessage = "Recording discarded";

        Show(GlyphIcon("✕", Rec), Rec, "Recording Canceled", subMessage);
    }

    public void ShowReplaySaved(string label, string resolvedPath, string? customSubMessage = null)
    {
        AudioCues.PlayClipSaved();
        string sub = customSubMessage ?? (string.IsNullOrEmpty(resolvedPath) ? null : $"Saved at '{resolvedPath}'")!;
        Show(GlyphIcon("\u21bb", Green), Green, $"{label} saved", sub, truncateSubMessage: true);
    }

    public void ShowTrimSaved(string? resolvedPath)
    {
        AudioCues.PlayClipSaved();
        var icon = new Path
        {
            Data = Geometry.Parse("M9.64,7.64c0.23,-0.5,0.36,-1.05,0.36,-1.64c0,-2.21,-1.79,-4,-4,-4S2,3.79,2,6s1.79,4,4,4c0.59,0,1.14,-0.13,1.64,-0.36L10,12l-2.36,2.36C7.14,14.13,6.59,14,6,14c-2.21,0,-4,1.79,-4,4s1.79,4,4,4s4,-1.79,4,-4c0,-0.59,-0.13,-1.14,-0.36,-1.64L12,14l7,7h3v-1L9.64,7.64zM6,8C4.9,8,4,7.1,4,6s0.9,-2,2,-2s2,0.9,2,2S7.1,8,6,8zM6,20c-1.1,0,-2,-0.9,-2,-2s0.9,-2,2,-2s2,0.9,2,2S7.1,20,6,20zM12,13.5c-0.28,0,-0.5,-0.22,-0.5,-0.5s0.22,-0.5,0.5,-0.5s0.5,0.22,0.5,0.5S12.28,13.5,12,13.5zM19,3l-6,6l2,2l7,-7V3H19z"),
            Fill = Green,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 1, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Show(icon, Green, "Trim Saved", resolvedPath is null ? null : $"Saved at '{resolvedPath}'", truncateSubMessage: true);
    }

    public void ShowCompressSaved(string? resolvedPath)
    {
        AudioCues.PlayClipSaved();
        var icon = new Path
        {
            Data = Geometry.Parse("M19,9h-4V3H9v6H5l7,7 7,-7zM5,18v2h14v-2H5z"),
            Fill = Green,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 1, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Show(icon, Green, "Compression Saved", resolvedPath is null ? null : $"Saved at '{resolvedPath}'", truncateSubMessage: true);
    }

    public void ShowMergeSaved(string? resolvedPath)
    {
        AudioCues.PlayClipSaved();
        var geo1 = new EllipseGeometry(new Point(18, 18), 3, 3);
        var geo2 = new EllipseGeometry(new Point(6, 6), 3, 3);
        var geo3 = Geometry.Parse("M6,21 V9 A9,9 0 0,0 15,18");
        var group = new GeometryGroup();
        group.Children.Add(geo1);
        group.Children.Add(geo2);
        group.Children.Add(geo3);

        var icon = new Path
        {
            Data = group,
            Stroke = Green,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 1, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Show(icon, Green, "Clips Merged", resolvedPath is null ? null : $"Saved at '{resolvedPath}'", truncateSubMessage: true);
    }

    public void ShowMergeFailed(string error) =>
        Show(GlyphIcon("⚠", Warning), Warning, "Merge Failed", error);

    private readonly Dictionary<string, (Border Toast, ScaleTransform Scale)> _processingToasts = new();
    private readonly Dictionary<string, DateTime> _recentlyCompletedProcessing = new();

    public void ShowProcessingClip(string key, string label)
    {
        if (_processingToasts.ContainsKey(key))
            return;

        if (_recentlyCompletedProcessing.TryGetValue(key, out DateTime completedAt) &&
            (DateTime.UtcNow - completedAt).TotalSeconds < 3.0)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            WindowZOrder.BringToFrontWithoutActivating(hwnd);

        var msg = new TextBlock { Text = "Processing clip...", FontWeight = FontWeights.Bold, FontSize = 12.5, Foreground = Text0, TextWrapping = TextWrapping.Wrap, MaxWidth = 210 };
        var sub = new TextBlock
        {
            Text = $"Processing {label}",
            FontSize = 10.5,
            Foreground = Text2,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 210,
        };
        var body = new StackPanel();
        body.Children.Add(msg);
        body.Children.Add(sub);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(GlyphIcon("\u21bb", Grey));
        row.Children.Add(body);

        var scale = new ScaleTransform(0.0, 1.0);
        var progressTrack = new Grid { Height = 3, Background = ThemeBrush("BorderMedium"), VerticalAlignment = VerticalAlignment.Bottom };
        var progressFill = new Border
        {
            Background = Grey,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = scale
        };
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

        const double rampSec = 6.0;
        var anim = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(rampSec),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);

        _processingToasts[key] = (toast, scale);
    }

    public void CompleteProcessingClip(string key, string label, string resolvedPath, string? customSubMessage = null)
    {
        _recentlyCompletedProcessing[key] = DateTime.UtcNow;

        if (!_processingToasts.Remove(key, out var entry))
        {
            ShowReplaySaved(label, resolvedPath, customSubMessage);
            return;
        }

        const double finishSec = 0.25;
        var finishAnim = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromSeconds(finishSec),
            FillBehavior = FillBehavior.HoldEnd
        };
        finishAnim.Completed += (_, _) =>
        {
            ToastStack.Children.Remove(entry.Toast);
            ShowReplaySaved(label, resolvedPath, customSubMessage);
        };
        entry.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, finishAnim);
    }

    public void CancelProcessingClip(string key)
    {
        if (!_processingToasts.Remove(key, out var entry))
            return;
        entry.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ToastStack.Children.Remove(entry.Toast);
    }

    public void ClearAllProcessingToasts()
    {
        foreach (var entry in _processingToasts.Values)
        {
            entry.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ToastStack.Children.Remove(entry.Toast);
        }
        _processingToasts.Clear();
    }

    private readonly Dictionary<string, Border> _updateInProgressToasts = new();

    public void ShowUpdateApplied(string component, string version)
    {
        ClearUpdateInProgress(component);
        Show(GlyphIcon("\u2b06", Green), Green, $"{component} updated", $"Now on version {version}");
    }

    public void ClearUpdateInProgress(string component)
    {
        if (_updateInProgressToasts.Remove(component, out Border? existing))
            ToastStack.Children.Remove(existing);
    }

    public void ShowAppStarted(string hotkeyText) =>
        Show(GlyphIcon("\u21bb", Accent), Accent, "Backtrack is running", $"Press {hotkeyText} to open the overlay");

    public void ShowOldClipsAutoDeleted(int count, int afterDays) =>
        Show(GlyphIcon("\u2715", Warning), Warning, $"Removed {count} old clip{(count == 1 ? "" : "s")}",
            $"Older than {afterDays} day{(afterDays == 1 ? "" : "s")}, per Settings > Clips");

    public void ShowBookmarkAdded(string text) =>
        Show(GlyphIcon("★", Warning), Warning, "Bookmark added", text);

    public void ShowCompressStarted(string targetText) =>
        Show(GlyphIcon("\u21bb", Accent), Accent, "Compressing clip", targetText);

    public void ShowCompressFailed(string error) =>
        Show(GlyphIcon("\u2715", Rec), Rec, "Compression failed", error);

    public void ShowFirewallSetup() =>
        Show(GlyphIcon("\u21bb", Accent), Accent, "Setting up clip sharing",
            "Windows may ask for admin permission once, just to open two Backtrack-only network ports");

    public void ShowUpdateInProgress(string component)
    {
        ClearUpdateInProgress(component);

        var msg = new TextBlock { Text = $"Updating {component}...", FontWeight = FontWeights.Bold, FontSize = 12.5, Foreground = Text0, TextWrapping = TextWrapping.Wrap, MaxWidth = 210 };
        var sub = new TextBlock
        {
            Text = "Downloading and installing in the background",
            FontSize = 10.5,
            Foreground = Text2,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 210,
        };
        var body = new StackPanel();
        body.Children.Add(msg);
        body.Children.Add(sub);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(GlyphIcon("\u2b07", Accent));
        row.Children.Add(body);

        var toast = new Border
        {
            Background = PanelBg,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Margin = new Thickness(0, 0, 0, 8),
            ClipToBounds = true,
            Child = new Border { Padding = new Thickness(12, 10, 14, 10), Child = row },
        };

        if (!IsVisible)
        {
            Show();
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            WindowZOrder.BringToFrontWithoutActivating(hwnd);

        ToastStack.Children.Insert(0, toast);
        _updateInProgressToasts[component] = toast;
    }

    private static TextBlock GlyphIcon(string glyph, Brush color) => new()
    {
        Text = glyph,
        FontSize = 14,
        Foreground = color,
        Margin = new Thickness(0, 1, 10, 0),
        VerticalAlignment = VerticalAlignment.Top,
    };

    public void ShowDeleteUndo(string clipName, Action onExpire, Action? onUndo = null) =>
        ShowDeleteUndoToast("Clip deleted", clipName, onExpire, onUndo);

    public void ShowMultiDeleteUndo(int count, Action onExpire, Action? onUndo = null) =>
        ShowDeleteUndoToast("Multi Deletion", $"{count} clips deleted", onExpire, onUndo);

    private void ShowDeleteUndoToast(string title, string subtitle, Action onExpire, Action? onUndo)
    {
        _activeUndoCount++;

        if (!IsVisible)
        {
            Show();
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        ClickThrough.Disable(hwnd);
        if (hwnd != IntPtr.Zero)
        {
            WindowZOrder.BringToFrontWithoutActivating(hwnd);
            Dispatcher.BeginInvoke(new Action(() => WindowZOrder.BringToFrontWithoutActivating(hwnd)), DispatcherPriority.Loaded);
        }

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

        var msg = new TextBlock { Text = title, FontWeight = FontWeights.Bold, FontSize = 12.5, Foreground = Text0, HorizontalAlignment = HorizontalAlignment.Left, TextWrapping = TextWrapping.Wrap, MaxWidth = 140 };
        var sub = new TextBlock { Text = subtitle, FontSize = 10.5, Foreground = Text2, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 140, HorizontalAlignment = HorizontalAlignment.Left };
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

        var scale = new ScaleTransform(1.0, 1.0);
        var progressTrack = new Grid { Height = 3, Background = ThemeBrush("BorderMedium"), VerticalAlignment = VerticalAlignment.Bottom };
        var progressFill = new Border
        {
            Background = Rec,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = scale
        };
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
        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = TimeSpan.FromSeconds(durationSec),
            FillBehavior = FillBehavior.HoldEnd
        };
        anim.Completed += (_, _) =>
        {
            Finish();
            onExpire();
        };

        undoButton.Click += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
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

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
    }

    private void Show(UIElement icon, Brush accentColor, string message, string? subMessage, double durationSec = 4.0, bool truncateSubMessage = false)
    {
        if (!IsVisible)
        {
            Show();
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            WindowZOrder.BringToFrontWithoutActivating(hwnd);
            Dispatcher.BeginInvoke(new Action(() => WindowZOrder.BringToFrontWithoutActivating(hwnd)), DispatcherPriority.Loaded);
        }

        var msg = new TextBlock { Text = message, FontWeight = FontWeights.Bold, FontSize = 12.5, Foreground = Text0, TextWrapping = TextWrapping.Wrap, MaxWidth = 210 };
        var body = new StackPanel();
        body.Children.Add(msg);
        if (!string.IsNullOrEmpty(subMessage))
        {
            var sub = new TextBlock
            {
                Text = subMessage,
                FontSize = 10.5,
                Foreground = Text2,
                Margin = new Thickness(0, 2, 0, 0),
                MaxWidth = 210,
            };
            if (truncateSubMessage)
                sub.TextTrimming = TextTrimming.CharacterEllipsis;
            else
                sub.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(sub);
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(body);

        var scale = new ScaleTransform(1.0, 1.0);
        var progressTrack = new Grid { Height = 3, Background = ThemeBrush("BorderMedium"), VerticalAlignment = VerticalAlignment.Bottom };
        var progressFill = new Border
        {
            Background = accentColor,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = scale
        };
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

        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = TimeSpan.FromSeconds(durationSec),
            FillBehavior = FillBehavior.HoldEnd
        };
        anim.Completed += (_, _) =>
        {
            ToastStack.Children.Remove(toast);
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
    }
}
