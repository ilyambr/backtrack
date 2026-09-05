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
    private Border BuildRemoteClipCard(RemoteGalleryFile file, bool isNewest = false, IReadOnlyList<RemoteGalleryFile>? allFiles = null)
    {
        var iconHost = new Border
        {
            Background = (Brush)FindResource("ThumbnailBg"),
            Height = 118,
            Cursor = Cursors.Hand,
            ClipToBounds = true,
        };

        var playGlyph = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M8,5.14V19.14L19,12.14L8,5.14Z"),
            Fill = (Brush)FindResource("Text1"),
            Width = 38,
            Height = 38,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };

        string relativePath = RemoteClipRelativePath(file.Name);
        bool isStarred = _settings.StarredClips.Contains(file.Name) || _settings.StarredClips.Contains(relativePath);
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
                _ = LoadRemoteGalleryAsync();
        };

        var iconGrid = new Grid();
        iconGrid.Children.Add(playGlyph);
        iconGrid.Children.Add(thumbImage);
        iconGrid.Children.Add(starButton);

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
        compressOverlay.Children.Add(compressStack);
        iconGrid.Children.Add(compressOverlay);

        if (_activeCompressingClips.TryGetValue(relativePath, out double initProg) ||
            _activeCompressingClips.TryGetValue(file.Name, out initProg))
        {
            compressOverlay.Visibility = Visibility.Visible;
            compressText.Text = $"Compressing {(int)Math.Round(initProg * 100)}%";
            progressBarFill.Width = Math.Max(2, initProg * 140.0);
        }
        else if (_activeMergingClips.TryGetValue(relativePath, out double initMergeProg) ||
                 _activeMergingClips.TryGetValue(file.Name, out initMergeProg))
        {
            compressOverlay.Visibility = Visibility.Visible;
            compressText.Text = $"Merging {(int)Math.Round(initMergeProg * 100)}%";
            progressBarFill.Width = Math.Max(2, initMergeProg * 140.0);
        }

        iconHost.Child = iconGrid;

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
                    progressBarFill.Width = Math.Max(2, prog * 140.0);
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
                    progressBarFill.Width = Math.Max(2, prog * 140.0);
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
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        _ = LoadRemoteGalleryAsync();
                });
            }
        };
        ClipCompressionCompleted += completeHandler;
        ClipMergeCompleted += completeHandler;

        iconHost.Unloaded += (_, _) =>
        {
            ClipCompressionProgressChanged -= progressHandler;
            ClipMergeProgressChanged -= mergeProgressHandler;
            ClipCompressionCompleted -= completeHandler;
            ClipMergeCompleted -= completeHandler;
        };

        var title = new TextBlock
        {
            Text = file.Name,
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 7, 0, 1),
            Cursor = Cursors.IBeam,
        };
        double mb = file.Size / (1024.0 * 1024.0);

        DateTime modified = file.Modified.ToLocalTime();
        string dateText = modified.Date == DateTime.Today ? modified.ToString("h:mm tt") : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock
        {
            Text = $"{dateText} · {mb:0.#} MB",
            FontSize = 11,
            Foreground = (Brush)FindResource("Text2"),
        };

        bool isDeduplicatedClip = allFiles is not null && allFiles.Any(older => IsCandidateDeduplicationPair(file, older));
        bool hasDeduplicatedChildren = allFiles is not null && allFiles.Any(newer => IsCandidateDeduplicationPair(newer, file));
        bool isLinked = isDeduplicatedClip || hasDeduplicatedChildren;

        StackPanel? newestRow = isNewest ? WithNewestDot(title, "Newest clip") : null;
        UIElement titleRow = isLinked
            ? WithDeduplicationDot(newestRow, title)
            : (UIElement?)newestRow ?? title;

        var content = new StackPanel();
        content.Children.Add(iconHost);
        content.Children.Add(titleRow);
        content.Children.Add(sub);

        var card = new Border { Width = GetGalleryCardWidth(), Child = content };

        _ = LoadRemoteThumbnailAsync(relativePath, file, thumbImage);

        bool TryGetDraggedClip(DragEventArgs e, out string draggedPath)
        {
            draggedPath = "";
            if (e.Data.GetDataPresent("BacktrackRemoteClipInfo") &&
                e.Data.GetData("BacktrackRemoteClipInfo") is RemoteGalleryFile rgf)
            {
                draggedPath = RemoteClipRelativePath(rgf.Name);
                return true;
            }
            if (e.Data.GetDataPresent("BacktrackRemoteClip") &&
                e.Data.GetData("BacktrackRemoteClip") is string brc && !string.IsNullOrWhiteSpace(brc))
            {
                draggedPath = brc;
                return true;
            }
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && !string.IsNullOrWhiteSpace(files[0]))
            {
                draggedPath = files[0];
                return true;
            }
            if (e.Data.GetDataPresent(DataFormats.Text) &&
                e.Data.GetData(DataFormats.Text) is string txt && !string.IsNullOrWhiteSpace(txt))
            {
                draggedPath = txt;
                return true;
            }
            return false;
        }

        (bool isEligible, string originRel, string dedupRel) ResolveMergePair(string dragged)
        {
            if (string.IsNullOrWhiteSpace(dragged))
                return (false, "", "");

            string draggedName = Path.GetFileName(dragged);
            string thisName = Path.GetFileName(relativePath);

            if (string.Equals(draggedName, thisName, StringComparison.OrdinalIgnoreCase))
                return (false, "", "");

            string draggedRel = RemoteClipRelativePath(draggedName);

            RemoteGalleryFile? draggedFile = allFiles?.FirstOrDefault(f =>
                string.Equals(f.Name, draggedName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(RemoteClipRelativePath(f.Name), draggedRel, StringComparison.OrdinalIgnoreCase));

            if (draggedFile is null)
                return (false, "", "");

            // Dragged file is deduplicated child (newer), this file is origin parent (older)
            if (IsCandidateDeduplicationPair(draggedFile, file))
            {
                return (true, relativePath, draggedRel);
            }

            // This file is deduplicated child (newer), dragged file is origin parent (older)
            if (IsCandidateDeduplicationPair(file, draggedFile))
            {
                return (true, draggedRel, relativePath);
            }

            return (false, "", "");
        }

        void HighlightDropTarget(bool highlight)
        {
            if (highlight)
            {
                iconHost.BorderBrush = (Brush)FindResource("Accent");
                iconHost.BorderThickness = new Thickness(2);
            }
            else
            {
                iconHost.BorderBrush = null;
                iconHost.BorderThickness = new Thickness(0);
            }
        }

        void HandleDragOver(DragEventArgs e)
        {
            if (TryGetDraggedClip(e, out string dragged))
            {
                var (isEligible, _, _) = ResolveMergePair(dragged);
                if (isEligible)
                {
                    e.Effects = DragDropEffects.Move | DragDropEffects.Copy;
                    e.Handled = true;
                    HighlightDropTarget(true);
                    return;
                }
            }
            e.Effects = DragDropEffects.None;
            HighlightDropTarget(false);
        }

        void HandleDrop(DragEventArgs e)
        {
            HighlightDropTarget(false);
            if (TryGetDraggedClip(e, out string dragged))
            {
                var (isEligible, originRel, dedupRel) = ResolveMergePair(dragged);
                if (isEligible && !string.IsNullOrEmpty(originRel) && !string.IsNullOrEmpty(dedupRel))
                {
                    e.Handled = true;
                    _ = MergeRemoteDeduplicatedClipsAsync(originRel, dedupRel);
                }
            }
        }

        iconHost.AllowDrop = true;
        iconHost.DragEnter += (_, e) => HandleDragOver(e);
        iconHost.DragOver += (_, e) => HandleDragOver(e);
        iconHost.DragLeave += (_, _) => HighlightDropTarget(false);
        iconHost.Drop += (_, e) => HandleDrop(e);

        Point? dragStart = null;
        bool isDragging = false;
        ImageSource? dragThumbSource = null;

        iconHost.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragStart = e.GetPosition(null);
            isDragging = false;
            dragThumbSource = thumbImage.Source;
        };

        iconHost.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && dragStart.HasValue && !isDragging)
            {
                Point currentPos = e.GetPosition(null);
                Vector diff = dragStart.Value - currentPos;
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    isDragging = true;
                    try
                    {
                        string dragLabel = Path.GetFileNameWithoutExtension(file.Name);
                        ShellDragHelper.DoFileDragDrop(iconHost, new[] { relativePath }, dragThumbSource, dragLabel);
                    }
                    finally
                    {
                        isDragging = false;
                        dragStart = null;
                        HighlightDropTarget(false);
                    }
                }
            }
        };

        iconHost.PreviewMouseLeftButtonUp += (_, _) =>
        {
            dragStart = null;
        };

        iconHost.MouseLeftButtonUp += (_, _) =>
        {
            if (isDragging) return;
            OpenRemoteClipStreaming(relativePath, file);
        };

        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                BeginRenameRemote(card, title, relativePath, file);
        };

        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var openFolderItem = new MenuItem { Header = "Open file location", Style = (Style)FindResource("DarkMenuItem") };
        openFolderItem.Click += (_, _) => _ = OpenRemoteClipFileLocationAsync(relativePath, file);
        var copyPathItem = new MenuItem { Header = "Copy path", Style = (Style)FindResource("DarkMenuItem") };
        copyPathItem.Click += (_, _) => _ = CopyRemoteClipPathAsync(relativePath, file);
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem"), Foreground = (Brush)FindResource("Rec") };
        deleteItem.Click += (_, _) => DeleteRemoteClip(relativePath, file);
        contextMenu.Items.Add(openFolderItem);
        contextMenu.Items.Add(copyPathItem);
        contextMenu.Items.Add(deleteItem);
        iconHost.ContextMenu = contextMenu;

        return card;
    }

    private void BeginRenameRemote(Border card, TextBlock title, string relativePath, RemoteGalleryFile file)
    {
        if (title.Parent is not Panel parent)
            return;
        int index = parent.Children.IndexOf(title);
        if (index < 0)
            return;

        _isRenamingCard = true;
        bool finished = false;

        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            Margin = title.Margin,
            Background = (Brush)FindResource("RowBg"),
            Foreground = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(0),
        };

        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, box);

        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (!finished) { finished = true; _ = CommitRenameAsync(); } }
            else if (e.Key == Key.Escape) { e.Handled = true; if (!finished) { finished = true; _isRenamingCard = false; LoadGallery(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; _ = CommitRenameAsync(); } };

        async Task CommitRenameAsync()
        {
            _isRenamingCard = false;
            string newName = box.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != Path.GetFileNameWithoutExtension(file.Name))
            {
                (bool success, string? error, _) = await _pairing.RenameRemoteClipAsync(relativePath, newName);
                if (!success)
                    MessageBox.Show(this, $"Couldn't rename: {error}", "Backtrack");
            }
            LoadGallery();
        }
    }
}
