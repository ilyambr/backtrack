using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
using Microsoft.Win32;

namespace Backtrack;

public partial class MainWindow : Window
{
    private void ToggleClipSelected(FileInfo file)
    {
        if (!_selectedClipPaths.Add(file.FullName))
            _selectedClipPaths.Remove(file.FullName);
        RefreshGallerySelectionUi();
    }

    private void RefreshGallerySelectionUi()
    {
        int count = _selectedClipPaths.Count;
        GallerySelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GallerySelectionCountText.Text = count == 1 ? "1 selected" : $"{count} selected";

        foreach (var (file, circle, thumb) in _galleryCardSelection)
        {
            bool selected = _selectedClipPaths.Contains(file.FullName);
            circle.Background = selected ? (Brush)FindResource("Green") : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            circle.BorderBrush = selected ? (Brush)FindResource("Green") : (Brush)FindResource("Text0");

            if (count > 0)
                circle.Visibility = Visibility.Visible;
            else if (!thumb.IsMouseOver)
                circle.Visibility = Visibility.Collapsed;
        }
    }

    internal void CancelSelection_Click(object sender, RoutedEventArgs e)
    {
        _selectedClipPaths.Clear();
        RefreshGallerySelectionUi();
    }

    internal void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        List<FileInfo> targets = _galleryCardSelection
            .Where(c => _selectedClipPaths.Contains(c.File.FullName))
            .Select(c => c.File)
            .ToList();
        if (targets.Count == 0)
            return;

        string message = targets.Count == 1
            ? $"Are you sure you want to delete \"{targets[0].Name}\"? This will send it to your recycle bin."
            : $"Are you sure you want to delete {targets.Count} clips? This will send them to your recycle bin.";

        ShowConfirmDialog(message, "Delete", confirmed =>
        {
            if (confirmed)
            {
                _selectedClipPaths.Clear();
                if (targets.Count == 1)
                    QueueDeleteWithUndo(targets[0]);
                else
                    QueueMultiDeleteWithUndo(targets);
            }
        });
    }

    internal void MoveClipsToFolder(IEnumerable<string> filePaths, string destination)
    {
        if (!Directory.Exists(destination))
            Directory.CreateDirectory(destination);

        string destFull = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int moved = 0;
        foreach (string path in filePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    string fileName = Path.GetFileName(path);
                    string dest = Path.Combine(destFull, fileName);
                    if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (File.Exists(dest))
                        dest = Path.Combine(destFull, $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now:HHmmss}{Path.GetExtension(fileName)}");

                    File.Move(path, dest);
                    moved++;
                }
                else if (Directory.Exists(path))
                {
                    string dirName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    string dest = Path.Combine(destFull, dirName);
                    string pathFull = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(pathFull, destFull, StringComparison.OrdinalIgnoreCase) || destFull.StartsWith(pathFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (Directory.Exists(dest))
                        dest = Path.Combine(destFull, $"{dirName}_{DateTime.Now:HHmmss}");

                    Directory.Move(path, dest);
                    moved++;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"Failed to move \"{path}\" to \"{destination}\": {ex.Message}");
            }
        }

        if (moved > 0)
        {
            _selectedClipPaths.Clear();
            LoadGallery();
        }
    }

    internal async void MoveRemoteClipsToFolder(IEnumerable<string> remotePaths, string? destinationFolder)
    {
        string dest = destinationFolder ?? "";
        (bool success, string? error) = await _pairing.MoveRemoteClipsAsync(remotePaths, dest);
        if (!success)
        {
            MessageBox.Show(this, $"Couldn't move clips: {error}", "Backtrack");
        }
        _selectedClipPaths.Clear();
        await LoadRemoteGalleryAsync();
    }

    internal void MoveSelected_Click(object sender, RoutedEventArgs e)
    {
        List<FileInfo> targets = _galleryCardSelection
            .Where(c => _selectedClipPaths.Contains(c.File.FullName))
            .Select(c => c.File)
            .ToList();
        if (targets.Count == 0)
            return;

        var dialog = new OpenFolderDialog { InitialDirectory = GalleryFolder };
        if (dialog.ShowDialog(this) != true)
            return;

        string destination = dialog.FolderName;
        MoveClipsToFolder(targets.Select(t => t.FullName), destination);
    }

    private void DeleteClip(FileInfo file, Border card)
    {
        ShowConfirmDialog(
            $"Are you sure you want to delete \"{file.Name}\"? This will send it to your recycle bin.",
            "Delete",
            confirmed =>
            {
                if (confirmed)
                {
                    QueueDeleteWithUndo(file);
                }
            });
    }
}
