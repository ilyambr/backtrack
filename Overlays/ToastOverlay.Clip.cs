using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Backtrack.Core;
using Backtrack.Interop;

namespace Backtrack;

public partial class ToastOverlay : Window
{
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
}
