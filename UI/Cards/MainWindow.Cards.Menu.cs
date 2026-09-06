using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Backtrack.Core;
using Backtrack.Interop;

namespace Backtrack;

public partial class MainWindow : Window
{
    private void AttachClipCardDragDrop(Border thumb, Image thumbImage, FileInfo file, Action onClick)
    {
        thumb.AllowDrop = true;
        thumb.DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1)
                {
                    string dragged = files[0];
                    if (!string.Equals(dragged, file.FullName, StringComparison.OrdinalIgnoreCase) &&
                        DeduplicationService.Instance.IsDeduplicated(dragged, out var dedupEntry) &&
                        (string.Equals(dedupEntry?.OriginClipPath, file.FullName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(dedupEntry?.OriginClipFileName, file.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        e.Effects = DragDropEffects.Move | DragDropEffects.Copy;
                        thumb.BorderBrush = (Brush)FindResource("Accent");
                        thumb.BorderThickness = new Thickness(2);
                        e.Handled = true;
                        return;
                    }
                }
            }
            e.Effects = DragDropEffects.None;
        };

        thumb.DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1)
                {
                    string dragged = files[0];
                    if (!string.Equals(dragged, file.FullName, StringComparison.OrdinalIgnoreCase) &&
                        DeduplicationService.Instance.IsDeduplicated(dragged, out var dedupEntry) &&
                        (string.Equals(dedupEntry?.OriginClipPath, file.FullName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(dedupEntry?.OriginClipFileName, file.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        e.Effects = DragDropEffects.Move | DragDropEffects.Copy;
                        e.Handled = true;
                        return;
                    }
                }
            }
            e.Effects = DragDropEffects.None;
        };

        thumb.DragLeave += (_, e) =>
        {
            thumb.BorderBrush = null;
            thumb.BorderThickness = new Thickness(0);
        };

        thumb.Drop += (_, e) =>
        {
            thumb.BorderBrush = null;
            thumb.BorderThickness = new Thickness(0);
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1)
                {
                    string dragged = files[0];
                    if (!string.Equals(dragged, file.FullName, StringComparison.OrdinalIgnoreCase) &&
                        DeduplicationService.Instance.IsDeduplicated(dragged, out var dedupEntry) &&
                        (string.Equals(dedupEntry?.OriginClipPath, file.FullName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(dedupEntry?.OriginClipFileName, file.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        e.Handled = true;
                        _ = MergeDeduplicatedClipsAsync(file, dragged);
                    }
                }
            }
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

        thumb.MouseLeftButtonUp += (_, _) =>
        {
            if (isDragging) return;
            onClick();
        };
    }

    private void AttachClipCardContextMenu(Border card, Border thumb, FileInfo file)
    {
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
