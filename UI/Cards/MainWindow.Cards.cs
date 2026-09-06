using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Pairing;

namespace Backtrack;

public partial class MainWindow : Window
{
    private Border BuildClipCard(FileInfo file, bool isNewest = false)
    {

        var thumb = new Border
        {
            Background = (Brush)FindResource("ThumbnailBg"),
            Height = 118,
            Cursor = Cursors.Hand,
            ClipToBounds = true,
        };
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };

        var selectCircle = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("BadgeBg"),
            BorderBrush = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
        };

        bool isStarred = _settings.StarredClips.Contains(file.Name) || _settings.StarredClips.Contains(file.FullName);
        var starButton = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("BadgeBg"),
            BorderBrush = (Brush)FindResource("BadgeBorder"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            Cursor = Cursors.Hand,
            ToolTip = "Toggle Star / Favorite",
        };
        var starGlyph = new TextBlock
        {
            Text = isStarred ? "★" : "☆",
            FontSize = 13,
            Foreground = isStarred ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)) : (Brush)FindResource("Text1"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        starButton.Child = starGlyph;
        starButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ToggleStarClip(file.Name);
            bool nowStarred = _settings.StarredClips.Contains(file.Name);
            starGlyph.Text = nowStarred ? "★" : "☆";
            starGlyph.Foreground = nowStarred ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)) : (Brush)FindResource("Text1");
            if (_settings.GalleryStarredOnly)
                LoadGallery();
        };

        var thumbHost = new Grid();
        thumbHost.Children.Add(thumbImage);
        thumbHost.Children.Add(selectCircle);
        thumbHost.Children.Add(starButton);

        var compressOverlay = new Grid
        {
            Background = (Brush)FindResource("PanelBgOpaque"),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        var compressStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(14, 0, 14, 0)
        };
        var compressText = new TextBlock
        {
            Text = "Compressing 0%",
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = (Brush)FindResource("Text0"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var progressBarTrack = new Border
        {
            Height = 4,
            Width = 140,
            Background = (Brush)FindResource("SeekTrackBg"),
            CornerRadius = new CornerRadius(2),
            ClipToBounds = true
        };
        var progressBarFill = new Border
        {
            Height = 4,
            Width = 0,
            Background = (Brush)FindResource("Accent"),
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(2)
        };
        progressBarTrack.Child = progressBarFill;
        compressStack.Children.Add(compressText);
        compressStack.Children.Add(progressBarTrack);

        var compressPill = new Border
        {
            Background = (Brush)FindResource("BadgeBg"),
            BorderBrush = (Brush)FindResource("BadgeBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = compressStack
        };

        compressOverlay.Children.Add(compressPill);
        thumbHost.Children.Add(compressOverlay);

        if (_activeCompressingClips.TryGetValue(file.FullName, out double initComp) ||
            _activeCompressingClips.TryGetValue(file.Name, out initComp))
        {
            compressOverlay.Visibility = Visibility.Visible;
            compressText.Text = $"Compressing {(int)Math.Round(initComp * 100)}%";
            progressBarFill.Width = Math.Max(2, initComp * 140.0);
        }
        else if (_activeMergingClips.TryGetValue(file.FullName, out double initMerge) ||
                 _activeMergingClips.TryGetValue(file.Name, out initMerge))
        {
            compressOverlay.Visibility = Visibility.Visible;
            compressText.Text = $"Merging {(int)Math.Round(initMerge * 100)}%";
            progressBarFill.Width = Math.Max(2, initMerge * 140.0);
        }
        else if (_activeUploadingClips.TryGetValue(file.FullName, out double initUpload) ||
                 _activeUploadingClips.TryGetValue(file.Name, out initUpload))
        {
            compressOverlay.Visibility = Visibility.Visible;
            compressText.Text = $"Uploading {(int)Math.Round(initUpload * 100)}%";
            progressBarFill.Width = Math.Max(2, initUpload * 140.0);
        }

        thumb.Child = thumbHost;

        Action<string, double> compressProgressHandler = (targetPath, prog) =>
        {
            if (string.Equals(targetPath, file.FullName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetPath, file.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(targetPath), file.Name, StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    compressOverlay.Visibility = Visibility.Visible;
                    compressText.Text = $"Compressing {(int)Math.Round(prog * 100)}%";
                    progressBarFill.Width = Math.Max(2, prog * 140.0);
                });
            }
        };
        ClipCompressionProgressChanged += compressProgressHandler;

        Action<string, double> mergeProgressHandler = (targetPath, prog) =>
        {
            if (string.Equals(targetPath, file.FullName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetPath, file.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(targetPath), file.Name, StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    compressOverlay.Visibility = Visibility.Visible;
                    compressText.Text = $"Merging {(int)Math.Round(prog * 100)}%";
                    progressBarFill.Width = Math.Max(2, prog * 140.0);
                });
            }
        };
        ClipMergeProgressChanged += mergeProgressHandler;

        Action<string, double> uploadProgressHandler = (targetPath, prog) =>
        {
            if (string.Equals(targetPath, file.FullName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetPath, file.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(targetPath), file.Name, StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    compressOverlay.Visibility = Visibility.Visible;
                    compressText.Text = $"Uploading {(int)Math.Round(prog * 100)}%";
                    progressBarFill.Width = Math.Max(2, prog * 140.0);
                });
            }
        };
        DriveUploadProgressChanged += uploadProgressHandler;

        Action<string> completeHandler = (targetPath) =>
        {
            if (string.Equals(targetPath, file.FullName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetPath, file.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(targetPath), file.Name, StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    compressOverlay.Visibility = Visibility.Collapsed;
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        LoadGallery();
                });
            }
        };
        ClipCompressionCompleted += completeHandler;
        ClipMergeCompleted += completeHandler;
        DriveUploadCompleted += completeHandler;

        thumb.Unloaded += (_, _) =>
        {
            ClipCompressionProgressChanged -= compressProgressHandler;
            ClipMergeProgressChanged -= mergeProgressHandler;
            DriveUploadProgressChanged -= uploadProgressHandler;
            ClipCompressionCompleted -= completeHandler;
            ClipMergeCompleted -= completeHandler;
            DriveUploadCompleted -= completeHandler;
        };

        AttachClipCardDragDrop(thumb, thumbImage, file, () =>
        {
            if (_selectedClipPaths.Count > 0)
                ToggleClipSelected(file);
            else
                OpenInPlayer(file);
        });

        thumb.MouseEnter += (_, _) => selectCircle.Visibility = Visibility.Visible;
        thumb.MouseLeave += (_, _) =>
        {
            if (_selectedClipPaths.Count == 0)
                selectCircle.Visibility = Visibility.Collapsed;
        };
        selectCircle.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ToggleClipSelected(file);
        };

        _galleryCardSelection.Add((file, selectCircle, thumb));

        var title = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 7, 0, 1),
            Cursor = Cursors.IBeam,
        };

        DateTime modified = file.LastWriteTime;
        string subText = modified.Date == DateTime.Today
            ? modified.ToString("h:mm tt")
            : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock { Text = subText, FontSize = 11, Foreground = (Brush)FindResource("Text2") };

        var durationText = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        long? knownDurationMs = TryGetCachedDurationMs(file);
        if (knownDurationMs is long ms)
            durationText.Text = FormatDuration(ms);

        var subRow = new Grid();
        subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        subRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(sub, 0);
        Grid.SetColumn(durationText, 1);
        subRow.Children.Add(sub);
        subRow.Children.Add(durationText);

        bool isDeduplicatedClip = DeduplicationService.Instance.IsDeduplicated(file.FullName, out var dEntry) &&
            !string.IsNullOrEmpty(dEntry?.OriginClipFileName) &&
            (File.Exists(dEntry.OriginClipPath) ||
             File.Exists(Path.Combine(file.DirectoryName ?? "", dEntry.OriginClipFileName)));

        bool hasDeduplicatedChildren = !isDeduplicatedClip && DeduplicationService.Instance.GetAllRecords().Values
            .Any(r => (string.Equals(r.OriginClipPath, file.FullName, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(r.OriginClipFileName, file.Name, StringComparison.OrdinalIgnoreCase)) &&
                      (File.Exists(r.ClipPath) ||
                       File.Exists(Path.Combine(file.DirectoryName ?? "", r.ClipFileName))));

        bool isLinked = isDeduplicatedClip || hasDeduplicatedChildren;

        StackPanel? newestRow = isNewest ? WithNewestDot(title, "Newest clip") : null;
        UIElement titleRow = isLinked
            ? WithDeduplicationDot(newestRow, title)
            : (UIElement?)newestRow ?? title;

        var content = new StackPanel();
        content.Children.Add(thumb);
        content.Children.Add(titleRow);
        content.Children.Add(subRow);

        _ = LoadThumbnailAsync(file, thumbImage, knownDurationMs is null ? durationText : null);

        var card = new Border { Width = GetGalleryCardWidth(), Child = content };

        if (IsNetworkPath(_settings.ClipsFolder))
        {
            var copyBtn = new Button { Content = "Copy here", Style = (Style)FindResource("IconButton"), Margin = new Thickness(0, 6, 0, 0) };
            copyBtn.Click += async (_, _) => await CopyToThisPcAsync(file, copyBtn);
            content.Children.Add(copyBtn);
        }

        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                BeginRename(card, title, file);
        };

        AttachClipCardContextMenu(card, thumb, file);

        return card;
    }
}
