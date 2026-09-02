using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Backtrack.Core;
using Backtrack.Pairing;

namespace Backtrack;

public partial class MainWindow : Window
{
    private Border BuildRecentRemoteClipTile(string relativePath, RemoteGalleryFile file)
    {
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };
        var thumbGrid = new Grid();
        thumbGrid.Children.Add(thumbImage);

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
            Margin = new Thickness(4, 0, 4, 0)
        };
        var compressText = new TextBlock
        {
            Text = "Compressing 0%",
            FontWeight = FontWeights.Bold,
            FontSize = 9.5,
            Foreground = (Brush)FindResource("Text0"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var progressBarTrack = new Border
        {
            Height = 3,
            Width = 72,
            Background = (Brush)FindResource("SeekTrackBg"),
            CornerRadius = new CornerRadius(1.5),
            ClipToBounds = true
        };
        var progressBarFill = new Border
        {
            Height = 3,
            Width = 0,
            Background = (Brush)FindResource("Accent"),
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(1.5)
        };
        progressBarTrack.Child = progressBarFill;
        compressStack.Children.Add(compressText);
        compressStack.Children.Add(progressBarTrack);
        compressOverlay.Children.Add(compressStack);
        thumbGrid.Children.Add(compressOverlay);

        if (_activeCompressingClips.TryGetValue(relativePath, out double initProg) ||
            _activeCompressingClips.TryGetValue(file.Name, out initProg))
        {
            compressOverlay.Visibility = Visibility.Visible;
            compressText.Text = $"Compressing {(int)Math.Round(initProg * 100)}%";
            progressBarFill.Width = Math.Max(2, initProg * 72.0);
        }
        else if (_activeMergingClips.TryGetValue(relativePath, out double initMergeProg) ||
                 _activeMergingClips.TryGetValue(file.Name, out initMergeProg))
        {
            compressOverlay.Visibility = Visibility.Visible;
            compressText.Text = $"Merging {(int)Math.Round(initMergeProg * 100)}%";
            progressBarFill.Width = Math.Max(2, initMergeProg * 72.0);
        }

        var thumb = new Border { Background = (Brush)FindResource("ThumbnailBg"), Width = 96, Height = 64, Cursor = Cursors.Hand, ClipToBounds = true, Child = thumbGrid };
        thumb.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenRemoteClipStreaming(relativePath, file);
        };

        Action<string, double> progressHandler = (targetPath, prog) =>
        {
            if (string.Equals(targetPath, relativePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetPath, file.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(targetPath), file.Name, StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    compressOverlay.Visibility = Visibility.Visible;
                    compressText.Text = $"Compressing {(int)Math.Round(prog * 100)}%";
                    progressBarFill.Width = Math.Max(2, prog * 72.0);
                });
            }
        };
        ClipCompressionProgressChanged += progressHandler;

        Action<string, double> mergeProgressHandler = (targetPath, prog) =>
        {
            if (string.Equals(targetPath, relativePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetPath, file.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(targetPath), file.Name, StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    compressOverlay.Visibility = Visibility.Visible;
                    compressText.Text = $"Merging {(int)Math.Round(prog * 100)}%";
                    progressBarFill.Width = Math.Max(2, prog * 72.0);
                });
            }
        };
        ClipMergeProgressChanged += mergeProgressHandler;

        Action<string> completeHandler = (targetPath) =>
        {
            if (string.Equals(targetPath, relativePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetPath, file.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(targetPath), file.Name, StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    compressOverlay.Visibility = Visibility.Collapsed;
                    RefreshRecentClipsOverlay();
                });
            }
        };
        ClipCompressionCompleted += completeHandler;
        ClipMergeCompleted += completeHandler;

        thumb.Unloaded += (_, _) =>
        {
            ClipCompressionProgressChanged -= progressHandler;
            ClipMergeProgressChanged -= mergeProgressHandler;
            ClipCompressionCompleted -= completeHandler;
            ClipMergeCompleted -= completeHandler;
        };

        var title = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 96,
            Margin = new Thickness(0, 4, 0, 0),
            Cursor = Cursors.IBeam,
            ToolTip = "Double-click to rename",
        };
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                BeginRecentRemoteClipRename(title, relativePath, file);
            }
        };

        DateTime modified = file.Modified.ToLocalTime();
        string dateText = modified.Date == DateTime.Today ? modified.ToString("h:mm tt") : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock
        {
            Text = $"{dateText} · {FormatFileSize(file.Size)}",
            FontSize = 9.5,
            Foreground = (Brush)FindResource("Text2"),
            Width = 96,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var content = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        content.Children.Add(thumb);
        content.Children.Add(title);
        content.Children.Add(sub);

        var tile = new Border { Child = content };
        _ = LoadRemoteThumbnailAsync(relativePath, file, thumbImage);

        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var renameItem = new MenuItem { Header = "Rename", Style = (Style)FindResource("DarkMenuItem") };
        renameItem.Click += (_, _) => BeginRecentRemoteClipRename(title, relativePath, file);
        var copyPathItem = new MenuItem { Header = "Copy path", Style = (Style)FindResource("DarkMenuItem") };
        copyPathItem.Click += (_, _) => Clipboard.SetText(relativePath);
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem"), Foreground = (Brush)FindResource("Rec") };
        deleteItem.Click += (_, _) => DeleteRemoteClip(relativePath, file);
        contextMenu.Items.Add(renameItem);
        contextMenu.Items.Add(copyPathItem);
        contextMenu.Items.Add(deleteItem);
        thumb.ContextMenu = contextMenu;

        return tile;
    }
}
