using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;
using Backtrack.Updates;
using Microsoft.Win32;

namespace Backtrack;

public partial class MainWindow : Window
{
    private Border BuildRecentClipTile(FileInfo file)
    {
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };
        var thumbGrid = new Grid();
        thumbGrid.Children.Add(thumbImage);

        // Compression progress overlay
        var compressOverlay = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x12, 0x14, 0x1A)),
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

        if (_activeCompressingClips.TryGetValue(file.FullName, out double initProg) ||
            _activeCompressingClips.TryGetValue(file.Name, out initProg))
        {
            compressOverlay.Visibility = Visibility.Visible;
            compressText.Text = $"Compressing {(int)Math.Round(initProg * 100)}%";
            progressBarFill.Width = Math.Max(2, initProg * 72.0);
        }

        Point? dragStart = null;
        bool isDragging = false;
        ImageSource? dragThumbSource = null;

        var thumb = new Border { Background = (Brush)FindResource("ThumbnailBg"), Width = 96, Height = 64, Cursor = Cursors.Hand, ClipToBounds = true, Child = thumbGrid };

        thumb.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragStart = e.GetPosition(null);
            isDragging = false;
            dragThumbSource = thumbImage.Source;
        };

        thumb.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && dragStart.HasValue && !isDragging)
            {
                Point currentPos = e.GetPosition(null);
                Vector diff = dragStart.Value - currentPos;
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (File.Exists(file.FullName))
                    {
                        isDragging = true;

                        try
                        {
                            ShellDragHelper.DoFileDragDrop(thumb, new[] { file.FullName }, dragThumbSource, Path.GetFileNameWithoutExtension(file.Name));
                        }
                        finally
                        {
                            isDragging = false;
                            dragStart = null;
                        }
                    }
                }
            }
        };

        thumb.PreviewMouseLeftButtonUp += (_, _) =>
        {
            dragStart = null;
        };

        thumb.MouseLeftButtonUp += (_, e) =>
        {
            if (isDragging) return;
            e.Handled = true;
            ShowMainWindowAndOpenInPlayer(file);
        };

        Action<string, double> progressHandler = (targetPath, prog) =>
        {
            if (string.Equals(targetPath, file.FullName, StringComparison.OrdinalIgnoreCase) ||
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

        Action<string> completeHandler = (targetPath) =>
        {
            if (string.Equals(targetPath, file.FullName, StringComparison.OrdinalIgnoreCase) ||
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

        thumb.Unloaded += (_, _) =>
        {
            ClipCompressionProgressChanged -= progressHandler;
            ClipCompressionCompleted -= completeHandler;
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
                BeginRecentClipRename(title, file);
            }
        };

        DateTime modified = file.LastWriteTime;
        string dateText = modified.Date == DateTime.Today ? modified.ToString("h:mm tt") : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock
        {
            Text = $"{dateText} · {FormatFileSize(file.Length)}",
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
        _ = LoadThumbnailAndPruneIfGlitchedAsync(file, thumbImage, tile);

        
        
        
        
        
        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var openFolderItem = new MenuItem { Header = "Open file location", Style = (Style)FindResource("DarkMenuItem") };
        openFolderItem.Click += (_, _) => RevealInExplorerAndClose(file.FullName);
        var renameItem = new MenuItem { Header = "Rename", Style = (Style)FindResource("DarkMenuItem") };
        renameItem.Click += (_, _) => BeginRecentClipRename(title, file);
        var copyPathItem = new MenuItem { Header = "Copy path", Style = (Style)FindResource("DarkMenuItem") };
        copyPathItem.Click += (_, _) => Clipboard.SetText(file.FullName);
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem"), Foreground = (Brush)FindResource("Rec") };
        deleteItem.Click += (_, _) => QueueDeleteWithUndo(file);
        contextMenu.Items.Add(openFolderItem);
        contextMenu.Items.Add(renameItem);
        contextMenu.Items.Add(copyPathItem);
        contextMenu.Items.Add(deleteItem);
        thumb.ContextMenu = contextMenu;

        return tile;
    }


    private void BeginRecentClipRename(TextBlock title, FileInfo file)
    {
        if (title.Parent is not Panel parent)
            return;
        int index = parent.Children.IndexOf(title);
        if (index < 0)
            return;

        bool finished = false;

        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 10.5,
            Width = 96,
            Margin = title.Margin,
            Background = (Brush)FindResource("RowBg"),
            Foreground = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, box);

        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (!finished) { finished = true; CommitRename(); } }
            else if (e.Key == Key.Escape) { e.Handled = true; if (!finished) { finished = true; RestoreTitle(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; CommitRename(); } };

        void RestoreTitle()
        {
            int i = parent.Children.IndexOf(box);
            if (i >= 0)
            {
                parent.Children.RemoveAt(i);
                parent.Children.Insert(i, title);
            }
        }

        void CommitRename()
        {
            string newName = box.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != Path.GetFileNameWithoutExtension(file.Name))
            {
                try
                {
                    string newPath = Path.Combine(file.DirectoryName!, newName + file.Extension);
                    File.Move(file.FullName, newPath);
                    title.Text = newName;
                    RefreshRecentClipsOverlay();
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        LoadGallery();
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Couldn't rename: {ex.Message}", "Backtrack");
                }
            }
            RestoreTitle();
        }
    }


    private void BeginRecentRemoteClipRename(TextBlock title, string relativePath, RemoteGalleryFile file)
    {
        if (title.Parent is not Panel parent)
            return;
        int index = parent.Children.IndexOf(title);
        if (index < 0)
            return;

        bool finished = false;

        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 10.5,
            Width = 96,
            Margin = title.Margin,
            Background = (Brush)FindResource("RowBg"),
            Foreground = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, box);

        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (!finished) { finished = true; CommitRemoteRename(); } }
            else if (e.Key == Key.Escape) { e.Handled = true; if (!finished) { finished = true; RestoreTitle(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; CommitRemoteRename(); } };

        void RestoreTitle()
        {
            int i = parent.Children.IndexOf(box);
            if (i >= 0)
            {
                parent.Children.RemoveAt(i);
                parent.Children.Insert(i, title);
            }
        }

        async void CommitRemoteRename()
        {
            string newName = box.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != Path.GetFileNameWithoutExtension(file.Name))
            {
                (bool success, string? error, _) = await _pairing.RenameRemoteClipAsync(relativePath, newName);
                if (success)
                {
                    title.Text = newName;
                    _ = RefreshRecentClipsOverlayRemoteAsync();
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        LoadGallery();
                    return;
                }
                else
                {
                    MessageBox.Show(this, $"Couldn't rename: {error}", "Backtrack");
                }
            }
            RestoreTitle();
        }
    }


        private async Task LoadThumbnailAndPruneIfGlitchedAsync(FileInfo file, Image thumbImage, Border tile)
    {
        await LoadThumbnailAsync(file, thumbImage);
        if (TryGetCachedDurationMs(file) is < 2000)
            RefreshRecentClipsOverlay();
    }
}
