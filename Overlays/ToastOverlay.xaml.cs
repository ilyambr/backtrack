using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Backtrack.Core;
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
    private static readonly SolidColorBrush Grey = new(Color.FromRgb(0xAE, 0xB4, 0xBD)); // matches Text1 across all four themes -- "in progress, not success/error/rec/stream yet" has no color of its own otherwise

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
        if (started)
        {
            AudioCues.PlayRecordingStarted();
        }
        else
        {
            AudioCues.PlayRecordingSaved();
        }

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
            resolvedPath is null ? null : $"Saved at '{resolvedPath}'", truncateSubMessage: true);
    }

    public void ShowEncoderOverload(string summary) =>
        Show(GlyphIcon("⚠", Warning), Warning, "Encoder overloaded", summary);

    public void ShowStorageLimitWarning(string? summary = null) =>
        Show(GlyphIcon("⚠", Warning), Warning, "Storage limit reached", summary ?? "Free up space or raise the limit in Settings.");

    /// <summary>
    /// 10s, not the usual 4s -- this one means "whatever you were about to
    /// do with that PC's clips just stopped working," worth a longer look
    /// than a routine confirmation toast. Same red warning-triangle glyph
    /// as StatusOverlay's own ObsDisconnectedBadge (Google Material Icons,
    /// Apache 2.0), a real vector Path rather than GlyphIcon's Unicode
    /// character so it's a pixel-exact visual match, not just similar.
    /// </summary>
    public void ShowRemotePcDisconnected(string ip)
    {
        // Font Awesome solid "triangle-exclamation" (CC BY 4.0), not Google
        // Material's outline glyph originally used here -- see
        // StatusOverlay.xaml's ObsDisconnectedBadge comment for why (the
        // outline "!" is a hairline stroke, invisible at small sizes; this
        // one cuts it as solid negative space out of a filled triangle).
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
        // Same real-Ellipse-for-the-started-dot reasoning as ShowRecording above.
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

    public void ShowReplaySaved(string label, string resolvedPath)
    {
        AudioCues.PlayClipSaved();
        Show(GlyphIcon("\u21bb", Green), Green, $"{label} saved", $"Saved at '{resolvedPath}'", truncateSubMessage: true);
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

    // Keyed by row key, not label -- CompleteProcessingClip needs to find the
    // right toast again once ReplaySaved fires for that same key, and two
    // rows can share a label in principle (nothing enforces uniqueness on
    // obs-replay-slider's side).
    private readonly Dictionary<string, (Border Toast, Border Fill, DispatcherTimer Timer)> _processingToasts = new();
    private readonly Dictionary<string, DateTime> _recentlyCompletedProcessing = new();

    /// <summary>
    /// Fired the moment a row's Save button is clicked, before the actual work
    /// is even confirmed started -- obs-replay-slider's save_row request
    /// returns almost instantly (it just flushes the buffer), but the real
    /// trim down to the requested clip length happens afterward, on a
    /// background thread on the OBS side, with zero progress signal
    /// Backtrack can see (that background thread is C++/Qt on the OBS
    /// process, not something this app has any visibility into). So the
    /// fill bar here is a SIMULATED estimate, not real progress: eases up to
    /// 100% over ~6s (long enough for a typical short clip to genuinely
    /// finish inside that window) and then just holds there, full, for
    /// however much longer a big buffer's real trim actually needs, rather
    /// than snapping back to 0 or lying about being done. Unlike every other
    /// toast in this file, this one does NOT auto-dismiss on a timer --
    /// CompleteProcessingClip (called once the matching ReplaySaved event
    /// actually arrives) is the only thing that removes it.
    /// </summary>
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

        // Same glyph as ShowReplaySaved's own icon -- grey instead of green
        // is what marks this as "still in progress, not the success state".
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(GlyphIcon("\u21bb", Grey));
        row.Children.Add(body);

        // Fills UP toward 100%, opposite direction from Show()'s own
        // progressFill (which counts DOWN to 0 as a dismiss timer) -- same
        // visual language (a bar at the bottom of the card), different
        // meaning, so this can't just reuse Show()'s timer logic as-is.
        var progressTrack = new Grid { Height = 3, Background = ThemeBrush("BorderMedium"), VerticalAlignment = VerticalAlignment.Bottom };
        var progressFill = new Border { Background = Grey, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
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
        const double maxTimeoutSec = 20.0;
        var startTime = DateTime.UtcNow;
        double CurrentFraction() => Math.Clamp((DateTime.UtcNow - startTime).TotalSeconds / rampSec, 0.0, 1.0);
        double Eased(double t) => 1.0 - Math.Pow(1.0 - t, 3); // cubic ease-out -- settles in, doesn't just count linearly

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // 60 FPS
        timer.Tick += (_, _) =>
        {
            double elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            if (elapsed >= maxTimeoutSec)
            {
                RemoveProcessingToast(key);
                return;
            }

            double t = CurrentFraction();
            progressFill.Width = Eased(t) * toast.ActualWidth;
            if (t >= 1.0)
            {
                progressFill.Width = toast.ActualWidth;
            }
        };
        toast.SizeChanged += (_, _) => progressFill.Width = Eased(CurrentFraction()) * toast.ActualWidth;
        timer.Start();

        _processingToasts[key] = (toast, progressFill, timer);
    }

    /// <summary>
    /// Called once the matching ReplaySaved event actually arrives. Rather
    /// than yanking the processing toast out and dropping ShowReplaySaved in
    /// its place regardless of whatever percentage the simulated bar
    /// happened to be sitting at (jarring if it was caught mid-ramp, e.g. a
    /// short clip that finished before the ~6s ease-out settled), this
    /// quick-finishes that SAME bar to full over ~250ms first, then swaps to
    /// the completed toast once it visually reads as "done" rather than
    /// "interrupted".
    /// </summary>
    public void CompleteProcessingClip(string key, string label, string resolvedPath)
    {
        _recentlyCompletedProcessing[key] = DateTime.UtcNow;

        if (!_processingToasts.Remove(key, out var entry))
        {
            // No processing toast was showing for this key (e.g. an
            // OBS-hotkey-triggered save -- see ShowProcessingClip's own
            // comment) -- nothing to finish, just show the normal toast.
            ShowReplaySaved(label, resolvedPath);
            return;
        }

        entry.Timer.Stop(); // the slow ~6s ramp is done deciding this bar's width; the finish below takes over

        const double finishSec = 0.25;
        double startWidth = entry.Fill.Width;
        var startTime = DateTime.UtcNow;
        var finishTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        finishTimer.Tick += (_, _) =>
        {
            double t = Math.Clamp((DateTime.UtcNow - startTime).TotalSeconds / finishSec, 0.0, 1.0);
            double fullWidth = entry.Toast.ActualWidth;
            entry.Fill.Width = startWidth + (fullWidth - startWidth) * t;
            if (t < 1.0)
                return;

            finishTimer.Stop();
            ToastStack.Children.Remove(entry.Toast);
            ShowReplaySaved(label, resolvedPath);
        };
        finishTimer.Start();
    }

    /// <summary>Immediate removal, no finish-animation -- only for ShowProcessingClip's own re-click dedup, where there's no label/path to hand off to a completion toast anyway.</summary>
    private void RemoveProcessingToast(string key)
    {
        if (!_processingToasts.Remove(key, out var entry))
            return;
        entry.Timer.Stop();
        ToastStack.Children.Remove(entry.Toast);
    }

    public void ClearAllProcessingToasts()
    {
        foreach (var entry in _processingToasts.Values)
        {
            entry.Timer.Stop();
            ToastStack.Children.Remove(entry.Toast);
        }
        _processingToasts.Clear();
    }

    // Keyed by component name ("Replay Slider", "Source Record", "Backtrack")
    // -- same grouping idea as _processingToasts: ShowUpdateInProgress/
    // ShowUpdateApplied for the SAME component share one toast slot instead
    // of stacking as two independent toasts, and two DIFFERENT components
    // updating around the same time (see CheckForUpdatesAsync's batch) each
    // still only ever show one toast at a time, not one per call.
    private readonly Dictionary<string, Border> _updateInProgressToasts = new();

    public void ShowUpdateApplied(string component, string version)
    {
        ClearUpdateInProgress(component);
        Show(GlyphIcon("\u2b06", Green), Green, $"{component} updated", $"Now on version {version}");
    }

    /// <summary>Removes component's in-progress toast without showing anything in its place -- for a failed/aborted update, so it doesn't sit there claiming to still be updating forever.</summary>
    public void ClearUpdateInProgress(string component)
    {
        if (_updateInProgressToasts.Remove(component, out Border? existing))
            ToastStack.Children.Remove(existing);
    }

    public void ShowAppStarted(string hotkeyText) =>
        Show(GlyphIcon("\u21bb", Accent), Accent, "Backtrack is running", $"Press {hotkeyText} to open the overlay");

    /// <summary>Fired after RunAutoDeleteOldClips actually removes something -- Settings > Clips > Auto-delete old clips.</summary>
    public void ShowOldClipsAutoDeleted(int count, int afterDays) =>
        Show(GlyphIcon("\u2715", Warning), Warning, $"Removed {count} old clip{(count == 1 ? "" : "s")}",
            $"Older than {afterDays} day{(afterDays == 1 ? "" : "s")}, per Settings > Clips");

    public void ShowBookmarkAdded(string text) =>
        Show(GlyphIcon("★", Warning), Warning, "Bookmark added", text);

    public void ShowCompressStarted(string targetText) =>
        Show(GlyphIcon("\u21bb", Accent), Accent, "Compressing clip", targetText);

    public void ShowCompressFailed(string error) =>
        Show(GlyphIcon("\u2715", Rec), Rec, "Compression failed", error);

    // Fired right before App.xaml.cs kicks off FirewallRules.AddRulesElevated
    // on a brand new install -- a UAC prompt appearing with zero warning,
    // moments after someone's very first launch, reads as suspicious with no
    // context for what's asking or why. This is that context, shown first.
    public void ShowFirewallSetup() =>
        Show(GlyphIcon("\u21bb", Accent), Accent, "Setting up clip sharing",
            "Windows may ask for admin permission once, just to open two Backtrack-only network ports");

    /// <summary>
    /// Fired right before the download+install actually starts (which can
    /// take a while and, for a plugin, closes and relaunches OBS along the
    /// way) so it doesn't look like the app just silently glitched or hung.
    /// Persistent, no auto-dismiss timer like every OTHER toast here -- an
    /// install can genuinely take longer than the usual 4s, and this is
    /// meant to be replaced by ShowUpdateApplied (or cleared by
    /// ClearUpdateInProgress on failure) once this component's own update
    /// actually resolves, not to vanish on its own partway through.
    /// </summary>
    public void ShowUpdateInProgress(string component)
    {
        ClearUpdateInProgress(component); // dedupe -- a second call for the same component replaces, doesn't stack

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

    /// <summary>
    /// truncateSubMessage: wrap is the general fix (long ordinary text stays
    /// fully readable across 2 lines instead of getting cut off), but a full
    /// file path (ShowRecording/ShowReplaySaved's "Saved at '...'") is
    /// exactly the case wrapping doesn't help -- it's long by nature, wraps
    /// mid-path with no natural break point, and just makes the toast
    /// visually messy. Those two ask for the old single-line ellipsis
    /// truncation back explicitly; everything else keeps wrapping.
    /// </summary>
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
