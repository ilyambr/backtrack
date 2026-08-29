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
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
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
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(isStarred ? (byte)0x80 : (byte)0x40, 0x00, 0x00, 0x00)),
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
            starButton.Background = new SolidColorBrush(Color.FromArgb(nowStarred ? (byte)0x80 : (byte)0x40, 0x00, 0x00, 0x00));
            if (_settings.GalleryStarredOnly)
                LoadGallery();
        };

        var thumbHost = new Grid();
        thumbHost.Children.Add(thumbImage);
        thumbHost.Children.Add(selectCircle);
        thumbHost.Children.Add(starButton);

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
        thumbHost.Children.Add(compressOverlay);

        if (_activeCompressingClips.TryGetValue(file.FullName, out double initProg) ||
            _activeCompressingClips.TryGetValue(file.Name, out initProg))
        {
            compressOverlay.Visibility = Visibility.Visible;
            compressText.Text = $"Compressing {(int)Math.Round(initProg * 100)}%";
            progressBarFill.Width = Math.Max(2, initProg * 140.0);
        }

        thumb.Child = thumbHost;

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
                    progressBarFill.Width = Math.Max(2, prog * 140.0);
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
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        LoadGallery();
                });
            }
        };
        ClipCompressionCompleted += completeHandler;

        thumb.Unloaded += (_, _) =>
        {
            ClipCompressionProgressChanged -= progressHandler;
            ClipCompressionCompleted -= completeHandler;
        };

        Point? dragStart = null;
        bool isDragging = false;
        ImageSource? dragThumbSource = null;

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
                        string[] files = _selectedClipPaths.Count > 0 && _selectedClipPaths.Contains(file.FullName)
                            ? _selectedClipPaths.ToArray()
                            : new[] { file.FullName };

                        string dragLabel = files.Length > 1
                            ? $"{Path.GetFileNameWithoutExtension(file.Name)} +{files.Length - 1} more"
                            : Path.GetFileNameWithoutExtension(file.Name);

                        try
                        {
                            ShellDragHelper.DoFileDragDrop(thumb, files, dragThumbSource, dragLabel);
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

        thumb.MouseLeftButtonUp += (_, _) =>
        {
            if (isDragging) return;
            if (_selectedClipPaths.Count > 0)
                ToggleClipSelected(file);
            else
                OpenInPlayer(file);
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

        UIElement titleRow = isNewest ? WithNewestDot(title, "Newest clip") : title;
        var content = new StackPanel();
        content.Children.Add(thumb);
        content.Children.Add(titleRow);
        content.Children.Add(subRow);

        _ = LoadThumbnailAsync(file, thumbImage, knownDurationMs is null ? durationText : null);

        
        
        
        
        var card = new Border { Width = 210, Child = content };

        
        
        
        
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

        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var openFolderItem = new MenuItem { Header = "Open file location", Style = (Style)FindResource("DarkMenuItem") };
        openFolderItem.Click += (_, _) => RevealInExplorerAndClose(file.FullName);
        var copyPathItem = new MenuItem { Header = "Copy path", Style = (Style)FindResource("DarkMenuItem") };
        copyPathItem.Click += (_, _) => Clipboard.SetText(file.FullName);
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem"), Foreground = (Brush)FindResource("Rec") };
        deleteItem.Click += (_, _) => DeleteClip(file, card);
        contextMenu.Items.Add(openFolderItem);
        contextMenu.Items.Add(copyPathItem);
        contextMenu.Items.Add(deleteItem);
        thumb.ContextMenu = contextMenu;

        return card;
    }


    private void BeginRename(Border card, TextBlock title, FileInfo file)
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
            if (e.Key == Key.Enter) { if (!finished) { finished = true; CommitRename(); } }
            else if (e.Key == Key.Escape) { e.Handled = true; if (!finished) { finished = true; _isRenamingCard = false; LoadGallery(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; CommitRename(); } };

        void CommitRename()
        {
            _isRenamingCard = false;
            string newName = box.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != Path.GetFileNameWithoutExtension(file.Name))
            {
                try
                {
                    string newPath = Path.Combine(file.DirectoryName!, newName + file.Extension);
                    File.Move(file.FullName, newPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Couldn't rename: {ex.Message}", "Backtrack");
                }
            }
            LoadGallery();
        }
    }
}
